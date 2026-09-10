using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace stellarisKIT.Models;

public enum TunerPriorityClass : uint
{
    Idle = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
    Realtime = 0x100,
}

public enum TunerThreadPriority
{
    Idle = -15,
    Lowest = -2,
    BelowNormal = -1,
    Normal = 0,
    AboveNormal = 1,
    Highest = 2,
    TimeCritical = 15,
}

public partial class TunerProcessRow : ObservableObject
{
    [ObservableProperty] public partial int Pid { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Path { get; set; } = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackVisibility))]
    public partial Microsoft.UI.Xaml.Media.ImageSource? AppIcon { get; set; }

    public Microsoft.UI.Xaml.Visibility FallbackVisibility => AppIcon == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty] public partial string PriorityText { get; set; } = "?";
    [ObservableProperty] public partial uint PriorityValue { get; set; }
    [ObservableProperty] public partial string AffinitySummary { get; set; } = "?";
    [ObservableProperty] public partial ulong AffinityMask { get; set; }
    [ObservableProperty] public partial string PriorityBoostText { get; set; } = "Unknown";
    [ObservableProperty] public partial string Memory { get; set; } = "0 MB";
    [ObservableProperty] public partial int Threads { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "Running";
    [ObservableProperty] public partial long ContextSwitchesDelta { get; set; }
    [ObservableProperty] public partial long CyclesDelta { get; set; }
    [ObservableProperty] public partial string Rule { get; set; } = "None";

    [ObservableProperty] public partial string EfficiencyMode { get; set; } = "Disabled";
    [ObservableProperty] public partial string BoostText { get; set; } = "Unknown";
    [ObservableProperty] public partial bool EfficiencyModeBool { get; set; }
    [ObservableProperty] public partial string SettingsSummary { get; set; } = "Settings unavailable";
    [ObservableProperty] public partial double CpuPercent { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = string.Empty;
}

public partial class ThreadDiagnosticRow : ObservableObject
{
    [ObservableProperty] public partial int Tid { get; set; }
    [ObservableProperty] public partial string StartAddress { get; set; } = string.Empty;
    /// <summary>Raw Win32 start address used for reliable module/path lookup.</summary>
    [ObservableProperty] public partial long StartAddressValue { get; set; }
    [ObservableProperty] public partial int Base { get; set; }
    [ObservableProperty] public partial int Dynamic { get; set; }
    [ObservableProperty] public partial string State { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string Relative { get; set; } = string.Empty;
    
    [ObservableProperty] public partial string CpuText { get; set; } = "-";
    [ObservableProperty] public partial string SwitchesText { get; set; } = "-";
    [ObservableProperty] public partial string CyclesText { get; set; } = "-";
    [ObservableProperty] public partial string AffinityText { get; set; } = "Unknown";
    [ObservableProperty] public partial string BoostText { get; set; } = "Unknown";
    [ObservableProperty] public partial string EfficiencyText { get; set; } = "Unknown";
}

public partial class CoreCell : ObservableObject
{
    [ObservableProperty] public partial int Index { get; set; }
    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial bool IsIdeal { get; set; }
    [ObservableProperty] public partial int Rank { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial ulong CpuSetId { get; set; }

    public Microsoft.UI.Xaml.Visibility IdealVisibility => IsIdeal ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>
/// Affinity intent for preset installs. Resolved against the live topology
/// at install time into a concrete mask — never persisted, never shipped
/// as a static mask (core counts differ per machine).
/// </summary>
public enum AffinityScope
{
    Unset,
    PerformanceCores,
    EfficiencyCores,
    /// <summary>First hardware thread of each physical core (SMT off).</summary>
    SingleThreadPerCore,
    /// <summary>First group sharing one last-level cache (one AMD CCD).</summary>
    SharedCacheGroup,
    /// <summary>
    /// Game default chain: P-cores → first shared-L3 group → single thread
    /// per core → unset. First non-trivial mask wins.
    /// </summary>
    GameDefault,
}

public sealed class TunerThreadRule
{
    /// <summary>Thread description match (exact, case-insensitive). Empty = match any.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Start address match ("module+offset", exact, case-insensitive). Empty = match any.</summary>
    public string StartAddress { get; set; } = string.Empty;
    /// <summary>Win32 thread priority to enforce (e.g. 0 Normal, 15 TimeCritical, -4 custom).</summary>
    public int Priority { get; set; }
    /// <summary>Per-thread Efficiency Mode (EcoQoS). Null = leave unchanged.</summary>
    public bool? EfficiencyMode { get; set; }
    /// <summary>Per-thread dynamic priority boost. Null = leave unchanged.</summary>
    public bool? BoostEnabled { get; set; }
    /// <summary>Live threads this rule matched the last time it was evaluated. Display only.</summary>
    public int TargetCount { get; set; }

    [JsonIgnore]
    public string TargetText =>
        string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(StartAddress)
            ? "(any thread)"
            : string.Join(" · ", new[] { Description, StartAddress }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [JsonIgnore]
    public string DetailText
    {
        get
        {
            var parts = new List<string> { $"Priority: {Priority}" };
            if (EfficiencyMode.HasValue) parts.Add(EfficiencyMode.Value ? "Eco ON" : "Eco OFF");
            if (BoostEnabled.HasValue) parts.Add(BoostEnabled.Value ? "Boost ON" : "Boost OFF");
            return string.Join(" · ", parts);
        }
    }

    public TunerThreadRule Clone() => new()
    {
        Description = Description,
        StartAddress = StartAddress,
        Priority = Priority,
        EfficiencyMode = EfficiencyMode,
        BoostEnabled = BoostEnabled,
        TargetCount = TargetCount,
    };
}

public sealed partial class TunerProfile : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Pattern { get; set; } = "*";
    [ObservableProperty] public partial uint? PriorityClass { get; set; }
    [ObservableProperty] public partial bool? BoostEnabled { get; set; }
    [ObservableProperty] public partial ulong? AffinityMask { get; set; }
    [ObservableProperty] public partial List<ulong>? CpuSetIds { get; set; }
    [ObservableProperty] public partial bool? EfficiencyMode { get; set; }
    [ObservableProperty] public partial bool AutoApply { get; set; } = true;
    /// <summary>
    /// When true, this rule additionally triggers app-wide Gaming mode
    /// (background lowering + target boost) whenever the process launches.
    /// When the last matching process exits, Gaming mode restores everything.
    /// </summary>
    [ObservableProperty] public partial bool GamingModeAuto { get; set; }
    /// <summary>Master switch for the Enable / disable button. Matching requires Enabled.</summary>
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial ObservableCollection<TunerThreadRule> ThreadRules { get; set; } = new();
    /// <summary>Last apply outcome, e.g. "3/3 actions applied · 14:02:11" or "Waiting for process".</summary>
    [ObservableProperty] public partial string LastResult { get; set; } = "Waiting for process";

    /// <summary>Install-time affinity intent (presets only). Not persisted; resolved to <see cref="AffinityMask"/> on install.</summary>
    [JsonIgnore]
    public AffinityScope PresetAffinity { get; set; } = AffinityScope.Unset;

    [JsonIgnore]
    public string ProcessActionsSummary
    {
        get
        {
        var parts = new List<string>();
        if (GamingModeAuto) parts.Add("Gaming mode");
        if (PriorityClass.HasValue) parts.Add("Priority");
        if (BoostEnabled.HasValue) parts.Add("Boost");
        if (EfficiencyMode.HasValue) parts.Add("Efficiency");
        if (AffinityMask.HasValue) parts.Add("Affinity");
        if (CpuSetIds != null && CpuSetIds.Count > 0) parts.Add("CPU Sets");
        return parts.Count > 0 ? string.Join(", ", parts) : "–";
        }
    }

    [JsonIgnore]
    public string ThreadRulesSummary
    {
        get
        {
            if (ThreadRules == null || ThreadRules.Count == 0) return "–";
            int targets = ThreadRules.Sum(r => r.TargetCount);
            string rules = ThreadRules.Count == 1 ? "1 rule" : $"{ThreadRules.Count} rules";
            string t = targets == 1 ? "1 target" : $"{targets} targets";
            return $"{rules} · {t}";
        }
    }

    [JsonIgnore]
    public string StatusText => Enabled ? "Enabled" : "Disabled";

    [JsonIgnore]
    public int ProcessActionCount =>
        (GamingModeAuto ? 1 : 0) +
        (PriorityClass.HasValue ? 1 : 0) +
        (BoostEnabled.HasValue ? 1 : 0) +
        (EfficiencyMode.HasValue ? 1 : 0) +
        (AffinityMask.HasValue ? 1 : 0) +
        ((CpuSetIds != null && CpuSetIds.Count > 0) ? 1 : 0);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PriorityClass.HasValue) parts.Add($"Priority: {PriorityClass}");
            if (BoostEnabled.HasValue) parts.Add($"Boost: {BoostEnabled}");
            if (AffinityMask.HasValue) parts.Add($"Affinity: 0x{AffinityMask:X}");
            if (CpuSetIds != null && CpuSetIds.Count > 0) parts.Add($"Sets: {CpuSetIds.Count} assigned");
            if (EfficiencyMode.HasValue) parts.Add(EfficiencyMode.Value ? "Eco ON" : "Eco OFF");

            return parts.Count > 0 ? string.Join(" | ", parts) : "No changes defined";
        }
    }

    /// <summary>Refreshes all computed summary bindings after a batch edit.</summary>
    public void RefreshSummaries()
    {
        OnPropertyChanged(nameof(ProcessActionsSummary));
        OnPropertyChanged(nameof(ThreadRulesSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProcessActionCount));
        OnPropertyChanged(nameof(Summary));
    }

    public TunerProfile Clone()
    {
        var copy = new TunerProfile
        {
            Name = Name,
            Pattern = Pattern,
            PriorityClass = PriorityClass,
            BoostEnabled = BoostEnabled,
            AffinityMask = AffinityMask,
            CpuSetIds = CpuSetIds == null ? null : new List<ulong>(CpuSetIds),
            EfficiencyMode = EfficiencyMode,
            AutoApply = AutoApply,
            GamingModeAuto = GamingModeAuto,
            Enabled = Enabled,
            LastResult = LastResult,
            PresetAffinity = PresetAffinity,
        };
        if (ThreadRules != null)
        {
            foreach (var r in ThreadRules) copy.ThreadRules.Add(r.Clone());
        }
        return copy;
    }

    public void CopyFrom(TunerProfile source)
    {
        Name = source.Name;
        Pattern = source.Pattern;
        PriorityClass = source.PriorityClass;
        BoostEnabled = source.BoostEnabled;
        AffinityMask = source.AffinityMask;
        CpuSetIds = source.CpuSetIds == null ? null : new List<ulong>(source.CpuSetIds);
        EfficiencyMode = source.EfficiencyMode;
        AutoApply = source.AutoApply;
        GamingModeAuto = source.GamingModeAuto;
        Enabled = source.Enabled;
        LastResult = source.LastResult;
        PresetAffinity = source.PresetAffinity;
        ThreadRules.Clear();
        if (source.ThreadRules != null)
        {
            foreach (var r in source.ThreadRules) ThreadRules.Add(r.Clone());
        }
        RefreshSummaries();
    }
}

public sealed class TunerLogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public string TimeText => Time.ToString("HH:mm:ss");
}
