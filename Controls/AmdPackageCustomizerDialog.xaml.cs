using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using stellarisKIT.Models;
using stellarisKIT.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace stellarisKIT.Controls
{
    public sealed partial class AmdPackageCustomizerDialog : ContentDialog
    {
        private readonly RadeonPackageSlimmer _slimmer;

        public ObservableCollection<RadeonPackageItem> Packages { get; } = new();
        public ObservableCollection<RadeonScheduledTaskItem> ScheduledTasks { get; } = new();
        public ObservableCollection<RadeonDisplayComponentItem> DisplayComponents { get; } = new();

        public AmdPackageCustomizerDialog(
            List<RadeonPackageItem> packages,
            List<RadeonScheduledTaskItem> tasks,
            List<RadeonDisplayComponentItem> displayComponents,
            RadeonPackageSlimmer slimmer)
        {
            _slimmer = slimmer;
            InitializeComponent();

            foreach (var p in packages) Packages.Add(p);
            foreach (var t in tasks) ScheduledTasks.Add(t);
            foreach (var d in displayComponents) DisplayComponents.Add(d);

            _slimmer.ApplyPreset(Packages, SlimmerPreset.LowLatencyGaming);
        }

        private void SelectAllPackages_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in Packages) p.IsSelected = true;
        }

        private void SelectNonePackages_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in Packages) if (p.IsRemovable) p.IsSelected = false;
        }

        private void EnableAllTasks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ScheduledTasks) t.IsEnabled = true;
        }

        private void DisableAllTasks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ScheduledTasks) t.IsEnabled = false;
        }

        private void SelectAllDisplay_Click(object sender, RoutedEventArgs e)
        {
            foreach (var d in DisplayComponents) d.IsSelected = true;
        }

        private void SelectNoneDisplay_Click(object sender, RoutedEventArgs e)
        {
            foreach (var d in DisplayComponents) if (!d.IsRequired) d.IsSelected = false;
        }

        private void Preset_Checked(object sender, RoutedEventArgs e)
        {
            if (_slimmer == null || sender is not RadioButton rb || rb.Tag is not string tag)
                return;

            SlimmerPreset preset = tag switch
            {
                "DisplayOnly" => SlimmerPreset.DisplayOnly,
                "Gaming" => SlimmerPreset.LowLatencyGaming,
                "Full" => SlimmerPreset.FullExperience,
                _ => SlimmerPreset.Custom
            };

            if (preset != SlimmerPreset.Custom)
            {
                _slimmer.ApplyPreset(Packages, preset);

                if (preset == SlimmerPreset.LowLatencyGaming || preset == SlimmerPreset.DisplayOnly)
                {
                    foreach (var t in ScheduledTasks) t.IsEnabled = false;
                    foreach (var d in DisplayComponents)
                    {
                        if (d.IsTelemetry || d.InfFile.Contains("fendr", System.StringComparison.OrdinalIgnoreCase))
                            d.IsSelected = false;
                    }
                }
            }
        }
    }
}