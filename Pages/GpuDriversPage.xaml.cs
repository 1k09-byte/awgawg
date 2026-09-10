using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using stellarisKIT.Models;
using stellarisKIT.ViewModels;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace stellarisKIT.Pages
{
    public sealed partial class GpuDriversPage : Page
    {
        public GpuDriversViewModel ViewModel { get; }

        public GpuDriversPage()
        {
            ViewModel = new GpuDriversViewModel();
            InitializeComponent();
            Loaded += GpuDriversPage_Loaded;
        }

        private async void GpuDriversPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Detect on first visit so the adapter list + installed versions are live.
            if (ViewModel.DetectedGpus.Count == 0 && !ViewModel.IsDetecting)
                await ViewModel.DetectGpusCommand.ExecuteAsync(null);

            await Task.Delay(50); // Let layout complete first

            for (int i = 0; i < ViewModel.VisibleDrivers.Count; i++)
            {
                var element = DriverGrid.TryGetElement(i);
                if (element is UIElement uiElement)
                {
                    uiElement.Opacity = 0;
                    uiElement.Translation = new Vector3(0, 40, 0);

                    await Task.Delay(100 * i);

                    uiElement.Opacity = 1;
                    uiElement.Translation = Vector3.Zero;
                }
            }
        }

        private async void DriverCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is GpuDriverItem driver)
            {
                ViewModel.SelectedDriver = driver;
                await DriverDetailsDialog.ShowAsync();
            }
        }

        private async void DialogProgressBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedDriver != null)
            {
                await ViewModel.InstallDriverCommand.ExecuteAsync(ViewModel.SelectedDriver);
            }
        }

        private void DialogVendorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedDriver != null)
            {
                ViewModel.OpenVendorPageCommand.Execute(ViewModel.SelectedDriver);
            }
        }

        // Static converter methods for x:Bind (same pattern as InstallerPage)
        public static Visibility BoolToVis(bool isVisible)
        {
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public static Visibility CountToVis(int count)
        {
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public static bool Not(bool val) => !val;

        public static Visibility NotEmptyToVisibility(string? text)
        {
            return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public static Visibility IsDoneToVisibility(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.Installed or GpuDriverStatus.UpToDate or GpuDriverStatus.ManualActionRequired
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public static string GetCloseButtonText(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.Downloading or GpuDriverStatus.Installing ? "Cancel" : "Close";
        }

        public static BitmapImage VendorIcon(string vendor)
        {
            // x:Bind function bindings require the exact target type, so this
            // returns ImageSource directly (unlike string property bindings).
            string path = vendor switch
            {
                _ when vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => "ms-appx:///Assets/nvidia-logo.png",
                _ when vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "ms-appx:///Assets/amd-logo.png",
                _ when vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "ms-appx:///Assets/intel-logo.png",
                _ => ""
            };
            return string.IsNullOrEmpty(path) ? new BitmapImage() : new BitmapImage(new Uri(path));
        }
    }
}
