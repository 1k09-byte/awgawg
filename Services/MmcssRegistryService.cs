using Microsoft.Win32;
using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Principal;

namespace stellarisKIT.Services;

/// <summary>Registry provider for Windows settings pages. More providers can be added later.</summary>
public sealed class MmcssRegistryService
{
    private const string TaskCategoriesPath = @"SYSTEM\CurrentControlSet\Control\MMCSS\Task Categories";
    private const string LegacyTaskCategoriesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks";
    private const string MmcssServicePath = @"SYSTEM\CurrentControlSet\Services\MMCSS";
    private const string PowerSchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string DefaultSchemeId = "458cc020-e29f-410a-b333-3168be05f157";
    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    public static readonly IReadOnlyList<string> KnownCategories = new[]
    {
        "Audio", "Games", "Pro Audio", "Playback", "Capture", "Distribution", "Window Manager"
    };

    public bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public int GetServiceStartType()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(MmcssServicePath, false);
        return key?.GetValue("Start", 2) is int value ? value : 2;
    }

    public WindowsPowerSettings GetPowerSettings()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var schemes = baseKey.OpenSubKey(PowerSchemesPath, false);
        var schemeId = schemes?.GetValue("ActivePowerScheme")?.ToString();
        if (string.IsNullOrWhiteSpace(schemeId)) schemeId = DefaultSchemeId;
        using var key = schemes?.OpenSubKey(schemeId, false);
        // These values are optional. Missing values mean Windows' default behavior,
        // so a missing active-scheme key must not make the WS page fail to load.
        return new WindowsPowerSettings
        {
            SerializeTimerExpiration = ReadDword(key, "SerializeTimerExpiration", 0) != 0,
            DisableInterruptRouting = ReadDword(key, "DisableInterruptRouting", 0) != 0,
            // Detection for the toggles this app writes: report whether each
            // value is explicitly configured (present in the registry) or still
            // at the Windows default (value absent).
            SerializeTimerExpirationConfigured = HasValue(key, "SerializeTimerExpiration"),
            DisableInterruptRoutingConfigured = HasValue(key, "DisableInterruptRouting"),
        };
    }

    private static bool HasValue(RegistryKey? key, string name)
        => key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;

    public void SavePowerSettings(WindowsPowerSettings settings)
    {
        EnsureAdministrator();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var schemes = baseKey.OpenSubKey(PowerSchemesPath, true)
            ?? throw new InvalidOperationException("The Windows power-scheme registry key could not be opened.");
        var schemeId = schemes.GetValue("ActivePowerScheme")?.ToString();
        if (string.IsNullOrWhiteSpace(schemeId)) schemeId = DefaultSchemeId;
        using var key = schemes.OpenSubKey(schemeId, true)
            ?? throw new InvalidOperationException("The active Windows power scheme could not be opened.");
        key.SetValue("SerializeTimerExpiration", settings.SerializeTimerExpiration ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DisableInterruptRouting", settings.DisableInterruptRouting ? 1 : 0, RegistryValueKind.DWord);
    }

    public void SetServiceEnabled(bool enabled)
    {
        EnsureAdministrator();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(MmcssServicePath, true)
            ?? throw new InvalidOperationException("The MMCSS service registry key could not be opened.");
        // Start=4 disables the service; Start=2 restores the normal automatic
        // start configuration. This changes configuration only; it does not
        // stop a running service or kill multimedia workloads.
        key.SetValue("Start", enabled ? 2 : 4, RegistryValueKind.DWord);
    }

    public IReadOnlyList<MmcssCategorySettings> GetTaskCategories()
    {
        using var root = OpenCategories(false);
        // Windows can omit this optional root on clean/default installations.
        // In that case expose the known categories using their documented defaults.
        var registryNames = root?.GetSubKeyNames() ?? Empty;
        var names = KnownCategories.Concat(registryNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => KnownCategories.Contains(name, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
        var result = new List<MmcssCategorySettings>();
        foreach (var name in names)
        {
            using var key = root?.OpenSubKey(name, false);
            result.Add(ReadCategory(name, key));
        }
        return result;
    }

    public MmcssCategorySettings GetCategorySettings(string name)
    {
        ValidateName(name);
        using var root = OpenCategories(false);
        using var key = root?.OpenSubKey(name, false);
        // An absent root/category is valid: MMCSS then uses Windows defaults.
        return ReadCategory(name, key);
    }

    public void SaveCategorySettings(string name, MmcssCategorySettings settings)
    {
        ValidateName(name); EnsureAdministrator(); ValidateSettings(settings);
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = baseKey.CreateSubKey(TaskCategoriesPath, true)
            ?? throw new InvalidOperationException("The MMCSS task-category registry key could not be opened.");
        using var key = root.CreateSubKey(name, true);
        key.SetValue("Priority", settings.Priority, RegistryValueKind.DWord);
        key.SetValue("Scheduling Category", settings.SchedulingCategory, RegistryValueKind.String);
        key.SetValue("SFIO Priority", settings.SfioPriority, RegistryValueKind.String);
    }

    private static MmcssCategorySettings ReadCategory(string name, RegistryKey? key)
    {
        if (key == null)
        {
            var defaults = MmcssValues.Defaults(name);
            defaults.IsConfigured = false;
            return defaults;
        }
        return new MmcssCategorySettings
        {
            Name = name, IsConfigured = true,
            Priority = ReadDword(key, "Priority", 6),
            SchedulingCategory = ReadString(key, "Scheduling Category", "Medium"),
            SfioPriority = ReadString(key, "SFIO Priority", "Normal")
        };
    }

    private static uint ReadDword(RegistryKey? key, string name, uint fallback)
    {
        if (key == null) return fallback;
        object? value = key.GetValue(name, fallback, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int i && i >= 0 ? (uint)i : value is uint u ? u : fallback;
    }

    private static string ReadString(RegistryKey? key, string name, string fallback) => key?.GetValue(name, fallback)?.ToString() ?? fallback;

    private static RegistryKey? OpenCategories(bool writable)
    {
        // Do not dispose the base key before returning a child key. Keeping the
        // parent alive avoids invalidating the returned handle on some Windows
        // RegistryKey implementations.
        var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            var requested = baseKey.OpenSubKey(TaskCategoriesPath, writable);
            if (requested != null && requested.GetSubKeyNames().Length > 0)
            {
                baseKey.Dispose();
                return requested;
            }
            requested?.Dispose();
            var fallback = baseKey.OpenSubKey(LegacyTaskCategoriesPath, writable);
            baseKey.Dispose();
            return fallback;
        }
        catch
        {
            baseKey.Dispose();
            throw;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') || name is "." or "..")
            throw new ArgumentException("Invalid MMCSS task category.", nameof(name));
    }

    private static void ValidateSettings(MmcssCategorySettings settings)
    {
        if (settings.Priority > 31) throw new ArgumentOutOfRangeException(nameof(settings.Priority));
        if (!MmcssValues.SchedulingCategories.Contains(settings.SchedulingCategory, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Invalid scheduling category.");
        if (!MmcssValues.SfioPriorities.Contains(settings.SfioPriority, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Invalid SFIO priority.");
    }

    private void EnsureAdministrator()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Administrator permission is required to change Windows settings.");
    }
}
