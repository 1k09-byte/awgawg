// App settings page (persisted via ThemeService).
// Written in the Windows App SDK C# dialect. See docs/GALLERY-REFERENCE.md section 2.

using Microsoft.UI.Xaml.Controls;

namespace stellarisKIT.Pages
{
    /// <summary>
    /// App settings page (persisted via ThemeService).
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly stellarisKIT.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private async void SettingsPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _syncingStartupToggle = true;
            try
            {
                startupToggle.IsOn = await _startup.IsEnabledAsync();
            }
            catch
            {
                startupToggle.IsOn = false;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }

        private async void StartupToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_syncingStartupToggle) return;
            _syncingStartupToggle = true;
            try
            {
                bool ok = await _startup.SetEnabledAsync(startupToggle.IsOn);
                if (!ok)
                {
                    // Revert: request denied (e.g. user dismissed the OS prompt).
                    startupToggle.IsOn = !startupToggle.IsOn;
                }
            }
            catch
            {
                startupToggle.IsOn = !startupToggle.IsOn;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }
    }
}