using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace stellarisKIT.Models;

/// <summary>
/// Root model of a Windhawk user-data backup (format "windhawk-user-data-v1").
/// This is the same format Windhawk 2.0's built-in backup/restore feature and
/// its windhawk-cli.exe "data import" command consume natively.
/// </summary>
public sealed class WindhawkBackup
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("appSettings")]
    public WindhawkAppSettings? AppSettings { get; set; }

    [JsonPropertyName("mods")]
    public List<WindhawkModEntry> Mods { get; set; } = new();
}

/// <summary>
/// Windhawk application-level settings ("Advanced" tab values). Everything is
/// optional so partial backups apply cleanly; nested engine settings use
/// Windhawk's dotted flat form (e.g. "engine.injectIntoGames") when written to
/// the registry/CLI.
/// </summary>
public sealed class WindhawkAppSettings
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    [JsonPropertyName("disableUpdateCheck")]
    public bool? DisableUpdateCheck { get; set; }

    [JsonPropertyName("devModeOptOut")]
    public bool? DevModeOptOut { get; set; }

    [JsonPropertyName("hideTrayIcon")]
    public bool? HideTrayIcon { get; set; }

    [JsonPropertyName("alwaysCompileModsLocally")]
    public bool? AlwaysCompileModsLocally { get; set; }

    [JsonPropertyName("dontAutoShowToolkit")]
    public bool? DontAutoShowToolkit { get; set; }

    [JsonPropertyName("modTasksDialogDelay")]
    public int? ModTasksDialogDelay { get; set; }

    [JsonPropertyName("loggingVerbosity")]
    public int? LoggingVerbosity { get; set; }

    [JsonPropertyName("engine")]
    public WindhawkEngineSettings? Engine { get; set; }
}

/// <summary>Windhawk engine sub-settings (the "Engine" section of app settings).</summary>
public sealed class WindhawkEngineSettings
{
    [JsonPropertyName("loggingVerbosity")]
    public int? LoggingVerbosity { get; set; }

    [JsonPropertyName("include")]
    public List<string>? Include { get; set; }

    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }

    [JsonPropertyName("injectIntoCriticalProcesses")]
    public bool? InjectIntoCriticalProcesses { get; set; }

    [JsonPropertyName("injectIntoIncompatiblePrograms")]
    public bool? InjectIntoIncompatiblePrograms { get; set; }

    [JsonPropertyName("injectIntoGames")]
    public bool? InjectIntoGames { get; set; }
}

/// <summary>One mod entry inside a windhawk-user-data-v1 backup.</summary>
public sealed class WindhawkModEntry
{
    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Flat per-mod settings as stored in the backup: keys use Windhawk's
    /// flattened array form (e.g. "controlStyles[0].target") and values are
    /// JSON scalars (string / number / bool).
    /// </summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, object?>? Settings { get; set; }
}

/// <summary>Per-mod import outcome, used for the end-of-import summary.</summary>
public sealed record WindhawkModImportOutcome(string ModId, bool Success, string? Reason);

/// <summary>Aggregate result of an ImportBackupAsync run.</summary>
public sealed class WindhawkImportResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<WindhawkModImportOutcome> Outcomes { get; } = new();

    public string SummaryText
    {
        get
        {
            var failures = new List<string>();
            foreach (var outcome in Outcomes)
            {
                if (!outcome.Success && outcome.Reason is not null)
                    failures.Add($"{outcome.ModId}: {outcome.Reason}");
            }

            var text = $"Imported {Imported} of {Imported + Skipped} mods";
            if (Skipped > 0) text += $", {Skipped} skipped";
            if (failures.Count > 0) text += ": " + string.Join("; ", failures);
            return text;
        }
    }
}
