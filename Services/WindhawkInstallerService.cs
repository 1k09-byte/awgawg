using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services;

/// <summary>
/// Verified Windhawk provisioning flow:
/// 1. Query GitHub releases API for the latest release and pick the asset whose
///    name contains windhawk_setup_offline.exe.
/// 2. Download that asset to a temp path.
/// 3. Run the NSIS installer with /S (silent), hidden window, and await exit.
/// 4. Locate windhawk-cli.exe under %ProgramFiles%\Windhawk and run:
///      data import "<json>" --confirm-app-restart --yes
///    with WorkingDirectory = the Windhawk install folder.
/// 5. Optional: for mods that have updates available, run:
///      mod list --update-available --json
///      then mod update <modId> for each returned id.
///
/// Anything beyond what the CLI import supports (extra HKLM\SOFTWARE\Windhawk
/// registry keys) requires TrustedInstaller, not just administrator, so this
/// service does not attempt raw registry writes to Engine\Mods itself.
/// </summary>
public sealed class WindhawkInstallerService
{
    private static readonly HttpClient _http = new();

    private const string ReleasesApi = "https://api.github.com/repos/ramensoftware/windhawk/releases";
    private const string OfflineInstallerNameHint = "windhawk_setup_offline.exe";

    private readonly WindhawkDetectionService _detection = new();

    // Temp files created during a run. Cleaned up in Dispose when the service is
    // short-lived, and also aggressively in CleanupTempFiles.
    private readonly List<string> _tempFilePaths = new();

    /// <summary>
    /// Currently tracked Windhawk install info after the last successful install.
    /// </summary>
    public WindhawkInstallationInfo? LastInstalledInfo { get; private set; }

    // ------------------------------------------------------------------
    // Step 1: resolve latest release + offline installer asset
    // ------------------------------------------------------------------

    /// <summary>
    /// Queries https://api.github.com/repos/ramensoftware/windhawk/releases (GET,
    /// User-Agent header), takes the first (latest) release, and finds the asset
    /// whose name contains windhawk_setup_offline.exe.
    /// </summary>
    public async Task<(string Version, string DownloadUrl)> ResolveLatestOfflineInstallerAsync(
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("stellarisKIT-WindhawkInstaller/1.0");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.GetArrayLength() == 0)
            throw new InvalidOperationException("GitHub Windhawk releases API returned no releases.");

        // First element is the latest release (GitHub returns them newest-first).
        var latest = root[0];
        string version = latest.TryGetProperty("tag_name", out var tag)
            ? tag.GetString() ?? ""
            : "";

        string? downloadUrl = null;
        foreach (var asset in latest.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains(OfflineInstallerNameHint, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException(
                $"Latest Windhawk release does not include an asset matching '{OfflineInstallerNameHint}'. " +
                "The offline installer may have been renamed or removed from the release.");

        return (version, downloadUrl);
    }

    // ------------------------------------------------------------------
    // Step 2: download to temp path
    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads the offline installer to a temp file. Returns the temp path.
    /// The returned path is tracked for cleanup.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string downloadUrl, IProgress<string>? status = null, CancellationToken ct = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup_offline.exe");

        // Avoid reusing a stale/locked temp file from a previous failed run.
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        status?.Report($"Downloading Windhawk offline installer from GitHub...");

        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[1 << 16];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
            await file.WriteAsync(buffer.AsMemory(0, read), ct);

        _tempFilePaths.Add(tempPath);
        return tempPath;
    }

    // ------------------------------------------------------------------
    // Step 3: run installer silently
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the downloaded NSIS installer with /S (silent) and hidden window style,
    /// and awaits exit. Requires elevation for the engine service install.
    /// </summary>
    public async Task InstallSilentlyAsync(
        string installerPath, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). " +
                "Relaunch stellarisKIT elevated and retry.");
        }

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Windhawk installer not found at the expected temp path.", installerPath);

        status?.Report("Installing Windhawk silently...");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start /wait \"\" \"{installerPath}\" /S /STANDARD",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk offline installer.");

        // NSIS installers can take a while (service install, file copy). 10-minute ceiling.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Windhawk installer exited with code {process.ExitCode}. " +
                "Installation may have failed; check the official site: https://windhawk.net/");
        }

        // Give the install a moment to settle before we probe for cli.exe.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    // ------------------------------------------------------------------
    // Step 4: locate cli.exe and run data import
    // ------------------------------------------------------------------

    /// <summary>
    /// Locates windhawk-cli.exe under %ProgramFiles%\Windhawk and runs the data
    /// import command. WorkingDirectory is set to the Windhawk install folder.
    /// </summary>
    public async Task<WindhawkInstallerResult> ImportSettingsAsync(
        string jsonPath,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var info = _detection.Detect();
        if (!info.IsInstalled || string.IsNullOrEmpty(info.InstallDirectory))
            throw new InvalidOperationException(
                "Windhawk does not appear to be installed. Install it first before importing settings.");

        string cliPath = info.CliPath
            ?? Path.Combine(info.InstallDirectory, "windhawk-cli.exe");

        if (!File.Exists(cliPath))
        {
            // Fall back to the explicit Program Files path requested in the spec.
            string pfCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk", "windhawk-cli.exe");
            if (File.Exists(pfCli))
                cliPath = pfCli;
        }

        if (!File.Exists(cliPath))
            throw new InvalidOperationException(
                "windhawk-cli.exe not found in the Windhawk install directory. " +
                "The CLI is required to import settings; ensure Windhawk 2.0+ is installed.");

        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Settings JSON file not found.", jsonPath);

        status?.Report("Importing Windhawk settings via windhawk-cli...");

        var importResult = await RunCliCommandAsync(
            cliPath,
            $"data import \"{jsonPath}\" --confirm-app-restart --yes",
            info.InstallDirectory,
            status,
            ct);

        // Parse the machine-readable summary if the CLI emitted one.
        var parsed = ParseImportSummary(importResult.Stdout);
        if (parsed is not null)
        {
            status?.Report(parsed);
        }
        else if (!string.IsNullOrWhiteSpace(importResult.Stderr))
        {
            status?.Report($"Import completed with stderr: {importResult.Stderr.Trim()}");
        }
        else
        {
            status?.Report("Settings imported via windhawk-cli.");
        }

        LastInstalledInfo = _detection.Detect();

        // Optional step 5: update any mods that have updates available.
        var updateResult = await UpdateModsIfAvailableAsync(cliPath, info.InstallDirectory, status, ct);
        if (updateResult.HasUpdates)
        {
            status?.Report($"Updated {updateResult.UpdatedIds.Count} mod(s) to the latest version.");
        }
        else if (updateResult.CheckedCount > 0)
        {
            status?.Report("All installed mods are up to date.");
        }

        return new WindhawkInstallerResult(
            Success: importResult.ExitCode == 0 || importResult.ExitCode == 7,
            ExitCode: importResult.ExitCode,
            Stdout: importResult.Stdout,
            Stderr: importResult.Stderr,
            ModsUpdated: updateResult.UpdatedIds.ToList());
    }

    // ------------------------------------------------------------------
    // Step 5: mod update (optional)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs "mod list --update-available --json", and for each returned modId runs
    /// "mod update <modId>". Returns the list of updated ids.
    /// </summary>
    public async Task<ModUpdateResult> UpdateModsIfAvailableAsync(
        string cliPath,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var result = new ModUpdateResult();

        status?.Report("Checking for mod updates...");

        var listResult = await RunCliCommandAsync(
            cliPath,
            "mod list --update-available --json",
            workingDirectory,
            status,
            ct);

        if (listResult.ExitCode != 0)
        {
            status?.Report($"Mod update check failed (exit {listResult.ExitCode}): {listResult.Stderr.Trim()}");
            return result;
        }

        var updateable = ParseUpdateAvailableList(listResult.Stdout);
        result.CheckedCount = updateable.Count;

        if (updateable.Count == 0)
            return result;

        foreach (var modId in updateable)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"Updating mod {modId}...");

            var updateResult = await RunCliCommandAsync(
                cliPath,
                $"mod update {modId}",
                workingDirectory,
                status,
                ct);

            if (updateResult.ExitCode == 0)
                result.UpdatedIds.Add(modId);
            else
                status?.Report($"Update failed for {modId} (exit {updateResult.ExitCode}): {updateResult.Stderr.Trim()}");
        }

        result.HasUpdates = result.UpdatedIds.Count > 0;
        return result;
    }

    // ------------------------------------------------------------------
    // Process invocation helper (same pattern as the rest of the service)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a windhawk-cli subcommand and captures stdout/stderr. WorkingDirectory
    /// is set to the Windhawk install folder so the CLI can find its engine/data
    /// paths.
    /// </summary>
    private static async Task<CliRunResult> RunCliCommandAsync(
        string cliPath,
        string arguments,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start windhawk-cli: {cliPath}");

        // Read both streams concurrently so a bloated stdout doesn't deadlock the stderr pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("windhawk-cli command timed out after 5 minutes.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        Debug.WriteLine($"windhawk-cli [{arguments}] exit {process.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine($"windhawk-cli stderr: {stderr}");

        return new CliRunResult(process.ExitCode, stdout, stderr);
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses the --json output of "data import". Expected shape:
    /// { "summary": { "mods": [ { "modId", "status", "message" } ... ] } }
    /// Returns a human-readable summary string when parsing succeeds.
    /// </summary>
    private static string? ParseImportSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement? modsElement = null;
            // Support both { "summary": { "mods": [...] } } and { "data": { "summary": { "mods": [...] } } }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("summary", out var s) && s.TryGetProperty("mods", out var m))
                {
                    modsElement = m;
                }
                else if (root.TryGetProperty("data", out var data) && data.TryGetProperty("summary", out var s2) && s2.TryGetProperty("mods", out var m2))
                {
                    modsElement = m2;
                }
                else
                {
                    // Fallback: try to find a nested "mods" property anywhere at top level
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("mods", out var m3))
                        {
                            modsElement = m3;
                            break;
                        }
                    }
                }
            }

            if (modsElement is null || modsElement.Value.ValueKind != JsonValueKind.Array)
                return null;

            int imported = 0, failed = 0;
            var failures = new List<string>();

            foreach (var mod in modsElement.Value.EnumerateArray())
            {
                string modId = mod.TryGetProperty("modId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
                string status = mod.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                bool ok = status.Equals("installed", StringComparison.OrdinalIgnoreCase);
                if (ok) imported++;
                else
                {
                    failed++;
                    string msg = mod.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? status
                        : status;
                    if (!string.IsNullOrEmpty(modId))
                        failures.Add($"{modId}: {msg}");
                }
            }

            var parts = new List<string> { $"CLI import: {imported} mod(s) installed" };
            if (failed > 0)
                parts.Add($"{failed} failed: " + string.Join("; ", failures));

            return string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParseImportSummary failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the stdout of "mod list --update-available --json". Expected shape is
    /// a JSON array of mod ids, or an object with an ids/advisories field. This is
    /// lenient: it collects any string entries that look like mod ids.
    /// </summary>
    private static List<string> ParseUpdateAvailableList(string json)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return ids;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // If it's a bare array of strings, use it directly.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        ids.Add(el.GetString()!);
                return ids;
            }

            // Otherwise look for common shapes: { "ids": [...] }, { "updateAvailable": [...] },
            // or flatten every string property value that looks like a mod id.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in prop.Value.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                            ids.Add(el.GetString()!);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParseUpdateAvailableList failed: {ex.Message}");
        }

        return ids;
    }

    // ------------------------------------------------------------------
    // Cleanup
    // ------------------------------------------------------------------

    /// <summary>
    /// Removes temp files created during install/import. If a file is still locked,
    /// attempts to kill the locking process first, then retries deletion.
    /// </summary>
    public void CleanupTempFiles()
    {
        foreach (var path in _tempFilePaths.ToList())
        {
            TryDeleteWithForce(path);
        }

        _tempFilePaths.Clear();
    }

    /// <summary>
    /// Explicitly clean up a specific temp file (installer .exe or imported .json).
    /// </summary>
    public static void CleanupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        TryDeleteWithForce(path);
    }

    private static void TryDeleteWithForce(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                break;
            }
            catch (IOException)
            {
                // File locked — try to find and kill the locking process, then retry.
                var locker = FindLockedProcess(path);
                if (locker is not null && locker.Id != Environment.ProcessId)
                {
                    try
                    {
                        locker.Kill(entireProcessTree: true);
                        Task.Delay(500).Wait();
                    }
                    catch { }
                }

                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    break;
                }
                catch
                {
                    // Last attempt failed; leave the file for the next cleanup sweep.
                }
            }
        }
    }

    private static Process? FindLockedProcess(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fileName)))
            {
                try
                {
                    if (proc.MainModule?.FileName.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
                        return proc;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    // ------------------------------------------------------------------
    // Records
    // ------------------------------------------------------------------

    public record CliRunResult(int ExitCode, string Stdout, string Stderr);

    public record WindhawkInstallerResult(
        bool Success,
        int ExitCode,
        string Stdout,
        string Stderr,
        List<string> ModsUpdated);

    public sealed class ModUpdateResult
    {
        public bool HasUpdates { get; set; }
        public int CheckedCount { get; set; }
        public List<string> UpdatedIds { get; } = new();
    }

    public void Dispose()
    {
        CleanupTempFiles();
    }
}
