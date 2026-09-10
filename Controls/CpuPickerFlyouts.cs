using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using stellarisKIT.Native;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace stellarisKIT.Controls;

/// <summary>
/// Shared CPU affinity / CPU Sets picker flyouts. Used by the rule editor
/// (per-rule values) and the threads window (immediate per-thread changes)
/// so both stay identical. Masks are single-group ulongs; multi-group
/// systems get an honest note instead of a silently group-0-only mask.
/// </summary>
public static class CpuPickerFlyouts
{
    public static async Task ShowAffinityPickerAsync(
        FrameworkElement anchor,
        int cpuCount,
        ulong initialMask,
        Action<ulong> onApply)
    {
        List<CpuSetEntry> topology = new();
        try
        {
            topology = await App.Current.CpuSets.GetTopologyAsync();
        }
        catch
        {
        }

        var entries = topology
            .Where(c => c.Group == 0 && c.LogicalIndex < 64)
            .OrderBy(c => c.LogicalIndex)
            .ToList();

        if (entries.Count == 0)
        {
            ShowFlatAffinityPicker(anchor, cpuCount, initialMask, onApply);
            return;
        }

        // Higher EfficiencyClass == faster P-core (per MS docs); rank 0 is fastest.
        var ranks = CpuSetService.RankClasses(entries);
        int maxRank = ranks.Count > 0 ? ranks.Values.Max() : 0;
        bool hybrid = ranks.Count > 1;

        var boxes = new List<CheckBox>();
        var panel = new StackPanel { Spacing = 6 };
        var summary = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        string CoreKind(byte cls)
        {
            if (!hybrid) return string.Empty;
            int r = ranks.TryGetValue(cls, out int rank) ? rank : maxRank;
            if (r == 0) return " · P-core";
            if (r == maxRank) return " · E-core";
            return $" · class {cls}";
        }

        foreach (var core in entries.GroupBy(c => c.CoreIndex).OrderBy(g => g.Min(c => c.LogicalIndex)))
        {
            var header = new TextBlock
            {
                Text = $"Core {core.Key}{CoreKind(core.First().EfficiencyClass)}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            panel.Children.Add(header);
            var indent = new StackPanel { Spacing = 2, Margin = new Thickness(16, 0, 0, 0) };
            foreach (var cpu in core.OrderBy(c => c.LogicalIndex))
            {
                string label = $"CPU {cpu.LogicalIndex}" + (cpu.Parked ? " · parked" : string.Empty);
                var cb = new CheckBox
                {
                    Content = label,
                    IsChecked = (initialMask & (1UL << cpu.LogicalIndex)) != 0,
                    Tag = (int)cpu.LogicalIndex,
                };
                cb.Checked += (_, _) => UpdatePickerSummary(summary, boxes);
                cb.Unchecked += (_, _) => UpdatePickerSummary(summary, boxes);
                boxes.Add(cb);
                indent.Children.Add(cb);
            }
            panel.Children.Add(indent);
        }

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddQuick(string label, Action apply)
        {
            var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
            b.Click += (_, _) => { apply(); UpdatePickerSummary(summary, boxes); };
            quick.Children.Add(b);
        }
        AddQuick("All", () => boxes.ForEach(b => b.IsChecked = true));
        AddQuick("None", () => boxes.ForEach(b => b.IsChecked = false));
        if (hybrid)
        {
            AddQuick("P-cores", () => boxes.ForEach(b => b.IsChecked = IsRank(b, ranks, entries, 0)));
            AddQuick("E-cores", () => boxes.ForEach(b => b.IsChecked = IsRank(b, ranks, entries, maxRank)));
        }
        AddQuick("No SMT", () =>
        {
            var firstPerCore = entries.GroupBy(c => c.CoreIndex)
                .ToDictionary(g => (int)g.Key, g => (int)g.Min(c => c.LogicalIndex));
            var seen = new HashSet<int>();
            boxes.ForEach(b =>
            {
                int bit = (int)b.Tag!;
                int core = entries.First(c => c.LogicalIndex == bit).CoreIndex;
                b.IsChecked = firstPerCore[core] == bit && seen.Add(core);
            });
        });

        UpdatePickerSummary(summary, boxes);

        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var flyout = new Flyout();
        var root = new StackPanel { Spacing = 8, MinWidth = 360 };
        root.Children.Add(quick);
        root.Children.Add(summary);
        if (topology.Select(c => c.Group).Distinct().Count() > 1)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Multiple processor groups detected — affinity applies to group 0 only.",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
        root.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(apply);
        flyout.Content = root;
        apply.Click += (_, _) =>
        {
            ulong mask = 0;
            foreach (var cb in boxes)
            {
                if (cb.IsChecked == true && cb.Tag is int bit) mask |= 1UL << bit;
            }
            if (mask != 0)
            {
                onApply(mask);
            }
            flyout.Hide();
        };
        flyout.ShowAt(anchor);
    }

    public static async Task ShowCpuSetsPickerAsync(
        FrameworkElement anchor,
        IReadOnlyList<ulong> currentIds,
        Action<List<ulong>, int> onApply)
    {
        List<CpuSetEntry> topology;
        try
        {
            topology = await App.Current.CpuSets.GetTopologyAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "CPU Sets",
                Content = new TextBlock
                {
                    Text = "Could not read CPU Sets topology: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
                XamlRoot = anchor.XamlRoot,
            }.ShowAsync();
            return;
        }

        var panel = new StackPanel { Spacing = 4 };
        var boxes = new List<CheckBox>();
        foreach (var cell in topology)
        {
            var cb = new CheckBox
            {
                Content = $"Core {cell.LogicalIndex} (Grp {cell.Group}) - Id {cell.Id}",
                IsChecked = currentIds.Count == 0 || currentIds.Contains(cell.Id),
                Tag = cell.Id,
            };
            boxes.Add(cb);
            panel.Children.Add(cb);
        }

        var summary = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        void RefreshSummary()
        {
            int n = 0;
            foreach (var cb in boxes)
            {
                if (cb.IsChecked == true) n++;
            }
            summary.Text = $"{n} of {boxes.Count} selected";
        }
        foreach (var cb in boxes)
        {
            cb.Checked += (_, _) => RefreshSummary();
            cb.Unchecked += (_, _) => RefreshSummary();
        }

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddQuick(string label, bool check)
        {
            var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
            b.Click += (_, _) =>
            {
                foreach (var cb in boxes) cb.IsChecked = check;
                RefreshSummary();
            };
            quick.Children.Add(b);
        }
        AddQuick("All", true);
        AddQuick("None", false);
        RefreshSummary();

        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var flyout = new Flyout();
        var root = new StackPanel { Spacing = 8, MinWidth = 360 };
        root.Children.Add(quick);
        root.Children.Add(summary);
        root.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(apply);
        flyout.Content = root;
        int total = topology.Count;
        apply.Click += (_, _) =>
        {
            var selected = new List<ulong>();
            foreach (var cb in boxes)
            {
                if (cb.IsChecked == true && cb.Tag is uint id) selected.Add(id);
            }
            if (selected.Count > 0)
            {
                onApply(selected, total);
            }
            flyout.Hide();
        };
        flyout.ShowAt(anchor);
    }

    private static bool IsRank(CheckBox box, Dictionary<byte, int> ranks, List<CpuSetEntry> entries, int want)
    {
        if (box.Tag is not int bit) return false;
        var entry = entries.FirstOrDefault(c => c.LogicalIndex == bit);
        if (entry == null) return false;
        return ranks.TryGetValue(entry.EfficiencyClass, out int rank) && rank == want;
    }

    private static void UpdatePickerSummary(TextBlock summary, List<CheckBox> boxes)
    {
        ulong mask = 0;
        int n = 0;
        foreach (var cb in boxes)
        {
            if (cb.IsChecked == true && cb.Tag is int bit)
            {
                mask |= 1UL << bit;
                n++;
            }
        }
        summary.Text = n == 0 ? "Nothing selected" : $"{n} of {boxes.Count} · 0x{mask:X}";
    }

    private static void ShowFlatAffinityPicker(
        FrameworkElement anchor,
        int cpuCount,
        ulong initialMask,
        Action<ulong> onApply)
    {
        var panel = new StackPanel { Spacing = 4 };
        var boxes = new List<CheckBox>();
        for (int i = 0; i < cpuCount && i < 64; i++)
        {
            var cb = new CheckBox { Content = $"CPU {i}", IsChecked = (initialMask & (1UL << i)) != 0, Tag = i };
            boxes.Add(cb);
            panel.Children.Add(cb);
        }

        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var flyout = new Flyout();
        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 320 });
        root.Children.Add(apply);
        flyout.Content = root;
        apply.Click += (_, _) =>
        {
            ulong mask = 0;
            foreach (var cb in boxes)
            {
                if (cb.IsChecked == true && cb.Tag is int bit) mask |= 1UL << bit;
            }
            if (mask != 0)
            {
                onApply(mask);
            }
            flyout.Hide();
        };
        flyout.ShowAt(anchor);
    }
}
