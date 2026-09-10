using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using stellarisKIT.Models;
using stellarisKIT.Native;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace stellarisKIT.Controls;

public sealed class PriorityOption
{
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
}

public sealed partial class RuleEditorDialog : ContentDialog, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Draft under edit. Same instance for the dialog lifetime; LoadFrom copies into it.</summary>
    public TunerProfile Draft { get; } = new();

    private bool _priorityEnabled;
    public bool PriorityEnabled
    {
        get => _priorityEnabled;
        set { if (Set(ref _priorityEnabled, value)) UpdateSummary(); }
    }

    private string _priorityName = "Normal";
    public string PriorityName
    {
        get => _priorityName;
        set => Set(ref _priorityName, value);
    }

    private bool _boostEnabled;
    public bool BoostEnabled
    {
        get => _boostEnabled;
        set { if (Set(ref _boostEnabled, value)) UpdateSummary(); }
    }

    private string _boostName = "Enabled";
    public string BoostName
    {
        get => _boostName;
        set => Set(ref _boostName, value);
    }

    private bool _efficiencyEnabled;
    public bool EfficiencyEnabled
    {
        get => _efficiencyEnabled;
        set { if (Set(ref _efficiencyEnabled, value)) UpdateSummary(); }
    }

    private string _efficiencyName = "Disabled";
    public string EfficiencyName
    {
        get => _efficiencyName;
        set => Set(ref _efficiencyName, value);
    }

    private bool _gamingAutoChecked;
    /// <summary>Rule triggers automatic app-wide Gaming mode on process launch.</summary>
    public bool GamingAutoChecked
    {
        get => _gamingAutoChecked;
        set { if (Set(ref _gamingAutoChecked, value)) UpdateSummary(); }
    }

    private bool _affinityEnabled;
    public bool AffinityEnabled
    {
        get => _affinityEnabled;
        set { if (Set(ref _affinityEnabled, value)) UpdateSummary(); }
    }

    private bool _cpuSetsEnabled;
    public bool CpuSetsEnabled
    {
        get => _cpuSetsEnabled;
        set { if (Set(ref _cpuSetsEnabled, value)) UpdateSummary(); }
    }

    private string _threadSearch = string.Empty;
    public string ThreadSearch
    {
        get => _threadSearch;
        set { if (Set(ref _threadSearch, value)) ApplyLiveFilter(); }
    }

    public List<string> ProcessPriorityNames { get; } = new() { "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime" };
    public List<string> ToggleNames { get; } = new() { "Enabled", "Disabled" };

    public static List<PriorityOption> BaseThreadPriorities { get; } = new()
    {
        new() { Label = "Idle", Value = -15 },
        new() { Label = "Lowest", Value = -2 },
        new() { Label = "Below normal", Value = -1 },
        new() { Label = "Normal", Value = 0 },
        new() { Label = "Above normal", Value = 1 },
        new() { Label = "Highest", Value = 2 },
        new() { Label = "Time critical", Value = 15 },
    };

    public ObservableCollection<LiveThreadInfo> FilteredLiveThreads { get; } = new();

    private readonly List<LiveThreadInfo> _allLiveThreads = new();
    private ulong _pendingAffinityMask;
    private List<ulong> _pendingCpuSetIds = new();
    private int _cpuCount;
    private bool _threadsLoadedFor = false;

    public RuleEditorDialog()
    {
        this.InitializeComponent();
        try
        {
            _cpuCount = App.Current.ProcessTuning.LogicalProcessorCount();
        }
        catch
        {
            _cpuCount = Environment.ProcessorCount;
        }
        ulong all = _cpuCount >= 64 ? ulong.MaxValue : ((1UL << _cpuCount) - 1UL);
        _pendingAffinityMask = all;
        Draft.ThreadRules.CollectionChanged += (_, _) =>
        {
            UpdateSummary();
            UpdateSavedRulesMeta();
        };
        UpdateSummary();
    }

    public void LoadFrom(TunerProfile source, bool isNew)
    {
        Title = isNew ? "New rule" : "Edit rule";
        Draft.CopyFrom(source);
        PriorityEnabled = Draft.PriorityClass.HasValue;
        PriorityName = Draft.PriorityClass.HasValue ? PriorityClassToName(Draft.PriorityClass.Value) : "Normal";
        BoostEnabled = Draft.BoostEnabled.HasValue;
        BoostName = Draft.BoostEnabled.HasValue && !Draft.BoostEnabled.Value ? "Disabled" : "Enabled";
        EfficiencyEnabled = Draft.EfficiencyMode.HasValue;
        EfficiencyName = Draft.EfficiencyMode.HasValue && Draft.EfficiencyMode.Value ? "Enabled" : "Disabled";
        GamingAutoChecked = Draft.GamingModeAuto;
        AffinityEnabled = Draft.AffinityMask.HasValue;
        CpuSetsEnabled = Draft.CpuSetIds != null && Draft.CpuSetIds.Count > 0;
        _pendingAffinityMask = Draft.AffinityMask ?? AllMask();
        _pendingCpuSetIds = Draft.CpuSetIds == null ? new List<ulong>() : new List<ulong>(Draft.CpuSetIds);
        UpdateAffinitySummary();
        UpdateCpuSetsSummary();
        ShowTab("Settings");
        HideError();
        UpdateSummary();
        UpdateSavedRulesMeta();
    }

    // ---- tabs ----

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            ShowTab(tag);
        }
    }

    private void ShowTab(string tag)
    {
        bool settings = tag != "Threads";
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ThreadsPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        TabSettings.FontWeight = settings ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        TabThreads.FontWeight = settings ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;
        if (!settings)
        {
            _ = LoadLiveThreadsAsync();
        }
    }

    // ---- target suggestions ----

    private void TargetBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        try
        {
            string q = sender.Text ?? string.Empty;
            var names = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => n!.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .Take(12)
                .ToList();
            sender.ItemsSource = names;
        }
        catch
        {
        }
    }

    private void TargetBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s)
        {
            sender.Text = s;
        }
    }

    // ---- live threads ----

    private async Task LoadLiveThreadsAsync()
    {
        string target = (TargetBox.Text ?? string.Empty).Trim();
        _allLiveThreads.Clear();
        FilteredLiveThreads.Clear();
        LiveSelectedText.Text = "loading…";
        if (string.IsNullOrEmpty(target))
        {
            LiveSelectedText.Text = "0 selected · enter a target process";
            return;
        }

        int pid = await Task.Run(() => ThreadQueryService.FindPid(target));
        if (pid == 0)
        {
            LiveSelectedText.Text = "0 selected · process not running";
            return;
        }

        var rows = await ThreadQueryService.ListThreadsAsync(pid);
        // Pin named threads to the top (stable: TID order within each group),
        // mirroring the threads window — otherwise the actionable rows drown
        // below dozens of (unnamed) driver threads.
        _allLiveThreads.AddRange(rows
            .OrderBy(t => string.IsNullOrWhiteSpace(t.Description) || t.Description == "(unnamed)" ? 1 : 0)
            .ThenBy(t => t.Tid));
        ApplyLiveFilter();
        LiveSelectedText.Text = $"0 selected · right-click to add rules";
    }

    private void ApplyLiveFilter()
    {
        string q = (ThreadSearch ?? string.Empty).Trim();
        FilteredLiveThreads.Clear();
        foreach (var t in _allLiveThreads)
        {
            if (q.Length == 0
                || t.Tid.ToString().Contains(q)
                || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.StartAddress.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredLiveThreads.Add(t);
            }
        }
    }

    private async void ThreadsRefresh_Click(object sender, RoutedEventArgs e) => await LoadLiveThreadsAsync();

    private void LiveThreads_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = LiveThreadsList.SelectedItems.Count;
        LiveSelectedText.Text = $"{n} selected · right-click to add rules";
    }

    private void LiveThread_AddRule(object sender, RoutedEventArgs e)
    {
        IEnumerable<LiveThreadInfo> targets;
        if (sender is FrameworkElement fe && fe.DataContext is LiveThreadInfo single)
        {
            targets = new[] { single };
        }
        else
        {
            targets = LiveThreadsList.SelectedItems.OfType<LiveThreadInfo>().ToList();
        }

        foreach (var t in targets)
        {
            AddThreadRule(t);
        }
        Draft.RefreshSummaries();
    }

    private void AddThreadRule(LiveThreadInfo t)
    {
        if (string.IsNullOrWhiteSpace(t.Description) || t.Description == "(unnamed)")
        {
            return;
        }
        bool exists = Draft.ThreadRules.Any(r =>
            string.Equals(r.Description, t.Description, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.StartAddress, t.StartAddress, StringComparison.OrdinalIgnoreCase));
        if (exists) return;
        Draft.ThreadRules.Add(new TunerThreadRule
        {
            Description = t.Description,
            StartAddress = t.StartAddress,
            Priority = t.RelativeValue,
            TargetCount = 1,
        });
        Draft.RefreshSummaries();
    }

    private void SavedRule_Delete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TunerThreadRule rule)
        {
            Draft.ThreadRules.Remove(rule);
            Draft.RefreshSummaries();
        }
    }

    private void SavedRulePriority_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        // The DataContext here is the TunerThreadRule for the row.
        if (cb.DataContext is not TunerThreadRule rule) return;

        var options = BaseThreadPriorities
            .Select(o => new PriorityOption { Label = o.Label, Value = o.Value })
            .ToList();
        if (!options.Any(o => o.Value == rule.Priority))
        {
            options.Add(new PriorityOption
            {
                Label = ThreadQueryService.FormatRelative(rule.Priority),
                Value = rule.Priority,
            });
        }

        cb.SelectionChanged -= SavedRulePriority_Changed;
        cb.DisplayMemberPath = "Label";
        cb.SelectedValuePath = "Value";
        cb.ItemsSource = options;
        cb.SelectedValue = rule.Priority;
        cb.Tag = rule;
        cb.SelectionChanged += SavedRulePriority_Changed;
    }

    private void SavedRulePriority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.Tag is TunerThreadRule rule && cb.SelectedValue is int v)
        {
            rule.Priority = v;
        }
    }

    private void UpdateSavedRulesMeta()
    {
        int n = Draft.ThreadRules.Count;
        SavedRulesCountText.Text = n == 1 ? "1 rule" : $"{n} rules";
        NoThreadRulesText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRulesList.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- affinity / cpu sets choosers (flyouts; nested dialogs are not allowed) ----

    private ulong AllMask() => _cpuCount >= 64 ? ulong.MaxValue : ((1UL << _cpuCount) - 1UL);

    private void AffinityChoose_Click(object sender, RoutedEventArgs e)
    {
        _ = CpuPickerFlyouts.ShowAffinityPickerAsync(
            AffinityChooseButton,
            _cpuCount,
            _pendingAffinityMask,
            mask =>
            {
                _pendingAffinityMask = mask;
                AffinityEnabled = true;
                UpdateAffinitySummary();
                UpdateSummary();
            });
    }

    private void CpuSetsChoose_Click(object sender, RoutedEventArgs e)
    {
        _ = CpuPickerFlyouts.ShowCpuSetsPickerAsync(
            CpuSetsChooseButton,
            _pendingCpuSetIds,
            (ids, total) =>
            {
                _pendingCpuSetIds = ids;
                CpuSetsEnabled = true;
                UpdateCpuSetsSummary(total);
                UpdateSummary();
            });
    }

    private void UpdateAffinitySummary()
    {
        if (_pendingAffinityMask == AllMask())
        {
            AffinitySummaryText.Text = "All logical processors";
        }
        else
        {
            int n = 0;
            for (int i = 0; i < _cpuCount && i < 64; i++)
            {
                if ((_pendingAffinityMask & (1UL << i)) != 0) n++;
            }
            AffinitySummaryText.Text = $"{n} of {_cpuCount} · 0x{_pendingAffinityMask:X}";
        }
    }

    private void UpdateCpuSetsSummary(int total = -1)
    {
        if (_pendingCpuSetIds.Count == 0 || (total >= 0 && _pendingCpuSetIds.Count >= total))
        {
            CpuSetsSummaryText.Text = "All logical processors";
        }
        else
        {
            CpuSetsSummaryText.Text = $"{_pendingCpuSetIds.Count} selected";
        }
    }

    // ---- summary / validation ----

    private int EnabledActionCount =>
        (PriorityEnabled ? 1 : 0) +
        (BoostEnabled ? 1 : 0) +
        (EfficiencyEnabled ? 1 : 0) +
        (AffinityEnabled ? 1 : 0) +
        (CpuSetsEnabled ? 1 : 0);

    private void UpdateSummary()
    {
        int a = EnabledActionCount;
        int t = Draft.ThreadRules.Count;
        SummaryText.Text = $"{a} process setting{(a == 1 ? "" : "s")} · {t} thread rule{(t == 1 ? "" : "s")}";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private static uint PriorityNameToValue(string name) => name switch
    {
        "Idle" => 0x40,
        "BelowNormal" => 0x4000,
        "Normal" => 0x20,
        "AboveNormal" => 0x8000,
        "High" => 0x80,
        "Realtime" => 0x100,
        _ => 0x20,
    };

    private static string PriorityClassToName(uint value) => value switch
    {
        0x40 => "Idle",
        0x4000 => "BelowNormal",
        0x20 => "Normal",
        0x8000 => "AboveNormal",
        0x80 => "High",
        0x100 => "Realtime",
        _ => "Normal",
    };

    /// <summary>True when the dialog was accepted via the Ctrl+Enter shortcut (Hide carries no result).</summary>
    public bool ShortcutAccepted { get; private set; }

    private void Dialog_PrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!TryCommit())
        {
            args.Cancel = true;
        }
    }

    /// <summary>Validates and pushes editor state into <see cref="Draft"/>.</summary>
    private bool TryCommit()
    {
        HideError();
        string target = (TargetBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(target))
        {
            ShowError("Enter a target process name or pattern.");
            return false;
        }
        if (EnabledActionCount == 0 && !Draft.GamingModeAuto && Draft.ThreadRules.Count == 0)
        {
            ShowError("Enable at least one process setting or add one thread rule.");
            return false;
        }

        Draft.Name = target.Trim('*');
        if (string.IsNullOrEmpty(Draft.Name)) Draft.Name = target;
        Draft.Pattern = target;
        Draft.PriorityClass = PriorityEnabled ? PriorityNameToValue(PriorityName) : null;
        Draft.BoostEnabled = BoostEnabled ? BoostName == "Enabled" : null;
        Draft.EfficiencyMode = EfficiencyEnabled ? EfficiencyName == "Enabled" : null;
        Draft.GamingModeAuto = GamingAutoChecked;
        Draft.AffinityMask = AffinityEnabled ? _pendingAffinityMask : null;
        Draft.CpuSetIds = CpuSetsEnabled && _pendingCpuSetIds.Count > 0 ? new List<ulong>(_pendingCpuSetIds) : null;
        Draft.RefreshSummaries();
        return true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            if (TryCommit())
            {
                ShortcutAccepted = true;
                Hide();
            }
            e.Handled = true;
        }
    }
}
