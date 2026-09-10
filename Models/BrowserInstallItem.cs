using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace stellarisKIT.Models
{
    public partial class BrowserInstallItem : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string imagePath = string.Empty;

        [ObservableProperty]
        private string downloadUrl = string.Empty;

        [ObservableProperty]
        private string silentInstallArgs = string.Empty;

        [ObservableProperty]
        private string installerFileName = string.Empty;

        [ObservableProperty]
        private string installedCheckPath = string.Empty;

        [ObservableProperty]
        private string description = string.Empty;

        [ObservableProperty]
        private string errorMessage = string.Empty;

        [ObservableProperty]
        private double downloadProgress = 0;

        [ObservableProperty]
        private bool isIndeterminate = false;

        [ObservableProperty]
        private BrowserInstallStatus status = BrowserInstallStatus.NotInstalled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private double installProgress = 0;

        [ObservableProperty]
        private bool isVisible = true;

        public string StatusText => Status switch
        {
            BrowserInstallStatus.NotInstalled => "Install",
            BrowserInstallStatus.Downloading => $"Downloading {InstallProgress:F0}%",
            BrowserInstallStatus.Installing => "Installing...",
            BrowserInstallStatus.AlreadyInstalled => "Already installed",
            BrowserInstallStatus.Installed => "Done",
            BrowserInstallStatus.Failed => "Failed",
            _ => ""
        };

        public ObservableCollection<ExtensionItem> Extensions { get; set; } = new();
    }

    public enum BrowserInstallStatus
    {
        NotInstalled,
        Downloading,
        Installing,
        AlreadyInstalled,
        Installed,
        Failed
    }
}
