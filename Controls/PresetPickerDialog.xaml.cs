using Microsoft.UI.Xaml.Controls;
using stellarisKIT.Services;
using System.Collections.Generic;
using System.Linq;

namespace stellarisKIT.Controls;

public sealed partial class PresetPickerDialog : ContentDialog
{
    public List<GamePreset> Presets { get; } = new(GamePresets.All);

    public IReadOnlyList<GamePreset> SelectedPresets =>
        PresetList.SelectedItems.OfType<GamePreset>().ToList();

    /// <summary>Read at Apply time; no binding needed.</summary>
    public bool IncludeAffinity => AffinityCheck.IsChecked != false;

    public PresetPickerDialog()
    {
        this.InitializeComponent();
        Loaded += (_, _) => PresetList.SelectAll();
    }

    private void SelectAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => PresetList.SelectAll();

    private void Clear_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        PresetList.SelectedItems.Clear();
}
