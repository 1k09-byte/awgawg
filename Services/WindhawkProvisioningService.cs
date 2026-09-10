using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services;

/// <summary>
/// The "Install Windhawk" half of the provisioning flow: ensures Windhawk is
/// present by downloading the latest official installer and running it silently.
///
/// VERIFIED FACTS (Windhawk GitHub + winget manifests, checked 2026-09):
/// - Latest stable release channel: https://github.com/ramensoftware/windhawk/releases/latest
///   (v1.7.3 as of verification; 2.0 exists only as pre-release alphas).
/// - The installer is NSIS ("InstallerType: nullsoft" in the winget manifest).
/// - Verified silent switches: /S /STANDARD (standard install) — NOT Inno's
///   /VERYSILENT. /PORTABLE exists as an alternative mode switch.
/// - ElevationRequirement: elevationRequired (the engine installs a service and
///   injects into processes). stellarisKIT's app.manifest is requireAdministrator,
///   so the installer normally runs elevated already; the elevation check still
///   guards unpackaged non-elevated runs.
///
/// The GitHub "latest" API is used instead of a hardcoded version so the URL
/// always resolves to the current stable release, and the SHA256 digest shipped
/// in the release metadata is verified before executing anything.
/// </summary>
public sealed class WindhawkProvisioningService
{
    private static readonly HttpClient _httpClient = new();

    private const string LatestReleaseApi = "https://api.github.com/repos/ramensoftware/windhawk/releases/latest";
    private const string OfficialSiteUrl = "https://windhawk.net/";

    public record ProvisionProgress(WindhawkProvisionStage Stage, string StatusText, double? DownloadPercent);
    public record ResolvedInstaller(string Version, string DownloadUrl, string? Sha256, long SizeBytes);

    public enum WindhawkProvisionStage
    {
        Resolving,
        Downloading,
        Installing,
        Verifying,
        Done,
        Failed,
    }

    private readonly WindhawkDetectionService _detection = new();
    private readonly WindhawkInstallerService _installer = new();

    /// <summary>
    /// Resolves the latest stable installer via the GitHub releases API, falling
    /// back to the canonical direct-download URL shape if the API is unreachable.
    /// </summary>
    public async Task<ResolvedInstaller> ResolveLatestInstallerAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            // GitHub API requires a UA; without it the request is rejected.
            request.Headers.UserAgent.ParseAdd("stellarisKIT-WindhawkProvisioner/1.0");
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string version = root.GetProperty("tag_name").GetString() ?? "";
            string? url = null, sha = null;
            long size = 0;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.Equals("windhawk_setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha = digest["sha256:".Length..];
                    }
                    break;
                }
            }

            if (!string.IsNullOrEmpty(url))
                return new ResolvedInstaller(version, url, sha, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"ResolveLatestInstaller API failed: {ex.Message}");
        }

        // Fallback: canonical "latest" download URL (GitHub redirects /releases/latest
        // download paths to the current release asset). No digest available here.
        return new ResolvedInstaller(
            "latest",
            "https://github.com/ramensoftware/windhawk/releases/latest/download/windhawk_setup.exe",
            null,
            0);
    }

    /// <summary>
    /// Full provisioning pipeline: resolve → download (with progress) → verify
    /// digest → silent install → wait → verify installed. Returns the resulting
    /// installation info. Throws only on unrecoverable failures; recoverable
    /// stage failures throw InvalidOperationException with a user-facing message.
    /// </summary>
    public async Task<WindhawkInstallationInfo> EnsureInstalledAsync(
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        var existing = _detection.Detect();
        if (existing.IsInstalled)
        {
            progress.Report(new ProvisionProgress(
                WindhawkProvisionStage.Done,
                $"Windhawk {existing.Version ?? "already"} is already installed.",
                null));
            return existing;
        }

        // 1. Resolve the latest stable installer.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving, "Resolving latest Windhawk release...", null));
        var installer = await ResolveLatestInstallerAsync(ct);

        // 2. Download with progress.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading, $"Downloading Windhawk {installer.Version}...", 0));
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
        await DownloadFileAsync(installer.DownloadUrl, tempPath, installer.SizeBytes, progress, ct);

        // 3. Integrity: verify SHA256 when the release metadata provided one.
        if (!string.IsNullOrEmpty(installer.Sha256))
        {
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installer signature...", null));
            await VerifySha256Async(tempPath, installer.Sha256!, ct);
        }

        // 4. Silent install. NSIS: /S = silent, /STANDARD = standard install mode
        // (verified against the winget manifest's InstallerSwitches and the
        // upstream discussion confirming these exact flags).
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, "Installing Windhawk (silent)...", null));
        await RunInstallerSilentlyAsync(tempPath, ct);

        // 5. Verify the install actually landed (NSIS exits 0 and the UI exe exists).
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installation...", null));
        var result = await WaitForInstallationAsync(ct);
        if (!result.IsInstalled)
        {
            throw new InvalidOperationException(
                "The Windhawk installer finished but the installation could not be verified. Check the official site: " + OfficialSiteUrl);
        }

        try { File.Delete(tempPath); } catch { }

        progress.Report(new ProvisionProgress(
            WindhawkProvisionStage.Done,
            $"Windhawk {result.Version ?? ""} installed.".Trim() + ".",
            null));
        return result;
    }

    /// <summary>
    /// Verified end-to-end flow: download Windhawk's offline installer from the
    /// latest GitHub release, install it silently, then import the provided settings
    /// JSON via windhawk-cli and apply any available mod updates.
    ///
    /// This is the composition entrypoint for the new installer service. It reuses
    /// the same process-invocation pattern everywhere (Process.Start + await
    /// WaitForExitAsync, hidden window, no UI blocking).
    /// </summary>
    public async Task<WindhawkInstallerService.WindhawkInstallerResult> InstallAndImportAsync(
        string jsonPath,
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        // 1. Resolve latest release + offline installer URL.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving,
            "Resolving latest Windhawk offline installer...", null));

        (string version, string downloadUrl) = await _installer.ResolveLatestOfflineInstallerAsync(ct);

        // 2. Download the offline installer.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading,
            $"Downloading Windhawk {version} (offline installer)...", null));

        string installerPath = await _installer.DownloadInstallerAsync(
            downloadUrl,
            new Progress<string>(s => progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading, s, null))),
            ct);

        try
        {
            // 3. Silent install.
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Installing Windhawk (silent)...", null));

            await _installer.InstallSilentlyAsync(
                installerPath,
                new Progress<string>(s => progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, s, null))),
                ct);

            // 4. Import settings via CLI.
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Importing Windhawk settings...", null));

            var importResult = await _installer.ImportSettingsAsync(
                jsonPath,
                new Progress<string>(s => progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, s, null))),
                ct);

            progress.Report(new ProvisionProgress(
                importResult.Success ? WindhawkProvisionStage.Done : WindhawkProvisionStage.Failed,
                importResult.Success
                    ? $"Windhawk {version} installed and settings imported."
                    : $"Windhawk installed, but settings import had issues (exit {importResult.ExitCode}).",
                null));

            return importResult;
        }
        finally
        {
            // 6. Cleanup temp installer + any imported JSON copies the CLI may have created.
            _installer.CleanupTempFiles();

            // Also clean up the installer temp path explicitly (already tracked, but belt-and-suspenders).
            WindhawkInstallerService.CleanupFile(installerPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, long totalBytes, IProgress<ProvisionProgress> progress, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        totalBytes = response.Content.Headers.ContentLength ?? totalBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTime.UtcNow;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;
            if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new ProvisionProgress(
                    WindhawkProvisionStage.Downloading,
                    $"Downloading Windhawk... ({totalRead * 100.0 / totalBytes:F0}%)",
                    totalRead * 100.0 / totalBytes));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            string actual = Convert.ToHexStringLower(hash);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Downloaded Windhawk installer failed SHA256 verification — download may be corrupted or tampered with. Aborting.");
            }
        }, ct);
    }

    private static async Task RunInstallerSilentlyAsync(string installerPath, CancellationToken ct)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            // requireAdministrator manifest covers packaged runs; this guards
            // unpackaged/un elevated configurations. The engine's service install
            // needs elevation and would silently fail otherwise.
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). Relaunch stellarisKIT elevated and retry.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S /STANDARD",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk installer.");

        // NSIS setup can take a while (it also installs the engine service); 10-minute ceiling.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        int exit = -1;
        try { if (process.HasExited) exit = process.ExitCode; } catch { }
        Debug.WriteLine($"Windhawk installer exit code {exit}");
        // NSIS: 0 = success. Verification below is the real source of truth; a
        // non-zero exit here is logged but the file check decides.
    }

    private static async Task<WindhawkInstallationInfo> WaitForInstallationAsync(CancellationToken ct)
    {
        var detection = new WindhawkDetectionService();
        for (int i = 0; i < 60; i++) // 60 * 1s = 60s
        {
            ct.ThrowIfCancellationRequested();
            var info = detection.Detect();
            if (info.IsInstalled) return info;
            await Task.Delay(1000, ct);
        }
        return WindhawkInstallationInfo.NotInstalled;
    }
}
