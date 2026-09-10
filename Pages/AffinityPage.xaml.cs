using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using stellarisKIT.Models;
using stellarisKIT.ViewModels;

namespace stellarisKIT.Pages
{
    public sealed partial class AffinityPage : Page
    {
        public AffinityViewModel ViewModel { get; }

        public AffinityPage()
        {
            ViewModel = new AffinityViewModel();
            InitializeComponent();
            Loaded += AffinityPage_Loaded;
        }

        private async void AffinityPage_Loaded(object sender, RoutedEventArgs e)
        {
            // First visit scans so the sections show live hardware.
            if (ViewModel.GraphicsDevices.Count == 0 &&
                ViewModel.NetworkDevices.Count == 0 &&
                ViewModel.UsbDevices.Count == 0 &&
                ViewModel.AudioDevices.Count == 0 &&
                !ViewModel.IsRefreshing)
            {
                await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        private async void DeviceRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AffinityDeviceItem device)
            {
                try
                {
                    await ViewModel.LoadDeviceDetailsAsync(device);
                }
                catch
                {
                    ViewModel.SelectedDevice = device;
                }
                DeviceDetailsDialog.XamlRoot = XamlRoot;
                await DeviceDetailsDialog.ShowAsync();
            }
        }

        private async void DialogApply_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedDevice is null)
            {
                DeviceDetailsDialog.Hide();
                return;
            }

            int previousChanges = ViewModel.TrackedChanges.Count;
            ViewModel.ApplyDeviceChanges(ViewModel.SelectedDevice);
            DeviceDetailsDialog.Hide();
                
            // If the application engine actually pushed a modification to the registry
            if (ViewModel.TrackedChanges.Count > previousChanges)
            {
                var restartDialog = new ContentDialog
                {
                    Title = "Restart Device",
                    Content = $"Changes have been successfully applied to {ViewModel.SelectedDevice.Name}.\n\nWould you like to restart the device now for the changes to take effect?",
                    PrimaryButtonText = "Restart Device",
                    CloseButtonText = "Later",
                    XamlRoot = XamlRoot,
                    Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
                };

                var result = await restartDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await stellarisKIT.Services.AffinityService.RestartDeviceAsync(ViewModel.SelectedDevice.DeviceInstanceId);
                }
            }
        }

        private void DialogCancel_Click(object sender, RoutedEventArgs e)
        {
            DeviceDetailsDialog.Hide();
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            // Build human-readable change list for the dialog
            string content;
            if (ViewModel.TrackedChanges.Count == 0)
            {
                content = "No changes have been made yet.";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in ViewModel.TrackedChanges)
                {
                    string oldVal = FormatValue(c.PropertyName, c.OldValue);
                    string newVal = FormatValue(c.PropertyName, c.NewValue);
                    sb.AppendLine($"• {c.DeviceName}");
                    sb.AppendLine($"    {c.PropertyName}: {oldVal} → {newVal}");
                    sb.AppendLine();
                }
                content = sb.ToString().TrimEnd();
            }

            var dialog = new ContentDialog
            {
                Title = $"Tracked Changes ({ViewModel.TrackedChanges.Count})",
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    Content = new TextBlock
                    {
                        Text = content,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };
            await dialog.ShowAsync();
        }

        private static string FormatValue(string propertyName, object? value)
        {
            if (value is null) return "—";
            return propertyName switch
            {
                "MsiEnabled" => (bool)value ? "Enabled" : "Disabled",
                "DevicePolicy" => Services.AffinityService.DevicePolicyShort((int?)value),
                "DevicePriority" => Services.AffinityService.DevicePriorityName((int?)value),
                "AffinityMask" => Services.AffinityService.AffinityMaskText((ulong?)value),
                _ => value.ToString() ?? "—"
            };
        }

        // Static converter methods for x:Bind
        public static Visibility BoolToVis(bool isVisible)
        {
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public static string ExpanderGlyph(bool expanded)
        {
            return expanded ? "\uE70D" : "\uE76C";
        }

        public static BitmapImage? VendorIcon(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string? path = null;
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/nvidia-logo.png";
            else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/amd-logo.png";
            else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/intel-logo.png";
            return path is null ? null : new BitmapImage(new Uri(path));
        }

        public static Visibility EmptyToVisibility(int count)
        {
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public static bool Not(bool val) => !val;
    }
}
