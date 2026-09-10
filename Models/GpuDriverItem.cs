using CommunityToolkit.Mvvm.ComponentModel;

namespace stellarisKIT.Models
{
    public partial class GpuDriverItem : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        // NVIDIA / AMD / Intel
        [ObservableProperty]
        private string vendor = string.Empty;

        [ObservableProperty]
        private string description = string.Empty;

        // Driver version currently on this machine ("" when nothing usable detected).
        [ObservableProperty]
        private string installedVersion = string.Empty;

        // Latest version from the vendor lookup ("" until checked).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        private string latestVersion = string.Empty;

        [ObservableProperty]
        private string downloadUrl = string.Empty;

        [ObservableProperty]
        private string silentInstallArgs = string.Empty;

        [ObservableProperty]
        private string installerFileName = string.Empty;

        // Fallback official page (used when lookup/download fails, and for page-only vendors).
        [ObservableProperty]
        private string vendorPageUrl = string.Empty;

        // True when the vendor installer has no reliable silent flags (AMD/Intel):
        // the app downloads (if a URL is known) then launches the vendor UI.
        [ObservableProperty]
        private bool guidedInstallOnly;

        // True when there is no direct download at all (Intel): primary button opens the page.
        [ObservableProperty]
        private bool isPageOnly;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        private GpuDriverStatus status = GpuDriverStatus.NotChecked;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private double downloadProgress = 0;

        [ObservableProperty]
        private string errorMessage = string.Empty;

        [ObservableProperty]
        private bool isVisible = true;

        public string StatusText => Status switch
        {
            GpuDriverStatus.NotChecked => "Check for updates",
            GpuDriverStatus.NotInstalled => "Install",
            GpuDriverStatus.UpdateAvailable => string.IsNullOrEmpty(LatestVersion) ? "Update available" : $"Update available · {LatestVersion}",
            GpuDriverStatus.UpToDate => string.IsNullOrEmpty(InstalledVersion) ? "Up to date" : $"Up to date · {InstalledVersion}",
            GpuDriverStatus.Downloading => $"Downloading {DownloadProgress:F0}%",
            GpuDriverStatus.Installing => "Installing...",
            GpuDriverStatus.Installed => "Done",
            GpuDriverStatus.Failed => "Failed",
            GpuDriverStatus.ManualActionRequired => "Continue in the vendor installer",
            _ => ""
        };

        public string PrimaryActionText => Status switch
        {
            _ when IsPageOnly => "Open download center",
            GpuDriverStatus.NotChecked => "Check for updates",
            GpuDriverStatus.NotInstalled => "Install",
            GpuDriverStatus.UpdateAvailable => "Update now",
            GpuDriverStatus.UpToDate => "Reinstall",
            GpuDriverStatus.Failed => "Retry",
            _ => "Install"
        };
    }

    public enum GpuDriverStatus
    {
        NotChecked,
        NotInstalled,
        UpdateAvailable,
        UpToDate,
        Downloading,
        Installing,
        Installed,
        Failed,
        ManualActionRequired
    }
}
