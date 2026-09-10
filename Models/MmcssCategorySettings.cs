using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace stellarisKIT.Models;

/// <summary>Settings exposed by a Windows settings provider.</summary>
public interface IWindowsSettingsCategory
{
    string Name { get; }
}

/// <summary>Editable values stored below the MMCSS Task Categories registry key.</summary>
public sealed class MmcssCategorySettings : IWindowsSettingsCategory
{
    public string Name { get; set; } = string.Empty;
    public uint Priority { get; set; } = 6;
    public string SchedulingCategory { get; set; } = "Medium";
    public string SfioPriority { get; set; } = "Normal";
    /// <summary>True when this category has an explicit registry subkey.</summary>
    public bool IsConfigured { get; set; } = true;

    // Kept as a compatibility alias for existing bindings/callers.
    public bool IsEnabled
    {
        get => IsConfigured;
        set => IsConfigured = value;
    }

    public MmcssCategorySettings Clone() => new()
    {
        Name = Name,
        Priority = Priority,
        SchedulingCategory = SchedulingCategory,
        SfioPriority = SfioPriority,
        IsConfigured = IsConfigured,
    };
}

public sealed partial class MmcssCategoryItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _stateText = string.Empty;

    public MmcssCategoryItem(string name, bool enabled)
    {
        Name = name;
        IsEnabled = enabled;
        StateText = enabled ? "Configured" : "Windows defaults";
    }

    partial void OnIsEnabledChanged(bool value) => StateText = value ? "Configured" : "Windows defaults";
}

public sealed class WindowsPowerSettings
{
    public bool SerializeTimerExpiration { get; set; }
    public bool DisableInterruptRouting { get; set; }

    /// <summary>True when the value is explicitly present in the registry; absence means Windows' default behavior.</summary>
    public bool SerializeTimerExpirationConfigured { get; set; }
    public bool DisableInterruptRoutingConfigured { get; set; }
}

public static class MmcssValues
{
    public static IReadOnlyList<string> SchedulingCategories { get; } =
        new[] { "High", "Medium", "Low" };

    public static IReadOnlyList<string> SfioPriorities { get; } =
        new[] { "High", "Normal", "Low" };

    public static MmcssCategorySettings Defaults(string name) => new()
    {
        Name = name,
        Priority = 6,
        SchedulingCategory = "Medium",
        SfioPriority = "Normal",
        IsEnabled = true,
    };
}
