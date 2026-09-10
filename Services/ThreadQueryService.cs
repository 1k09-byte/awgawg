using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using stellarisKIT.Models;
using stellarisKIT.Native;

namespace stellarisKIT.Services;

/// <summary>One live thread row for the rule editor's thread picker.</summary>
public sealed class LiveThreadInfo
{
    public int Tid { get; set; }
    public int Pid { get; set; }
    public string Description { get; set; } = string.Empty;
    public string StartAddress { get; set; } = string.Empty;
    public int RelativeValue { get; set; }
    public string RelativeText { get; set; } = string.Empty;
    public int Base { get; set; }
}

/// <summary>
/// Enumerates live threads for the rule editor (description + start address +
/// current priority). Uses OS thread names only — no DbgHelp, so it stays fast
/// and never blocks on symbol downloads. Null when the process is gone.
/// </summary>
public static class ThreadQueryService
{
    /// <summary>System Informer style: "Time critical", "Below normal", "Custom (3)", "Custom (-4)".</summary>
    public static string FormatRelative(int level) => level switch
    {
        -15 => "Idle",
        -2 => "Lowest",
        -1 => "Below normal",
        0 => "Normal",
        1 => "Above normal",
        2 => "Highest",
        15 => "Time critical",
        _ => $"Custom ({level})",
    };

    public static async Task<List<LiveThreadInfo>> ListThreadsAsync(int pid)
    {
        return await Task.Run(() =>
        {
            var rows = new List<LiveThreadInfo>();
            Process proc;
            try
            {
                proc = Process.GetProcessById(pid);
            }
            catch
            {
                return rows;
            }

            using (proc)
            {
                ProcessThreadCollection threads;
                try
                {
                    threads = proc.Threads;
                }
                catch
                {
                    return rows;
                }

                foreach (ProcessThread t in threads)
                {
                    string desc = string.Empty;
                    string start = "-";
                    int relative = 0;
                    int basePri = 0;
                    try
                    {
                        desc = NativeSnapshotService.TryGetThreadDescription((uint)t.Id)
                            ?? "(unnamed)";
                        start = ResolveStart(proc, t);
                        relative = (int)t.PriorityLevel;
                        basePri = t.BasePriority;
                    }
                    catch
                    {
                        continue;
                    }

                    rows.Add(new LiveThreadInfo
                    {
                        Tid = t.Id,
                        Pid = pid,
                        Description = desc,
                        StartAddress = start,
                        RelativeValue = relative,
                        RelativeText = FormatRelative(relative),
                        Base = basePri,
                    });
                }
            }

            return rows.OrderBy(r => r.Tid).ToList();
        }).ConfigureAwait(false);
    }

    /// <summary>First running process matching a rule pattern (supports * wildcards, with/without .exe).</summary>
    public static int FindPid(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return 0;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (ProfileMatcher.Matches(pattern, proc.ProcessName))
                    {
                        return proc.Id;
                    }
                }
                catch
                {
                    continue;
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }
        return 0;
    }

    private static string ResolveStart(Process proc, ProcessThread t)
    {
        long addr = 0;
        try
        {
            addr = NativeSnapshotService.TryGetWin32StartAddress((uint)t.Id) ?? 0;
        }
        catch
        {
        }

        if (addr == 0)
        {
            try
            {
                addr = t.StartAddress.ToInt64();
            }
            catch
            {
                return "-";
            }
        }

        try
        {
            foreach (ProcessModule mod in proc.Modules)
            {
                long b = mod.BaseAddress.ToInt64();
                if (addr >= b && addr < b + mod.ModuleMemorySize)
                {
                    return $"{mod.ModuleName}+0x{(addr - b):X}";
                }
            }
        }
        catch
        {
        }

        return $"0x{addr:X}";
    }
}

/// <summary>Shared rule-pattern matching (supports * wildcards, with/without .exe).</summary>
public static class ProfileMatcher
{
    public static bool Matches(string pattern, string processName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(processName))
            return false;
        if (pattern == "*")
            return true;

        string p = pattern;
        string n = processName;
        if (string.Equals(p, n, StringComparison.OrdinalIgnoreCase))
            return true;
        if (SimpleMatch(p, n))
            return true;

        // Tolerate .exe on either side: "DiscordPTB" == "DiscordPTB.exe",
        // "Discord*" matches "DiscordPTB.exe", "*potify" matches "fastpotify.exe".
        string pNoExe = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
        string nNoExe = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
        if (string.Equals(pNoExe, nNoExe, StringComparison.OrdinalIgnoreCase))
            return true;
        if (SimpleMatch(pNoExe, nNoExe))
            return true;
        if (SimpleMatch(pNoExe, n))
            return true;
        if (SimpleMatch(p, nNoExe))
            return true;
        return false;
    }

    public static bool SimpleMatch(string pattern, string input)
    {
        if (pattern.StartsWith("*") && pattern.EndsWith("*") && pattern.Length > 1)
        {
            return input.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.StartsWith("*"))
        {
            return input.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.EndsWith("*"))
        {
            return input.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
