using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace stellarisKIT.ViewModels
{
    public partial class GpuDriversViewModel : ObservableObject
    {
        private readonly GpuDriverService _gpuService = new();
        private readonly NvidiaDriverService _nvidiaService = new();
        private readonly AmdDriverService _amdService = new();
        
        private CancellationTokenSource? _cts;

        public ObservableCollection<GpuDriverItem> Drivers { get; } = new();
        public ObservableCollection<DetectedGpu> DetectedGpus { get; } = new();

        // Cards actually shown: only vendors present on this machine.
        // Falls back to all three when nothing has been detected yet.
        public ObservableCollection<GpuDriverItem> VisibleDrivers { get; } = new();

        [ObservableProperty]
        public partial GpuDriverItem? SelectedDriver { get; set; }

        [ObservableProperty]
        public partial bool IsInstalling { get; set; }

        [ObservableProperty]
        public partial string InstallStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsDetecting { get; set; }

        [ObservableProperty]
        public partial string DetectStatusText { get; set; } = "Detecting GPUs...";

        // NVIDIA properties
        public ObservableCollection<NvidiaDriverPackage> NvidiaPackages { get; } = new();
        
        [ObservableProperty]
        public partial NvidiaDriverPackage? SelectedNvidiaPackage { get; set; }

        // AMD properties 
        public ObservableCollection<AmdDriverPackageConfig> AmdPackages { get; } = new();

        public GpuDriversViewModel()
        {
            Drivers.Add(new GpuDriverItem
            {
                Name = "NVIDIA GeForce Game Ready Driver",
                Vendor = "NVIDIA",
                Description = "Game Ready WHQL driver, installed silently (display driver + bundled components from the official package).",
                SilentInstallArgs = "-s -noreboot",
                InstallerFileName = "nvidia_driver.exe",
                VendorPageUrl = "https://www.nvidia.com/en-us/geforce/drivers/"
            });
            Drivers.Add(new GpuDriverItem
            {
                Name = "AMD Software: Adrenalin Edition",
                Vendor = "AMD",
                Description = "Official Adrenalin package. AMD offers no reliable silent flags natively, so the app scrapes the newest executable payload dynamically bypassing CDN protections.",
                DownloadUrl = string.Empty,
                InstallerFileName = "amd_software_installer.exe",
                VendorPageUrl = "https://www.amd.com/en/support/downloads/drivers.html",
                GuidedInstallOnly = true
            });
            Drivers.Add(new GpuDriverItem
            {
                Name = "Intel Arc Graphics Driver",
                Vendor = "Intel",
                Description = "Intel ships versioned bundles only — this card opens the official Intel download center.",
                VendorPageUrl = "https://www.intel.com/content/www/us/en/download-center/home.html",
                GuidedInstallOnly = true,
                IsPageOnly = true
            });

            UpdateVisibleDrivers();
        }

        [RelayCommand]
        private async Task DetectGpusAsync()
        {
            if (IsDetecting) return;
            IsDetecting = true;
            DetectStatusText = "Detecting GPUs...";
            try
            {
                var gpus = await _gpuService.DetectGpusAsync();
                DetectedGpus.Clear();
                foreach (var gpu in gpus)
                    DetectedGpus.Add(gpu);

                // Sync each driver card with what's on the machine.
                foreach (var driver in Drivers)
                {
                    var match = gpus.FirstOrDefault(g => g.Vendor.Equals(driver.Vendor, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    bool generic = match.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
                    driver.InstalledVersion = generic ? string.Empty : match.DriverVersion;
                    if (driver.Status is GpuDriverStatus.NotChecked or GpuDriverStatus.NotInstalled or GpuDriverStatus.UpToDate)
                        driver.Status = generic ? GpuDriverStatus.NotInstalled : GpuDriverStatus.NotChecked;
                }

                DetectStatusText = gpus.Count == 0
                    ? "No display adapters found."
                    : $"{gpus.Count} adapter{(gpus.Count == 1 ? "" : "s")} detected.";

                UpdateVisibleDrivers();
            }
            catch (Exception ex)
            {
                DetectStatusText = $"Detection failed: {ex.Message}";
            }
            finally
            {
                IsDetecting = false;
            }
        }

        private void UpdateVisibleDrivers()
        {
            VisibleDrivers.Clear();
            if (DetectedGpus.Count == 0)
            {
                foreach (var driver in Drivers)
                    VisibleDrivers.Add(driver);
                return;
            }
            var vendorsDetected = DetectedGpus.Select(g => g.Vendor).ToList();
            foreach (var driver in Drivers)
            {
                if (vendorsDetected.Contains(driver.Vendor, StringComparer.OrdinalIgnoreCase))
                {
                    VisibleDrivers.Add(driver);
                }
            }
            if (SelectedDriver == null && VisibleDrivers.Count > 0)
            {
                SelectedDriver = VisibleDrivers.FirstOrDefault();
            }
        }

        [RelayCommand]
        private async Task CheckDriverAsync(GpuDriverItem? item)
        {
            if (item is null || item.IsPageOnly) return;
            
            var gpu = DetectedGpus.FirstOrDefault(g => g.Vendor.Equals(item.Vendor, StringComparison.OrdinalIgnoreCase));
            if (gpu == null) return;
            
            if (item.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                item.Status = GpuDriverStatus.Downloading;
                try
                {
                    // Fetch up to 15 versions for dropdown selection
                    var packages = await _nvidiaService.GetDriversAsync(gpu.Name, 15, CancellationToken.None);
                    NvidiaPackages.Clear();
                    foreach(var pkg in packages) NvidiaPackages.Add(pkg);

                    if (NvidiaPackages.Count > 0)
                    {
                        SelectedNvidiaPackage = NvidiaPackages[0];
                        item.LatestVersion = NvidiaPackages[0].Version;
                        item.DownloadUrl = NvidiaPackages[0].DownloadUrl;
                        item.Status = string.IsNullOrEmpty(item.InstalledVersion) ? GpuDriverStatus.NotInstalled : GpuDriverStatus.UpdateAvailable;
                        item.ErrorMessage = string.Empty;
                    }
                    else
                    {
                        item.ErrorMessage = "Lookup failed — use the vendor page.";
                        item.Status = GpuDriverStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    item.ErrorMessage = $"Lookup failed: {ex.Message}";
                    item.Status = GpuDriverStatus.Failed;
                }
                return;
            }

            if (item.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
            {
                item.Status = GpuDriverStatus.Downloading;
                try
                {
                    var amdApi = new AmdDriverApiService();
                    var driverInfo = await amdApi.GetLatestDriverAsync(_cts?.Token ?? CancellationToken.None);
                    if (driverInfo != null)
                    {
                        item.LatestVersion = driverInfo.Version;
                        item.DownloadUrl = driverInfo.DownloadUrl;
                        item.Status = string.IsNullOrEmpty(item.InstalledVersion) ? GpuDriverStatus.NotInstalled : GpuDriverStatus.UpdateAvailable;
                        item.ErrorMessage = string.Empty;
                    }
                    else
                    {
                        item.ErrorMessage = "AMD API Lookup failed.";
                        item.Status = GpuDriverStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    item.ErrorMessage = $"AMD Lookup failed: {ex.Message}";
                    item.Status = GpuDriverStatus.Failed;
                }
                return;
            }
        }

        [RelayCommand]
        private async Task InstallDriverAsync(GpuDriverItem? item)
        {
            if (item is null) return;
            SelectedDriver = item;

            if (item.IsPageOnly)
            {
                GpuDriverService.OpenUrl(item.VendorPageUrl);
                return;
            }

            _cts = new CancellationTokenSource();
            IsInstalling = true;
            InstallStatusText = "Initializing...";

            IProgress<GpuDriverStatus> progress = new Progress<GpuDriverStatus>(status =>
            {
                item.Status = status;
                InstallStatusText = item.StatusText;
                if (status is GpuDriverStatus.Installed or GpuDriverStatus.Failed or GpuDriverStatus.ManualActionRequired)
                    IsInstalling = false;
            });
            IProgress<double> downloadProgress = new Progress<double>(val =>
            {
                item.DownloadProgress = val;
                InstallStatusText = $"Downloading... ({val:F1}%)";
            });
            IProgress<string> errorProgress = new Progress<string>(error => item.ErrorMessage = error);
            
            void logProgress(string msg)
            {
                System.Diagnostics.Debug.WriteLine(msg);
                InstallStatusText = msg;
            }

            try
            {
                if (item.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    if (SelectedNvidiaPackage != null) 
                    {
                        item.DownloadUrl = SelectedNvidiaPackage.DownloadUrl;
                    }
                    else
                    {
                        var gpu = DetectedGpus.FirstOrDefault(g => g.Vendor.Equals("NVIDIA"));
                        if (gpu != null)
                        {
                            var best = await _nvidiaService.GetDriversAsync(gpu.Name, 1, _cts.Token);
                            if (best.Count > 0) item.DownloadUrl = best[0].DownloadUrl;
                        }
                    }
                    
                    if (string.IsNullOrEmpty(item.DownloadUrl))
                    {
                        item.ErrorMessage = "No URL selected. Click Check to resolve driver.";
                        item.Status = GpuDriverStatus.Failed;
                        IsInstalling = false;
                        return;
                    }
                    
                    // Proceed to download and extract
                    progress.Report(GpuDriverStatus.Downloading);
                    string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
                    
                    // Simple download routine inline:
                    using var response = await new HttpClient().GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    response.EnsureSuccessStatusCode();
                    
                    using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                    await contentStream.CopyToAsync(fileStream, _cts.Token);
                    fileStream.Close();
                    
                    progress.Report(GpuDriverStatus.Installing);
                    bool success = await _nvidiaService.InstallSilentAsync(tempPath, true, logProgress, _cts.Token);
                    
                    if (success) progress.Report(GpuDriverStatus.Installed);
                    else { item.ErrorMessage = "Install failed or threw errors."; progress.Report(GpuDriverStatus.Failed); }
                    
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                else if (item.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.DownloadUrl))
                    {
                        item.ErrorMessage = "No URL identified for AMD.";
                        item.Status = GpuDriverStatus.Failed;
                        IsInstalling = false;
                        _cts.Dispose();
                        _cts = null;
                        return;
                    }

                    progress.Report(GpuDriverStatus.Downloading);
                    string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
                    
                    using var response = await new HttpClient().GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    response.EnsureSuccessStatusCode();
                    
                    using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                    await contentStream.CopyToAsync(fileStream, _cts.Token);
                    fileStream.Close();
                    
                    string extractDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AMD_Extract");
                    System.IO.Directory.CreateDirectory(extractDir);
                    
                    progress.Report(GpuDriverStatus.Installing);
                    
                    bool extracted = await _amdService.ExtractInstallerAsync(tempPath, extractDir, logProgress, _cts.Token);
                    if (!extracted) { item.ErrorMessage = "AMD Extraction failed."; progress.Report(GpuDriverStatus.Failed); return; }
                    
                    var slimmer = new RadeonPackageSlimmer();
                    var packages = slimmer.DiscoverPackages(extractDir);
                    var tasks = slimmer.DiscoverScheduledTasks(extractDir);
                    var displayComps = slimmer.DiscoverDisplayComponents(extractDir);
                    
                    var uiTask = new TaskCompletionSource<bool>();
                    App.MainWindow!.DispatcherQueue.TryEnqueue(async () =>
                    {
                        var dialog = new stellarisKIT.Controls.AmdPackageCustomizerDialog(
                            packages.ToList(), tasks.ToList(), displayComps.ToList(), slimmer);
                        dialog.XamlRoot = App.MainWindow.Content.XamlRoot;
                        
                        var result = await dialog.ShowAsync();
                        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                        {
                            slimmer.StripUnselected(extractDir, packages, tasks, displayComps);
                            uiTask.SetResult(true);
                        }
                        else
                        {
                            uiTask.SetResult(false);
                        }
                    });
                    
                    bool approved = await uiTask.Task;
                    if (!approved)
                    {
                        item.ErrorMessage = "Canceled";
                        progress.Report(GpuDriverStatus.Failed);
                        return;
                    }
                    
                    progress.Report(GpuDriverStatus.Installing);
                    
                    string logPath = Path.Combine(Path.GetTempPath(), "AMDInstall.log");
                    bool success = await _amdService.InstallCustomizedAsync(extractDir, logPath, logProgress, _cts.Token);
                    
                    if (success) progress.Report(GpuDriverStatus.Installed);
                    else { item.ErrorMessage = "Install failed or threw errors."; progress.Report(GpuDriverStatus.Failed); }
                }
            }
            catch (OperationCanceledException)
            {
                item.ErrorMessage = "Cancelled";
                item.Status = GpuDriverStatus.Failed;
            }

            IsInstalling = false;
            _cts.Dispose();
            _cts = null;
        }

        [RelayCommand]
        private void OpenVendorPage(GpuDriverItem? item)
        {
            if (item is null) return;
            GpuDriverService.OpenUrl(item.VendorPageUrl);
        }

        [RelayCommand]
        private void CancelInstall()
        {
            _cts?.Cancel();
        }
    }
}
