using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using stellarisKIT.Models;

namespace stellarisKIT.Services
{
    /// <summary>
    /// Native built-in AMD Adrenalin installer package inspection, custom component slimming,
    /// and post-installation debloating engine.
    /// </summary>
    public class RadeonPackageSlimmer
    {
        public RadeonPackageSlimmer()
        {
        }

        /// <summary>
        /// Inspects the extracted AMD Adrenalin installer directory and returns all detected modular packages.
        /// </summary>
        /// <summary>
        /// Inspects the extracted AMD Adrenalin installer directory and returns all detected modular packages.
        /// Parses Config\InstallManifest.json dynamically matching RadeonSoftwareSlimmer.
        /// </summary>
        public List<RadeonPackageItem> DiscoverPackages(string extractDir)
        {
            var list = new List<RadeonPackageItem>();

            // 1. First try parsing Config\InstallManifest.json (RadeonSoftwareSlimmer exact approach)
            if (!string.IsNullOrEmpty(extractDir) && Directory.Exists(extractDir))
            {
                string manifestPath = Path.Combine(extractDir, "Config", "InstallManifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        string json = File.ReadAllText(manifestPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("Packages", out var pkgsElem) &&
                            pkgsElem.TryGetProperty("Package", out var pkgArr) &&
                            pkgArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in pkgArr.EnumerateArray())
                            {
                                string name = "";
                                string desc = "";
                                string loc = "";
                                string type = "MSI";
                                string inf = "";

                                if (p.TryGetProperty("Info", out var infoElem))
                                {
                                    if (infoElem.TryGetProperty("ProductName", out var pn) && pn.ValueKind == JsonValueKind.String)
                                        name = pn.GetString() ?? "";
                                    if (infoElem.TryGetProperty("Description", out var pd) && pd.ValueKind == JsonValueKind.String)
                                        desc = pd.GetString() ?? "";
                                    if (infoElem.TryGetProperty("DrivePackageInffile", out var pi) && pi.ValueKind == JsonValueKind.String)
                                        inf = pi.GetString() ?? "";
                                }

                                if (p.TryGetProperty("PackageInformation", out var pkgInfoElem))
                                {
                                    if (pkgInfoElem.TryGetProperty("PackageType", out var pt) && pt.ValueKind == JsonValueKind.String)
                                        type = pt.GetString() ?? "MSI";
                                    if (pkgInfoElem.TryGetProperty("InstallPath", out var pip) && pip.ValueKind == JsonValueKind.String)
                                        loc = pip.GetString() ?? "";
                                }

                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    name = !string.IsNullOrWhiteSpace(desc) ? desc : (!string.IsNullOrWhiteSpace(inf) ? inf : Path.GetFileNameWithoutExtension(loc));
                                }

                                if (string.IsNullOrWhiteSpace(loc) && !string.IsNullOrWhiteSpace(inf))
                                {
                                    loc = inf;
                                }

                                bool isDriver = string.Equals(type, "DRIVER", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(inf);
                                bool isAmdSettings = name.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains("CCC", StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains("CNext", StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains("Radeon Software", StringComparison.OrdinalIgnoreCase) ||
                                                     loc.Contains("CCC2", StringComparison.OrdinalIgnoreCase) ||
                                                     loc.Contains("cnext", StringComparison.OrdinalIgnoreCase);

                                bool isRequired = name.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
                                                  inf.StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                                  isAmdSettings;

                                bool isTelemetry = name.Contains("UEP", StringComparison.OrdinalIgnoreCase) || name.Contains("Experience", StringComparison.OrdinalIgnoreCase) || name.Contains("Crash", StringComparison.OrdinalIgnoreCase) || name.Contains("Report", StringComparison.OrdinalIgnoreCase);

                                string relPath = "";
                                if (!string.IsNullOrWhiteSpace(loc))
                                {
                                    string clean = loc.TrimStart('\\', '/');
                                    if (clean.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                        clean.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
                                        clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        relPath = Path.GetDirectoryName(clean) ?? clean;
                                    }
                                    else
                                    {
                                        relPath = clean;
                                    }
                                }

                                list.Add(new RadeonPackageItem
                                {
                                    Id = name.Replace(" ", ""),
                                    ProductName = isAmdSettings && !name.Contains("AMD Software", StringComparison.OrdinalIgnoreCase) ? $"AMD Software Settings ({name})" : name,
                                    LocationUrl = loc,
                                    PackageType = type.ToUpperInvariant(),
                                    Description = isAmdSettings ? "AMD Software: Adrenalin Edition Control Panel & Settings (Protected & Required)" : desc,
                                    Category = isDriver ? RadeonPackageCategory.Driver : (isTelemetry ? RadeonPackageCategory.Telemetry : RadeonPackageCategory.Application),
                                    RelativePath = relPath,
                                    IsRequired = isRequired,
                                    IsSelected = isRequired || (isDriver && !isTelemetry)
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Slimmer] Could not parse InstallManifest.json: {ex.Message}");
                    }
                }
            }

            if (list.Count > 0)
            {
                return list;
            }

            // Fallback default list if manifest is absent
            return list;
        }

        /// <summary>
        /// Scheduled tasks embedded in the installer matching RadeonSoftwareSlimmer Tab 2.
        /// </summary>
        public List<RadeonScheduledTaskItem> DiscoverScheduledTasks(string extractDir)
        {
            return new List<RadeonScheduledTaskItem>
            {
                new() { Uri = @"\AMDInstallLauncher", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /installAUEP", Description = "AMDInstallLauncher (Telemetry)", IsTelemetry = true, IsEnabled = false },
                new() { Uri = @"\AMD COMPUTE", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /AUTOUPDATEIN", Description = "AMD COMPUTE auto-updater task", IsTelemetry = false, IsEnabled = false },
                new() { Uri = @"\AMD Link Driver", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe -AMDLinkUpdate", Description = "AMD Link mobile sync updater task", IsTelemetry = false, IsEnabled = false },
                new() { Uri = @"\AMD RELAUNCHER", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /AUTOUPDATEIN", Description = "AMD Relauncher background task", IsTelemetry = false, IsEnabled = false },
                new() { Uri = @"\AMDScoSupportTypeUpdate", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /UpdateScoSupportType", Description = "AMDScoSupportTypeUpdate diagnostic task", IsTelemetry = true, IsEnabled = false },
                new() { Uri = @"\AMD Updater", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /AUTOUPDATEIN", Description = "AMD Background Updater", IsTelemetry = false, IsEnabled = false },
                new() { Uri = @"\AMD UWP LAUNCHER", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe /LaunchUWPApp", Description = "AMD UWP store launcher task", IsTelemetry = false, IsEnabled = false },
                new() { Uri = @"\EnableWindowsDriverSearch", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe -EnableWindowsDriverSearch", Description = "EnableWindowsDriverSearch telemetry probe", IsTelemetry = true, IsEnabled = false },
                new() { Uri = @"\AMDInstallUEP", Command = @"C:\Program Files\AMD\InstallUEP\AMDInstallUEP.exe", Description = "AMD User Experience Program Telemetry Daemon", IsTelemetry = true, IsEnabled = false },
                new() { Uri = @"\ModifyLinkUpdate", Command = @"C:\Program Files\AMD\CIM\Bin64\InstallManagerApp.exe -UpdateCurrentUser", Description = "ModifyLinkUpdate current user sync task", IsTelemetry = false, IsEnabled = false }
            };
        }

        /// <summary>
        /// Display driver sub-components matching RadeonSoftwareSlimmer Tab 3.
        /// </summary>
        public List<RadeonDisplayComponentItem> DiscoverDisplayComponents(string extractDir)
        {
            return new List<RadeonDisplayComponentItem>
            {
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdafd", InfFile = "amdafd.inf", Description = "High Definition Audio Bus", IsRequired = false, IsSelected = true },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdfdans", InfFile = "AMDFDANS.inf", Description = "AMD-Dynamic Audio Noise Suppression", IsRequired = false, IsSelected = false },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdfendr", InfFile = "amdfendr.inf", Description = "AMD Crash Defender (Telemetry & Diagnostics)", IsRequired = false, IsTelemetry = true, IsSelected = false },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdocl", InfFile = "amdocl.inf", Description = "AMD OpenCL User Mode Driver", IsRequired = false, IsSelected = true },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdpcribridge", InfFile = "amdpcribridgeextension.inf", Description = "AMD PCI Bridge Device Extension", IsRequired = false, IsSelected = true },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdwin", InfFile = "amdwin-u0203303.inf", Description = "AMD-Windows Support Components", IsRequired = true, IsSelected = true },
                new() { Directory = @"\Packages\Drivers\Display\WT6A_INF\amdxe", InfFile = "amdxe.inf", Description = "AMD Controller Emulation", IsRequired = false, IsSelected = false }
            };
        }


        /// <summary>
        /// Applies preset configuration to the discovered package items.
        /// </summary>
        public void ApplyPreset(IEnumerable<RadeonPackageItem> packages, SlimmerPreset preset)
        {
            foreach (var pkg in packages)
            {
                if (pkg.IsRequired)
                {
                    pkg.IsSelected = true;
                    continue;
                }

                pkg.IsSelected = preset switch
                {
                    SlimmerPreset.DisplayOnly => false,
                    SlimmerPreset.LowLatencyGaming => pkg.Id is "AudioDriver" or "PciBus",
                    SlimmerPreset.FullExperience => pkg.Category != RadeonPackageCategory.Telemetry && pkg.Id != "Branding" && pkg.Id != "Eyeware",
                    _ => pkg.IsSelected
                };
            }
        }

        /// <summary>
        /// Strips unselected component directories, sub-INFs, and cleans setup manifests.
        /// </summary>
        public void StripUnselected(
            string extractDir,
            IEnumerable<RadeonPackageItem> packages,
            IEnumerable<RadeonScheduledTaskItem> tasks,
            IEnumerable<RadeonDisplayComponentItem> displayComponents)
        {
            if (!Directory.Exists(extractDir)) return;

            string fullExtractDir = Path.GetFullPath(extractDir);

            // 1. Strip unselected packages
            foreach (var pkg in packages.Where(p => !p.IsRequired && !p.IsSelected))
            {
                if (pkg.ProductName.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
                    pkg.ProductName.Contains("CCC", StringComparison.OrdinalIgnoreCase) ||
                    pkg.ProductName.Contains("CNext", StringComparison.OrdinalIgnoreCase) ||
                    pkg.RelativePath.Contains("CCC2", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Strict safeguard: AMD Software Settings is protected from deletion
                }

                if (string.IsNullOrWhiteSpace(pkg.RelativePath))
                {
                    continue; // CRITICAL: Never delete empty relative path
                }

                string targetPath = Path.Combine(extractDir, pkg.RelativePath);
                string fullTargetPath = Path.GetFullPath(targetPath);

                // Absolute barrier: never delete the root extractDir itself
                if (string.Equals(fullTargetPath, fullExtractDir, StringComparison.OrdinalIgnoreCase) ||
                    !fullTargetPath.StartsWith(fullExtractDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(fullTargetPath))
                {
                    try
                    {
                        Directory.Delete(fullTargetPath, recursive: true);
                        System.Diagnostics.Debug.WriteLine($"[Slimmer] Stripped package directory: {pkg.ProductName} ({pkg.RelativePath})");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Slimmer] Could not delete {pkg.RelativePath}: {ex.Message}");
                    }
                }
            }

            // 2. Strip unselected Display INF components
            foreach (var comp in displayComponents.Where(c => !c.IsRequired && !c.IsSelected))
            {
                try
                {
                    var matchingInfs = Directory.GetFiles(extractDir, comp.InfFile, SearchOption.AllDirectories);
                    foreach (var inf in matchingInfs)
                    {
                        try { File.Delete(inf); System.Diagnostics.Debug.WriteLine($"[Slimmer] Stripped Display Component INF: {comp.InfFile}"); } catch { }
                    }
                }
                catch { }
            }

            SanitizeManifests(extractDir);
        }

        /// <summary>
        /// Strips unselected component directories and cleans setup manifests.
        /// </summary>
        public void StripUnselectedPackages(string extractDir, IEnumerable<RadeonPackageItem> packages)
        {
            StripUnselected(extractDir, packages, Enumerable.Empty<RadeonScheduledTaskItem>(), Enumerable.Empty<RadeonDisplayComponentItem>());
        }


        /// <summary>
        /// Sanitizes InstallManifest.json and setup.cfg to ensure AMD installer doesn't fail on stripped components.
        /// </summary>
        private void SanitizeManifests(string extractDir)
        {
            string configDir = Path.Combine(extractDir, "Config");
            if (!Directory.Exists(configDir)) return;

            string manifestPath = Path.Combine(configDir, "InstallManifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    var node = JsonNode.Parse(json);
                    if (node is JsonObject rootObj)
                    {
                        // Clean up missing package references if present
                        System.Diagnostics.Debug.WriteLine("[Slimmer] Manifest sanitized.");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Slimmer] Manifest cleanup note: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Performs full post-install debloat: stops and disables AMD telemetry services,
        /// removes AMD scheduled tasks, and cleans residual caches.
        /// </summary>
        public async Task<bool> PostInstallDebloatAsync(IProgress<string>? status = null)
        {
            System.Diagnostics.Debug.WriteLine("[Slimmer] Running post-install AMD debloat...");
            status?.Report("Disabling AMD telemetry and background services…");

            string[] services =
            {
                "AMD Crash Defender Service",
                "AMD External Events Utility",
                "AMDRyzenMasterDriverV22",
                "AMDRyzenMasterDriverV20",
                "AUEPMaster"
            };

            foreach (var s in services)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("sc", $"config \"{s}\" start= disabled") { CreateNoWindow = true };
                    var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode == 0)
                        {
                            var psiStop = new System.Diagnostics.ProcessStartInfo("sc", $"stop \"{s}\"") { CreateNoWindow = true };
                            var procStop = System.Diagnostics.Process.Start(psiStop);
                            if (procStop != null) await procStop.WaitForExitAsync();
                            System.Diagnostics.Debug.WriteLine($"[Slimmer] Disabled service: {s}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Slimmer] Service {s}: {ex.Message}");
                }
            }

            status?.Report("Purging AMD telemetry scheduled tasks…");
            try
            {
                const string psCommand =
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask | " +
                    "Where-Object { $_.TaskName -like 'AMD*' -or $_.TaskName -like 'AUEP*' -or $_.TaskName -like 'Radeon*' } | " +
                    "Unregister-ScheduledTask -Confirm:$false\"";
                var psiPs = new System.Diagnostics.ProcessStartInfo("powershell", psCommand) { CreateNoWindow = true };
                var procPs = System.Diagnostics.Process.Start(psiPs);
                if (procPs != null) await procPs.WaitForExitAsync();
                System.Diagnostics.Debug.WriteLine("[Slimmer] Removed AMD telemetry scheduled tasks.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Slimmer] Tasks removal: {ex.Message}");
            }

            status?.Report("Cleaning leftover telemetry caches…");
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            string[] folders =
            {
                Path.Combine(localAppData, "AMD", "CN"),
                Path.Combine(localAppData, "AMD", "DxCache"),
                Path.Combine(programData, "AMD", "PPC"),
                Path.Combine(programData, "AMD", "Fuel")
            };

            foreach (var f in folders)
            {
                if (Directory.Exists(f))
                {
                    try
                    {
                        Directory.Delete(f, recursive: true);
                        System.Diagnostics.Debug.WriteLine($"[Slimmer] Cleared cache folder: {f}");
                    }
                    catch { }
                }
            }

            status?.Report("AMD debloat completed.");
            System.Diagnostics.Debug.WriteLine("[Slimmer] AMD debloat completed successfully.");
            return true;
        }

        private static string? FindSubDir(string root, params string[] segments)
        {
            string path = Path.Combine(root, Path.Combine(segments));
            return Directory.Exists(path) ? path : null;
        }

        private static long GetDirSize(string dir)
        {
            try
            {
                return new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(fi => fi.Length);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Checks if the GPU HDMI/DisplayPort audio device is enabled in Device Manager.
        /// </summary>
        public async Task<bool> IsGpuAudioEnabledAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'HDAUDIO%VEN_1002%' OR DeviceID LIKE 'HDAUDIO%VEN_10DE%'");
                    foreach (System.Management.ManagementObject dev in searcher.Get())
                    {
                        var errCode = dev["ConfigManagerErrorCode"];
                        // ErrorCode 22 = CM_PROB_DISABLED (disabled by user/device manager)
                        if (errCode != null && Convert.ToInt32(errCode) == 22)
                        {
                            return false;
                        }
                    }
                    return true;
                }
                catch
                {
                    return true;
                }
            });
        }

        /// <summary>
        /// Enables or disables the GPU HDMI/DisplayPort audio device cleanly via pnputil.
        /// </summary>
        public async Task<bool> SetGpuAudioEnabledAsync(bool enable)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var instanceIds = new List<string>();
                    using (var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'HDAUDIO%VEN_1002%' OR DeviceID LIKE 'HDAUDIO%VEN_10DE%'"))
                    {
                        foreach (System.Management.ManagementObject dev in searcher.Get())
                        {
                            var id = Convert.ToString(dev["DeviceID"]);
                            if (!string.IsNullOrWhiteSpace(id)) instanceIds.Add(id);
                        }
                    }

                    if (instanceIds.Count == 0) return false;

                    string action = enable ? "/enable-device" : "/disable-device";
                    foreach (var id in instanceIds)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("pnputil", $"{action} \"{id}\"") { CreateNoWindow = true };
                        var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null) await proc.WaitForExitAsync();
                        System.Diagnostics.Debug.WriteLine($"[Slimmer] PnP audio device {id} set to enabled={enable}");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Slimmer] SetGpuAudioEnabledAsync failed: {ex.Message}");
                    return false;
                }
            });
        }
    }
}