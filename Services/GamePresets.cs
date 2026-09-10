using System;
using System.Collections.Generic;
using System.Linq;
using stellarisKIT.Models;

namespace stellarisKIT.Services;

/// <summary>One installable preset: a game (or group) plus the rules it creates.</summary>
public sealed class GamePreset
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<TunerProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Curated game presets. Conservative by design:
/// games get High priority + boost + Eco explicitly OFF, launchers and
/// clients get BelowNormal + Eco ON. No affinity/CPU-set restrictions
/// (wrong core counts hurt more than they help).
///
/// Thread rules exist only where the thread name is engine-stable across
/// builds: Unreal Engine has named its main threads "GameThread" and
/// "RenderThread" for a decade (Fortnite UE5, Valorant UE4), so a
/// description-exact match is build-independent. They get a mild +1
/// (AboveNormal) — never TimeCritical — and a missing thread simply
/// reports 0 targets instead of touching anything.
/// Thread rules for other engines are deliberately absent: their thread
/// names vary per build and a blind guess would be worse than nothing.
///
/// NEVER touches anti-cheat (Vanguard vgc/vgk, EasyAntiCheat, BattlEye) or
/// Windows system processes — modifying those risks bans or instability.
/// </summary>
public static class GamePresets
{
    private const uint High = 0x80;
    private const uint BelowNormal = 0x4000;
    private const int AboveNormal = 1;

    public static IReadOnlyList<GamePreset> All { get; } = new List<GamePreset>
    {
        new()
        {
            Name = "Roblox",
            Description = "RobloxPlayerBeta.exe → High priority + boost",
            Profiles = new() { GameRule("RobloxPlayerBeta.exe", "RobloxPlayerBeta.exe") },
        },
        new()
        {
            Name = "Fortnite",
            Description = "Client → High priority + boost · Render/Game threads → Above normal · Epic launcher → Eco",
            Profiles = new()
            {
                GameRule("FortniteClient-Win64-Shipping.exe", "FortniteClient-Win64-Shipping.exe", unrealThreads: true),
                LauncherRule("EpicGamesLauncher.exe", "EpicGamesLauncher.exe"),
            },
        },
        new()
        {
            Name = "Valorant",
            Description = "Client → High priority + boost · Render/Game threads → Above normal · Riot Client → Eco (Vanguard untouched)",
            Profiles = new()
            {
                GameRule("VALORANT-Win64-Shipping.exe", "VALORANT-Win64-Shipping.exe", unrealThreads: true),
                LauncherRule("RiotClientServices.exe", "RiotClientServices.exe"),
            },
        },
        new()
        {
            Name = "Counter-Strike 2",
            Description = "cs2.exe → High priority + boost · Steam → Eco",
            Profiles = new()
            {
                GameRule("cs2.exe", "cs2.exe"),
                LauncherRule("steam.exe", "steam.exe"),
            },
        },
        new()
        {
            Name = "Apex Legends",
            Description = "r5apex.exe → High priority + boost · Steam → Eco",
            Profiles = new()
            {
                GameRule("r5apex.exe", "r5apex.exe"),
                LauncherRule("steam.exe", "steam.exe"),
            },
        },
        new()
        {
            Name = "Call of Duty",
            Description = "cod.exe HQ + year-coded title exes (*-cod.exe) → High priority + boost · Steam → Eco",
            Profiles = new()
            {
                GameRule("cod.exe", "cod.exe"),
                GameRule("*-cod.exe", "*-cod.exe"),
                LauncherRule("steam.exe", "steam.exe"),
            },
        },
        new()
        {
            Name = "GTA V",
            Description = "GTA5.exe + GTA5_Enhanced.exe → High priority + boost",
            Profiles = new()
            {
                GameRule("GTA5.exe", "GTA5.exe"),
                GameRule("GTA5_Enhanced.exe", "GTA5_Enhanced.exe"),
            },
        },
        new()
        {
            Name = "League of Legends",
            Description = "Client → High priority + boost · Riot Client → Eco (Vanguard untouched)",
            Profiles = new()
            {
                GameRule("League of Legends.exe", "League of Legends.exe"),
                LauncherRule("RiotClientServices.exe", "RiotClientServices.exe"),
            },
        },
        new()
        {
            Name = "Overwatch 2",
            Description = "Overwatch.exe → High priority + boost · Battle.net → Eco",
            Profiles = new()
            {
                GameRule("Overwatch.exe", "Overwatch.exe"),
                LauncherRule("Battle.net.exe", "Battle.net.exe"),
            },
        },
        new()
        {
            Name = "Minecraft (Bedrock)",
            Description = "Minecraft.Windows.exe → High priority + boost",
            Profiles = new() { GameRule("Minecraft.Windows.exe", "Minecraft.Windows.exe") },
        },
    };

    private static TunerProfile GameRule(string name, string pattern, bool unrealThreads = false) => WithAffinity(new TunerProfile
    {
        Name = name,
        Pattern = pattern,
        PriorityClass = High,
        BoostEnabled = true,
        EfficiencyMode = false,
        AutoApply = true,
        Enabled = true,
        // Game rules arm app-wide Gaming mode automatically: the watcher
        // boosts + lowers background load at launch and restores on exit.
        GamingModeAuto = true,
    }, AffinityScope.GameDefault, unrealThreads);

    private static TunerProfile LauncherRule(string name, string pattern) => WithAffinity(new TunerProfile
    {
        Name = name + " (Eco)",
        Pattern = pattern,
        PriorityClass = BelowNormal,
        EfficiencyMode = true,
        AutoApply = true,
        Enabled = true,
    }, AffinityScope.EfficiencyCores, false);

    private static TunerProfile WithAffinity(TunerProfile profile, AffinityScope scope, bool unrealThreads)
    {
        profile.PresetAffinity = scope;
        if (unrealThreads)
        {
            // Description-exact: no start address, so offsets can't go stale.
            profile.ThreadRules.Add(new TunerThreadRule { Description = "RenderThread", Priority = AboveNormal });
            profile.ThreadRules.Add(new TunerThreadRule { Description = "GameThread", Priority = AboveNormal });
        }
        return profile;
    }

    /// <summary>
    /// Computes an affinity mask for a scope against live topology.
    /// Returns null when the scope can't be honored — the caller then leaves
    /// affinity unset rather than applying a wrong mask. Cases:
    /// multi-group systems (&gt;64 logical CPUs, masks are per-group),
    /// homogeneous CPUs for P/E-core scopes, single-cache/single-thread
    /// systems where the scope would be a no-op. Higher EfficiencyClass ==
    /// faster P-core (per MS docs).
    /// </summary>
    public static ulong? ComputeAffinityMask(
        IReadOnlyList<stellarisKIT.Native.CpuSetEntry> topology,
        AffinityScope scope)
    {
        if (scope == AffinityScope.Unset || topology == null || topology.Count == 0)
        {
            return null;
        }

        // Affinity masks are group-relative ulongs; spanning groups would
        // silently pin to group 0, so decline instead.
        if (topology.Select(c => c.Group).Distinct().Count() > 1)
        {
            return null;
        }

        var entries = topology.Where(c => c.Group == 0 && c.LogicalIndex < 64).ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        switch (scope)
        {
            case AffinityScope.PerformanceCores:
            case AffinityScope.EfficiencyCores:
                return ClassMask(entries, scope == AffinityScope.PerformanceCores);
            case AffinityScope.SingleThreadPerCore:
                return FirstThreadMask(entries);
            case AffinityScope.SharedCacheGroup:
                return FirstCacheGroupMask(entries);
            case AffinityScope.GameDefault:
                return ClassMask(entries, performance: true)
                    ?? FirstCacheGroupMask(entries)
                    ?? FirstThreadMask(entries);
            default:
                return null;
        }
    }

    private static ulong? ClassMask(List<stellarisKIT.Native.CpuSetEntry> entries, bool performance)
    {
        var ranks = CpuSetService.RankClasses(entries);
        if (ranks.Count < 2)
        {
            return null; // Homogeneous CPU: nothing to isolate.
        }
        int want = performance ? 0 : ranks.Values.Max();
        ulong mask = 0;
        foreach (var cpu in entries)
        {
            if (ranks.TryGetValue(cpu.EfficiencyClass, out int rank) && rank == want)
            {
                mask |= 1UL << cpu.LogicalIndex;
            }
        }
        return mask == 0 ? null : mask;
    }

    private static ulong? FirstThreadMask(List<stellarisKIT.Native.CpuSetEntry> entries)
    {
        ulong mask = 0;
        foreach (var core in entries.GroupBy(c => c.CoreIndex))
        {
            mask |= 1UL << core.Min(c => c.LogicalIndex);
        }
        return mask == 0 || mask == AllBits(entries) ? null : mask;
    }

    private static ulong? FirstCacheGroupMask(List<stellarisKIT.Native.CpuSetEntry> entries)
    {
        var first = entries.GroupBy(c => c.LastLevelCacheIndex)
            .OrderBy(g => g.Min(c => c.LogicalIndex))
            .First();
        ulong mask = 0;
        foreach (var cpu in first)
        {
            mask |= 1UL << cpu.LogicalIndex;
        }
        ulong all = AllBits(entries);
        // A single cache group covering everything (or nothing) is a no-op.
        return mask == 0 || mask == all ? null : mask;
    }

    private static ulong AllBits(List<stellarisKIT.Native.CpuSetEntry> entries)
    {
        ulong all = 0;
        foreach (var cpu in entries)
        {
            all |= 1UL << cpu.LogicalIndex;
        }
        return all;
    }

    /// <summary>Profiles from the given presets, skipping patterns that already have a rule.</summary>
    public static List<TunerProfile> BuildNew(
        IEnumerable<GamePreset> presets,
        IEnumerable<string> existingPatterns,
        Func<AffinityScope, ulong?>? resolveAffinity = null)
    {
        var existing = new HashSet<string>(
            existingPatterns.Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);
        var fresh = new List<TunerProfile>();
        foreach (var preset in presets)
        {
            foreach (var profile in preset.Profiles)
            {
                if (existing.Add(profile.Pattern))
                {
                    var clone = profile.Clone();
                    if (clone.PresetAffinity != AffinityScope.Unset && resolveAffinity != null)
                    {
                        ulong? mask = null;
                        try
                        {
                            mask = resolveAffinity(clone.PresetAffinity);
                        }
                        catch
                        {
                        }
                        if (mask.HasValue && mask.Value != 0)
                        {
                            clone.AffinityMask = mask.Value;
                        }
                    }
                    clone.PresetAffinity = AffinityScope.Unset;
                    fresh.Add(clone);
                }
            }
        }
        return fresh;
    }
}
