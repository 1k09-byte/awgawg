// Theme service implementation.
// Persistence mirrors WinUI/DevWinUI patterns: values stored via
// Windows.Storage.ApplicationData.LocalSettings.Values (packaged MSIX), with
// graceful fallback to in-memory for unpackaged runs.
// See docs/GALLERY-REFERENCE.md section 2/5.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using WinRT;
using Microsoft.UI.Composition.SystemBackdrops;

namespace stellarisKIT
{
    public class ThemeService : IThemeService
    {
        private static readonly string ThemeKey = "AppTheme";
        private static readonly string BackdropKey = "AppBackdrop";

        private Windows.Storage.ApplicationData? _appData;
        private FrameworkElement? _root;
        private Window? _window;
        private DesktopAcrylicController? _acrylicController;
        private MicaController? _micaController;
        private SystemBackdropConfiguration? _configurationSource;

        private ISystemBackdropControllerWithTargets? _currentController;

        public ThemeService()
        {
            // ApplicationData.Current requires a packaged (MSIX) app; unpackaged runs
            // fall back to in-memory persistence only (best-effort, never fatal).
            try
            {
                _appData = Windows.Storage.ApplicationData.Current;
            }
            catch (Exception)
            {
                _appData = null;
            }
        }

        public ElementTheme ElementTheme
        {
            get { return _root is null ? ElementTheme.Default : _root.ActualTheme; }
        }

        public void Initialize(Window window)
        {
            _window = window;
            if (window.Content is FrameworkElement rootElement)
            {
                _root = rootElement;
            }

            ElementTheme? saved = MapThemeName(GetSavedThemeName());
            if (saved is not null && _root is not null)
            {
                _root.RequestedTheme = saved.Value;
            }

            SetBackdrop(GetSavedBackdropName() ?? "Mica");
        }

        public void SetElementTheme(ElementTheme theme)
        {
            if (_root is not null)
            {
                _root.RequestedTheme = theme;
            }

            SaveTheme(theme);
        }

        public string? GetSavedThemeName()
        {
            if (_appData is null)
            {
                return null;
            }

            try
            {
                object? value = _appData.LocalSettings.Values[ThemeKey];
                return value as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SaveTheme(ElementTheme theme)
        {
            if (_appData is null)
            {
                return;
            }

            try
            {
                _appData.LocalSettings.Values[ThemeKey] = ThemeName(theme);
            }
            catch (Exception)
            {
                // Ignore persistence failures; theme still applies in-memory.
            }
        }

        private static string ThemeName(ElementTheme theme)
        {
            if (theme == ElementTheme.Light)
            {
                return "Light";
            }

            if (theme == ElementTheme.Dark)
            {
                return "Dark";
            }

            return "Default";
        }

        private static ElementTheme? MapThemeName(string? name)
        {
            if (name is null)
            {
                return null;
            }

            if (name == "Light")
            {
                return ElementTheme.Light;
            }

            if (name == "Dark")
            {
                return ElementTheme.Dark;
            }

            return ElementTheme.Default;
        }

        public void OnThemeComboBoxSelectionChanged(object sender)
        {
            if (sender is ComboBox comboBox &&
                comboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag)
            {
                SetElementTheme(MapThemeName(tag) ?? ElementTheme.Default);
            }
        }

        public void SetThemeComboBoxDefaultItem(ComboBox comboBox)
        {
            // SettingsPage defines items in order: Light(0), Dark(1), Use system setting(2 = Default).
            int index = 2; // Default
            string? saved = GetSavedThemeName();

            if (saved is not null)
            {
                if (saved == "Light")
                {
                    index = 0;
                }
                else if (saved == "Dark")
                {
                    index = 1;
                }
            }

            comboBox.SelectedIndex = index;
        }

        public void SetBackdrop(string backdropName)
        {
            if (_window is null) return;
            
            // Ensure Xaml SystemBackdrop is permanently null because we are using manual controllers
            _window.SystemBackdrop = null;

            if (_configurationSource == null)
            {
                _configurationSource = new SystemBackdropConfiguration();
                _configurationSource.IsInputActive = true;
                _configurationSource.Theme = SystemBackdropTheme.Default; // Keep matched to the system
            }

            ISystemBackdropControllerWithTargets? nextController = null;

            if (backdropName == "Mica")
            {
                if (_micaController == null) _micaController = new MicaController();
                _micaController.Kind = MicaKind.Base;
                nextController = _micaController;
            }
            else if (backdropName == "MicaAlt")
            {
                if (_micaController == null) _micaController = new MicaController();
                _micaController.Kind = MicaKind.BaseAlt;
                nextController = _micaController;
            }
            else if (backdropName == "Acrylic")
            {
                if (_acrylicController == null) _acrylicController = new DesktopAcrylicController();
                _acrylicController.Kind = DesktopAcrylicKind.Base;
                nextController = _acrylicController;
            }
            else if (backdropName == "AcrylicThin")
            {
                if (_acrylicController == null) _acrylicController = new DesktopAcrylicController();
                _acrylicController.Kind = DesktopAcrylicKind.Thin;
                nextController = _acrylicController;
            }

            // Smoothly remove old controller and add new one to prevent window flashing
            if (_currentController != null && _currentController != nextController)
            {
                _currentController.RemoveAllSystemBackdropTargets();
            }

            if (nextController != null && _currentController != nextController)
            {
                dynamic dynWindow = _window;
                nextController.AddSystemBackdropTarget(dynWindow);
                nextController.SetSystemBackdropConfiguration(_configurationSource);
            }

            _currentController = nextController;
            SaveBackdrop(backdropName);
        }

        public string? GetSavedBackdropName()
        {
            if (_appData is null)
            {
                return null;
            }
            try
            {
                return _appData.LocalSettings.Values[BackdropKey] as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SaveBackdrop(string backdropName)
        {
            if (_appData is null)
            {
                return;
            }
            try
            {
                _appData.LocalSettings.Values[BackdropKey] = backdropName;
            }
            catch (Exception)
            {
                // Ignore persistence failures
            }
        }

        public void OnBackdropComboBoxSelectionChanged(object sender)
        {
            if (sender is ComboBox comboBox &&
                comboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag)
            {
                SetBackdrop(tag);
            }
        }

        public void SetBackdropComboBoxDefaultItem(ComboBox comboBox)
        {
            // SettingsPage defines items in order: None(0), Mica(1), Mica Alt(2), Acrylic(3), Acrylic Thin(4)
            int index = 1; // Default to Mica
            string? saved = GetSavedBackdropName();

            if (saved is not null)
            {
                if (saved == "None") index = 0;
                else if (saved == "MicaAlt") index = 2;
                else if (saved == "Acrylic") index = 3;
                else if (saved == "AcrylicThin") index = 4;
            }

            comboBox.SelectedIndex = index;
        }
    }
}