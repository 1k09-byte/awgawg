// Theme service contract.
// Written in the Windows App SDK C# dialect (see docs/GALLERY-REFERENCE.md section 2/5).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace stellarisKIT
{
    /// <summary>
    /// Manages the app's light/dark/system theme and persists the user's choice.
    /// </summary>
    public interface IThemeService
    {
        /// <summary>The currently active resolved theme.</summary>
        ElementTheme ElementTheme { get; }

        /// <summary>Binds to the application window and applies any persisted theme on startup.</summary>
        void Initialize(Window window);

        /// <summary>Applies the theme and persists the user's selection.</summary>
        void SetElementTheme(ElementTheme theme);

        /// <summary>Name of the persisted theme, or null when nothing is saved yet.</summary>
        string? GetSavedThemeName();

        /// <summary>Applies the theme selected in a Settings ComboBox (reads the item's Tag).</summary>
        void OnThemeComboBoxSelectionChanged(object sender);

        /// <summary>Selects the persisted theme in a Settings ComboBox.</summary>
        void SetThemeComboBoxDefaultItem(ComboBox comboBox);

        /// <summary>Applies the backdrop and persists the user's selection.</summary>
        void SetBackdrop(string backdropName);

        /// <summary>Name of the persisted backdrop, or null when nothing is saved yet.</summary>
        string? GetSavedBackdropName();

        /// <summary>Applies the backdrop selected in a Settings ComboBox (reads the item's Tag).</summary>
        void OnBackdropComboBoxSelectionChanged(object sender);

        /// <summary>Selects the persisted backdrop in a Settings ComboBox.</summary>
        void SetBackdropComboBoxDefaultItem(ComboBox comboBox);
    }
}