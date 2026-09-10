using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using stellarisKIT.ViewModels;
using System;
using Windows.Storage.Pickers;

namespace stellarisKIT.Pages;

public sealed partial class WindowsSettingsPage : Page
{
    public WindowsSettingsViewModel ViewModel => (WindowsSettingsViewModel)DataContext;
    public WindhawkProvisioningViewModel Windhawk => ViewModel.Windhawk;

    public WindowsSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = ViewModel.LoadAsync();
        Loaded += (_, _) => Windhawk.RefreshDetection();
    }

    private void ServiceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEdit)
            _ = ViewModel.ToggleServiceCommand.ExecuteAsync(null);
        else
            _ = ViewModel.LoadAsync();
    }

    private void PowerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEdit)
            _ = ViewModel.SavePowerSettingsCommand.ExecuteAsync(null);
        else
            _ = ViewModel.LoadAsync();
    }

    private async void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".json");

        // Unpackaged WinUI 3 pickers need the owning window handle.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            Windhawk.BackupFilePath = file.Path;
            // Picking a custom file opts out of the bundled default.
            Windhawk.UseBundledDefaults = false;
        }
    }

    private async void StartWindhawk_Click(object sender, RoutedEventArgs e)
    {
        // Confirmation before installing anything or overwriting settings.
        var dialog = new ContentDialog
        {
            Title = "Install Windhawk & import settings?",
            Content = Windhawk.IsInstalled
                ? "Your current Windhawk mods and settings will be backed up, then overwritten with the selected backup file. Continue?"
                : "The latest Windhawk release will be downloaded from its official GitHub releases and installed silently (administrator rights required). Then the selected backup will be imported over any existing settings. Continue?",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await Windhawk.RunProvisioningAsync();
    }
}
