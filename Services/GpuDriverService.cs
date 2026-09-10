using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    public sealed record DetectedGpu(string Name, string Vendor, string DriverVersion);

    public class GpuDriverService
    {
        private static readonly HttpClient _httpClient = new();

        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        // NVIDIA Lookup API series pairs (psid, pfid). Any valid desktop series
        // returns the current Game Ready driver; tried in order until one succeeds.
        private static readonly (int Psid, int Pfid)[] NvidiaSeriesQueries = new[]
        {
            (120, 929),   // RTX 30 desktop
            (127, 1039),  // RTX 40 desktop
            (112, 895),   // GTX 16 desktop
            (107, 879),   // RTX 20 desktop
        };

        /// <summary>
        /// Enumerates display adapters from the driver store class key (no extra
        /// dependencies: plain Microsoft.Win32 registry reads, like the uninstall scan).
        /// </summary>
        public Task<List<DetectedGpu>> DetectGpusAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var result = new List<DetectedGpu>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                    if (baseKey is null) return result;
                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                        using var key = baseKey.OpenSubKey(sub);
                        if (key is null) continue;
                        string name = (key.GetValue("DriverDesc") as string ?? "").Trim();
                        if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                        string version = (key.GetValue("DriverVersion") as string ?? "").Trim();
                        string matchId = (key.GetValue("MatchingDeviceId") as string ?? "").Trim();
                        string vendor = DetectVendor(name, matchId);
                        // Only real GPU vendors: drops virtual adapters (Hyper-V,
                        // VMware, VirtualBox, Remote Display) and generic
                        // "Microsoft Basic Display Adapter" entries.
                        if (!vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
                            !vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase) &&
                            !vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase))
                            continue;
                        result.Add(new DetectedGpu(name, vendor, version));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DetectGpus: {ex.Message}");
                }
                return result;
            }, ct);
        }

        private static string DetectVendor(string name, string matchId)
        {
            string hay = name + " " + matchId;
            if (hay.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || hay.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return "NVIDIA";
            if (hay.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || hay.Contains("AMD", StringComparison.OrdinalIgnoreCase) || hay.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                return "AMD";
            if (hay.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || hay.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                return "Intel";
            return "Unknown";
        }

        /// <summary>
        /// Latest NVIDIA Game Ready (WHQL/DCH) driver via the same lookup API
        /// tools like NVCleanstall use. Returns (version, directExeUrl) or nulls
        /// when offline or when the response shape changes — callers fall back
        /// to the official download page.
        /// </summary>
        public async Task<(string? Version, string? Url)> GetLatestNvidiaDriverAsync(CancellationToken ct)
        {
            foreach (var (psid, pfid) in NvidiaSeriesQueries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string api = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    string json = await _httpClient.GetStringAsync(api, cts.Token);
                    var (version, url) = ParseNvidiaLookup(json);
                    if (!string.IsNullOrEmpty(version) && IsHttpUrl(url))
                        return (version, url);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-series HTTP timeout: same backend serves all series, stop trying.
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"NvidiaLookup {psid}/{pfid}: {ex.Message}");
                }
            }
            return (null, null);
        }

        private static (string? Version, string? Url) ParseNvidiaLookup(string json)
        {
            try
            {
                var root = JsonNode.Parse(json);
                var ids = root?["IDS"]?.AsArray();
                if (ids is null || ids.Count == 0) return (null, null);
                var first = ids[0];
                var info = first?["downloadInfo"] ?? first;
                string? version = info?["Version"]?.GetValue<string>();
                string? url = info?["DownloadURL"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    url = first?["DownloadURL"]?.GetValue<string>();
                return (version?.Trim(), url?.Trim());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ParseNvidiaLookup: {ex.Message}");
                return (null, null);
            }
        }

        private static bool IsHttpUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        public static void OpenUrl(string? url)
        {
            try
            {
                if (!IsHttpUrl(url)) return;
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenUrl: {ex.Message}");
            }
        }

        /// <summary>
        /// Full silent pipeline for NVIDIA: download with progress, run the vendor
        /// installer silent (-s -noreboot), then verify via exit code. Mirrors
        /// InstallerService.InstallBrowserAsync (elevated manifest, UseShellExecute=false).
        /// </summary>
        public async Task InstallNvidiaAsync(GpuDriverItem item, IProgress<GpuDriverStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct)
        {
            progress.Report(GpuDriverStatus.Downloading);
            downloadProgress.Report(0);
            string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
            try
            {
                using var response = await _httpClient.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                var lastReport = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                    if (totalBytes != -1 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        downloadProgress.Report((double)totalRead / totalBytes * 100.0);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Download failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            ct.ThrowIfCancellationRequested();

            progress.Report(GpuDriverStatus.Installing);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = item.SilentInstallArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    errorProgress.Report("Installer failed to start.");
                    progress.Report(GpuDriverStatus.Failed);
                    return;
                }

                // Driver packages take several minutes; 20-minute ceiling, then kill.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    errorProgress.Report("Installer timed out after 20 minutes and was terminated.");
                    progress.Report(GpuDriverStatus.Failed);
                    return;
                }

                int exit = -1;
                try { if (process.HasExited) exit = process.ExitCode; } catch { }
                Debug.WriteLine($"NVIDIA installer exit code {exit}");
                if (exit == 0)
                {
                    await RefreshInstalledVersionAsync(item, "NVIDIA", ct);
                    progress.Report(GpuDriverStatus.Installed);
                }
                else
                {
                    errorProgress.Report($"Installer exited with code {exit}. A reboot may be pending — check GeForce Experience.");
                    progress.Report(GpuDriverStatus.Failed);
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                errorProgress.Report($"Install failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            CleanUp(tempPath);
        }

        /// <summary>
        /// Guided path for vendors without reliable silent flags: download the
        /// package (when a URL is known) then hand off to the vendor UI.
        /// </summary>
        public async Task DownloadAndLaunchAsync(GpuDriverItem item, IProgress<GpuDriverStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct)
        {
            if (!IsHttpUrl(item.DownloadUrl))
            {
                OpenUrl(item.VendorPageUrl);
                progress.Report(GpuDriverStatus.ManualActionRequired);
                return;
            }

            progress.Report(GpuDriverStatus.Downloading);
            downloadProgress.Report(0);
            string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
            try
            {
                using var response = await _httpClient.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                var lastReport = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                    if (totalBytes != -1 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        downloadProgress.Report((double)totalRead / totalBytes * 100.0);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Download failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                progress.Report(GpuDriverStatus.ManualActionRequired);
            }
            catch (Exception ex)
            {
                errorProgress.Report($"Could not launch installer: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
            }
        }

        public async Task RefreshInstalledVersionAsync(GpuDriverItem item, string vendor, CancellationToken ct = default)
        {
            try
            {
                var gpus = await DetectGpusAsync(ct);
                foreach (var gpu in gpus)
                {
                    if (gpu.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(gpu.DriverVersion) &&
                        !gpu.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    {
                        item.InstalledVersion = gpu.DriverVersion;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshInstalledVersion: {ex.Message}");
            }
        }

        private static void CleanUp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
