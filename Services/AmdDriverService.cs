using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    public class AmdDriverPackageConfig
    {
        public string ProductName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string PackageType { get; set; } = string.Empty; // "ptype" from manifest
        public bool IsSelected { get; set; } = true;
        
        // Internal references to map back to JSON array nodes
        internal JsonNode? ManifestNode { get; set; }
    }

    public class AmdDriverService
    {
        private static readonly HttpClient _httpClient = new();

        /// <summary>
        /// Downloads the lightweight 7-Zip standalone executable if not present.
        /// </summary>
        private async Task<string> Ensure7ZipAsync(CancellationToken ct)
        {
            try
            {
                string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stellarisKIT", "Tools");
                Directory.CreateDirectory(toolsDir);
                string zPath = Path.Combine(toolsDir, "7za.exe");
                
                if (File.Exists(zPath)) return zPath;

                // Download 7za.exe from an official or trusted fast CDN
                string url = "https://www.7-zip.org/a/7zr.exe"; 
                byte[] bytes = await _httpClient.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(zPath, bytes, ct);
                
                return zPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ensure7ZipAsync failed: {ex.Message}");
                // Fallback to expecting system path
                return "7z.exe"; 
            }
        }

        public async Task<bool> ExtractInstallerAsync(string installerExePath, string extractDir, Action<string> logCallback, CancellationToken ct)
        {
            try
            {
                string zExe = await Ensure7ZipAsync(ct);
                
                // 7z.exe x "<installer.exe>" -o"<extractDir>" -y
                string args = $"x \"{installerExePath}\" -o\"{extractDir}\" -y";
                logCallback($"Extracting AMD package: {zExe} {args}");
                
                var psi = new ProcessStartInfo
                {
                    FileName = zExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                logCallback($"Extraction failed: {ex.Message}");
                return false;
            }
        }

        public async Task<List<AmdDriverPackageConfig>> GetPackagesFromManifestAsync(string extractDir, Action<string> logCallback)
        {
            var packages = new List<AmdDriverPackageConfig>();
            string manifestPath = Path.Combine(extractDir, "Bin64", "cccmanifest_64.json");
            
            if (!File.Exists(manifestPath))
            {
                logCallback($"Manifest not found at: {manifestPath}");
                return packages;
            }

            try
            {
                string json = await File.ReadAllTextAsync(manifestPath);
                var root = JsonNode.Parse(json);
                var pkgArray = root?["Packages"]?["Package"]?.AsArray();
                
                if (pkgArray != null)
                {
                    foreach (var node in pkgArray)
                    {
                        var info = node?["Info"];
                        if (info == null) continue;

                        string pname = info["productName"]?.GetValue<string>() ?? "";
                        string desc = info["Description"]?.GetValue<string>() ?? "";
                        string ptype = info["ptype"]?.GetValue<string>() ?? "";

                        packages.Add(new AmdDriverPackageConfig
                        {
                            ProductName = pname,
                            Description = desc,
                            PackageType = ptype,
                            ManifestNode = node
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback($"Failed to parse AMD manifest: {ex.Message}");
            }
            
            return packages;
        }

        public async Task<bool> CustomizeManifestsAsync(string extractDir, List<AmdDriverPackageConfig> packages, Action<string> logCallback)
        {
            try
            {
                string binManifest = Path.Combine(extractDir, "Bin64", "cccmanifest_64.json");
                string configManifest = Path.Combine(extractDir, "Config", "InstallManifest.json");

                await PruneManifestAsync(binManifest, packages, logCallback);
                await PruneManifestAsync(configManifest, packages, logCallback);

                // Handle specific INF removals for Display Drivers
                var displayDriver = packages.FirstOrDefault(p => p.ProductName.Contains("Display", StringComparison.OrdinalIgnoreCase) && !p.IsSelected);
                if (displayDriver != null)
                {
                    string infDir = Path.Combine(extractDir, "Packages", "Drivers", "Display", "WT6A_INF");
                    if (Directory.Exists(infDir))
                    {
                        // Exclude the sub-folder
                        string backupDir = Path.Combine(extractDir, "Packages", "Drivers", "Display", "WT6A_INF_Backup");
                        Directory.CreateDirectory(backupDir);
                        
                        foreach (var dir in Directory.GetDirectories(infDir))
                        {
                            string dirName = Path.GetFileName(dir);
                            string dest = Path.Combine(backupDir, dirName);
                            logCallback($"Excluding INF package component: {dirName}");
                            Directory.Move(dir, dest);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                logCallback($"Failed to customize AMD configurations: {ex.Message}");
                return false;
            }
        }

        private async Task PruneManifestAsync(string manifestPath, List<AmdDriverPackageConfig> packages, Action<string> logCallback)
        {
            if (!File.Exists(manifestPath)) return;
            
            string backupPath = manifestPath + ".bak";
            if (!File.Exists(backupPath))
                File.Copy(manifestPath, backupPath);

            string json = await File.ReadAllTextAsync(manifestPath);
            var root = JsonNode.Parse(json);
            
            var pkgArray = root?["Packages"]?["Package"]?.AsArray();
            if (pkgArray != null)
            {
                var unselectedNames = packages.Where(p => !p.IsSelected).Select(p => p.ProductName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                
                // Remove nodes from end to start to maintain correct Array indexing
                for (int i = pkgArray.Count - 1; i >= 0; i--)
                {
                    var node = pkgArray[i];
                    string? name = node?["Info"]?["productName"]?.GetValue<string>();
                    
                    if (name != null && unselectedNames.Contains(name))
                    {
                        logCallback($"Pruning {name} from {Path.GetFileName(manifestPath)}");
                        pkgArray.RemoveAt(i);
                    }
                }
            }

            await File.WriteAllTextAsync(manifestPath, root?.ToJsonString() ?? "{}");
        }

        private static List<string> FindAllAmdDisplayInfs(string extractedDir)
        {
            var results = new List<string>();
            if (!System.IO.Directory.Exists(extractedDir)) return results;

            foreach (var sub in new[] { "Display2", "Display" })
            {
                var dir = System.IO.Path.Combine(extractedDir, "Packages", "Drivers", sub);
                if (System.IO.Directory.Exists(dir))
                {
                    var infs = System.IO.Directory.GetFiles(dir, "*.inf", System.IO.SearchOption.AllDirectories)
                        .Where(f => System.IO.Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                    f.Contains("WT6A_INF", StringComparison.OrdinalIgnoreCase));
                    results.AddRange(infs);
                }
            }

            var altDir = System.IO.Path.Combine(extractedDir, "Drivers");
            if (System.IO.Directory.Exists(altDir))
            {
                var infs = System.IO.Directory.GetFiles(altDir, "*.inf", System.IO.SearchOption.AllDirectories)
                    .Where(f => System.IO.Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("WT6A_INF", StringComparison.OrdinalIgnoreCase));
                results.AddRange(infs);
            }

            if (results.Count == 0)
            {
                var allInfs = System.IO.Directory.GetFiles(extractedDir, "*.inf", System.IO.SearchOption.AllDirectories)
                    .Where(f => System.IO.Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Display", StringComparison.OrdinalIgnoreCase));
                results.AddRange(allInfs);
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(f => System.IO.Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        }

        public async Task<bool> InstallCustomizedAsync(string extractDir, string logPath, Action<string> logCallback, CancellationToken ct)
        {
            try
            {
                var infs = FindAllAmdDisplayInfs(extractDir);
                if (infs.Count == 0)
                {
                    logCallback("No AMD display INFs found in the extracted payload.");
                    return false;
                }

                foreach (var inf in infs)
                {
                    logCallback($"Injecting AMD display driver: {inf}");
                    var psi = new ProcessStartInfo { 
                        FileName = "pnputil", 
                        Arguments = $"/add-driver \"{inf}\" /install", 
                        UseShellExecute = false, 
                        CreateNoWindow = true, 
                        RedirectStandardOutput = true 
                    };
                    
                    using var process = Process.Start(psi);
                    if (process == null) continue;
                    
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
                    
                    await process.WaitForExitAsync(timeoutCts.Token);
                    int exitCode = process.ExitCode;
                    
                    if (exitCode == 0 || exitCode == 3010) {
                         logCallback($"Successfully installed AMD driver natively! Exit code: {exitCode}");
                         return true;
                    }
                    if (exitCode == 259) {
                         logCallback($"INF {Path.GetFileName(inf)} skipped internally - hardware up to date or device signature mismatch (PNP 259).");
                         continue;
                    }
                    logCallback($"INF {System.IO.Path.GetFileName(inf)} execution dropped strictly with exit code {exitCode}.");
                }
                
                return false;
            }
            catch (Exception ex)
            {
                logCallback($"AMD native PnpUtil installation failed: {ex.Message}");
                return false;
            }
        }

        public async Task<int> UninstallInteractiveAsync(string extractDir, Action<string> logCallback, CancellationToken ct)
        {
            try
            {
                string cleanupPath = Path.Combine(extractDir, "Bin64", "AMDCleanupUtility.exe");
                if (!File.Exists(cleanupPath))
                {
                    logCallback($"AMDCleanupUtility not found at {cleanupPath}");
                    return -1;
                }
                
                logCallback($"Launching AMD Cleanup Utility: {cleanupPath}");
                
                // No silent flags exist. Must wrap around standard UI constraints.
                var psi = new ProcessStartInfo
                {
                    FileName = cleanupPath,
                    UseShellExecute = true
                };

                using var process = Process.Start(psi);
                if (process == null) return -1;
                
                // Wait indefinitely since it's an interactive UI
                await process.WaitForExitAsync(ct);
                
                logCallback($"AMD uninstaller exited with code: {process.ExitCode}");
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logCallback($"AMD Uninstall failed: {ex.Message}");
                return -1;
            }
        }
    }
}
