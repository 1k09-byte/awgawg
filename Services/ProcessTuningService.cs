using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using stellarisKIT.Models;
using stellarisKIT.Native;

namespace stellarisKIT.Services;

/// <summary>
/// Live process enumeration/sampling plus all mutating process controls
/// (priority, boost, affinity, efficiency mode, suspend/resume/terminate).
/// Native calls always run off the UI thread; collections are updated by the caller.
/// </summary>
public sealed class ProcessTuningService
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "smss.exe", "svchost.exe", "dwm.exe", "winlogon.exe", "fontdrvhost.exe",
    };

    private readonly NativeSnapshotService _snapshot = new();
    private readonly Dictionary<int, long> _lastTotalTicks = new();
    private readonly Dictionary<int, long> _lastContextSwitches = new();
    private readonly Dictionary<int, long> _lastCycles = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private int _logicalCount = -1;

    public bool SnapshotLimited => _snapshot.Limited;

    public static bool IsCritical(string name, int pid)
    {
        if (pid <= 4)
        {
            return true;
        }

        if (CriticalNames.Contains(name))
        {
            return true;
        }

        try
        {
            if (string.Equals(Process.GetProcessById(pid).ProcessName + ".exe", name, StringComparison.OrdinalIgnoreCase)
                && pid == Environment.ProcessId)
            {
                return true; // host app itself (handled with a warning, not a block)
            }
        }
        catch
        {
            // ignore
        }

        return CriticalNames.Contains(name);
    }

    public static bool IsSelf(int pid) => pid == Environment.ProcessId;

    public int LogicalProcessorCount()
    {
        if (_logicalCount <= 0)
        {
            _logicalCount = Environment.ProcessorCount;
        }

        return _logicalCount;
    }

    public async Task<List<TunerProcessRow>> ListProcessesAsync()
    {
        return await Task.Run(() =>
        {
            var rows = new List<TunerProcessRow>();
            Dictionary<int, string>? wmiNames = null;

            foreach (var proc in Process.GetProcesses())
            {
                int pid;
                try
                {
                    pid = proc.Id;
                }
                catch
                {
                    continue; // process died mid-enumeration
                }

                // Every process gets a row. Some (protected / system / elevated)
                // throw on ProcessName; fall back to a WMI lookup, then to a
                // bare PID label so the row is never silently dropped.
                string name;
                try
                {
                    name = proc.ProcessName + ".exe";
                }
                catch
                {
                    wmiNames ??= GetWmiProcessNames();
                    name = wmiNames.TryGetValue(pid, out string? wmiName) && !string.IsNullOrWhiteSpace(wmiName)
                        ? wmiName
                        : $"PID {pid}";
                }

                var row = new TunerProcessRow { Pid = pid, Name = name };
                FillStatic(row);
                rows.Add(row);
            }

            return rows;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// One WMI pass returning ProcessId → Name for every process; used as a name
    /// source for processes that deny direct ProcessName access. Runs once per
    /// enumeration and only when a fallback is actually needed.
    /// </summary>
    private static Dictionary<int, string> GetWmiProcessNames()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, Name FROM Win32_Process");
            foreach (System.Management.ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["ProcessId"] is { } pidValue
                        && int.TryParse(pidValue.ToString(), out int pid)
                        && obj["Name"] is { } nameValue
                        && !string.IsNullOrWhiteSpace(nameValue.ToString()))
                    {
                        map[pid] = nameValue.ToString();
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable: callers fall back to the bare PID label.
        }

        return map;
    }

    /// <summary>Samples CPU% for the given PIDs only (visible rows), not the whole system.</summary>
    public async Task SampleCpuAsync(IReadOnlyList<TunerProcessRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var deltas = await Task.Run(() =>
        {
            var now = DateTime.UtcNow;
            double intervalMs = Math.Max(1, (now - _lastSampleUtc).TotalMilliseconds);
            _lastSampleUtc = now;
            int cpus = LogicalProcessorCount();
            var result = new Dictionary<int, (double Cpu, long MemoryMb, int Threads, long ContextSwitches, long Cycles)>();
            
            var snapshotList = _snapshot.TrySnapshot();
            var snapDict = new Dictionary<int, stellarisKIT.Native.SnapshotProcess>();
            foreach (var s in snapshotList) snapDict[(int)s.ProcessId] = s;
            
            foreach (var row in rows)
            {
                try
                {
                    using var process = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)row.Pid);
                    if (process.IsInvalid)
                    {
                        continue;
                    }

                    double cpuPercent = 0;
                    if (NativeMethods.Times.GetProcessTimes(process, out _, out _, out var k, out var u))
                    {
                        long total = k.ToTicks() + u.ToTicks();
                        if (_lastTotalTicks.TryGetValue(row.Pid, out long prev))
                        {
                            double pct = ((total - prev) / 10000.0) / intervalMs * 100.0 / cpus;
                            cpuPercent = Math.Clamp(pct, 0, 100 * cpus);
                        }
                        _lastTotalTicks[row.Pid] = total;
                    }

                    long memoryMb = 0;
                    int threads = 0;
                    try
                    {
                        using var proc = Process.GetProcessById(row.Pid);
                        memoryMb = proc.WorkingSet64 / (1024 * 1024);
                        threads = proc.Threads.Count;
                    }
                    catch { }

                    long contextSwitchesDelta = 0;
                    long cyclesDelta = 0;
                    
                    if (snapDict.TryGetValue(row.Pid, out var snapProcess))
                    {
                        long totalSwitches = 0;
                        foreach (var thread in snapProcess.Threads)
                        {
                            totalSwitches += thread.ContextSwitches;
                        }
                        
                        long totalCycles = snapProcess.CycleTime;

                        if (_lastContextSwitches.TryGetValue(row.Pid, out long prevSwitches))
                        {
                            contextSwitchesDelta = totalSwitches - prevSwitches;
                        }
                        _lastContextSwitches[row.Pid] = totalSwitches;
                        
                        if (_lastCycles.TryGetValue(row.Pid, out long prevCycles))
                        {
                            cyclesDelta = totalCycles - prevCycles;
                        }
                        _lastCycles[row.Pid] = totalCycles;
                    }

                    result[row.Pid] = (cpuPercent, memoryMb, threads, Math.Max(0, contextSwitchesDelta), Math.Max(0, cyclesDelta));
                }
                catch
                {
                    // Per-row failure must never break the whole tick.
                }
            }

            return result;
        });

        foreach (var row in rows)
        {
            if (deltas.TryGetValue(row.Pid, out var metrics))
            {
                // Guarded writes: a same-value assignment still raises
                // PropertyChanged and re-renders the row, so only touch
                // properties that actually changed to keep the 2 s tick cheap.
                double cpu = Math.Round(metrics.Cpu, 1);
                if (row.CpuPercent != cpu) row.CpuPercent = cpu;

                string memory = $"{metrics.MemoryMb} MB";
                if (row.Memory != memory) row.Memory = memory;

                if (row.Threads != metrics.Threads) row.Threads = metrics.Threads;
                if (row.ContextSwitchesDelta != metrics.ContextSwitches) row.ContextSwitchesDelta = metrics.ContextSwitches;
                if (row.CyclesDelta != metrics.Cycles) row.CyclesDelta = metrics.Cycles;
            }
        }
    }

    public void PruneCache(IEnumerable<int> livePids)
    {
        var live = new HashSet<int>(livePids);
        foreach (var pid in _lastTotalTicks.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _lastTotalTicks.Remove(pid);
        }
        foreach (var pid in _lastContextSwitches.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _lastContextSwitches.Remove(pid);
            _lastCycles.Remove(pid);
        }
    }

    private static void FillStatic(TunerProcessRow row)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)row.Pid);
            if (process.IsInvalid)
            {
                row.Error = "No access";
                return;
            }

            uint cls = NativeMethods.Priority.GetPriorityClass(process);
            row.PriorityValue = cls;
            row.PriorityText = PriorityName(cls);

            if (NativeMethods.Affinity.GetProcessAffinityMask(process, out IntPtr mask, out IntPtr sys))
            {
                row.AffinityMask = (ulong)mask.ToInt64();
                row.AffinitySummary = AffinitySummary(row.AffinityMask, (ulong)sys.ToInt64());
            }

            try
            {
                using var proc = Process.GetProcessById(row.Pid);
                row.Path = SafeModuleName(proc);
                row.State = IsSuspended(proc) ? "Suspended" : "Running";
            }
            catch
            {
                // ignore
            }

            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                row.EfficiencyMode = ((state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0) ? "Enabled" : "Disabled";
            }
        }
        catch (Exception ex)
        {
            row.Error = ShortError(ex);
        }
    }

    /// <summary>
    /// A suspended process has every thread parked with WaitReason Suspended;
    /// detecting that lets the UI reflect Suspend/Resume instead of always
    /// claiming the process is running.
    /// </summary>
    private static bool IsSuspended(Process proc)
    {
        try
        {
            foreach (ProcessThread t in proc.Threads)
            {
                if (t.ThreadState == System.Diagnostics.ThreadState.Wait
                    && t.WaitReason == ThreadWaitReason.Suspended)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Process exited mid-scan; treat as running (the row will be removed
            // on the next poll anyway).
        }

        return false;
    }

    public static string PriorityName(uint cls) => cls switch
    {
        NativeMethods.Priority.Idle => "Idle",
        NativeMethods.Priority.BelowNormal => "Below Normal",
        NativeMethods.Priority.Normal => "Normal",
        NativeMethods.Priority.AboveNormal => "Above Normal",
        NativeMethods.Priority.High => "High",
        NativeMethods.Priority.Realtime => "Realtime",
        _ => $"0x{cls:X}",
    };

    public static string AffinitySummary(ulong mask, ulong systemMask)
    {
        if (mask == 0)
        {
            return "None!";
        }

        if (mask == systemMask)
        {
            return "All";
        }

        int bits = System.Numerics.BitOperations.PopCount(mask);
        int total = System.Numerics.BitOperations.PopCount(systemMask);
        return $"{bits}/{total} cores";
    }

    public async Task<(string Path, string CommandLine, string Times)> GetDetailsAsync(int pid)
    {
        return await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);

            string path = "";
            try
            {
                using var proc = Process.GetProcessById(pid);
                path = SafeModuleName(proc);
            }
            catch
            {
                // ignore
            }

            string cmd = NativeSnapshotService.TryGetCommandLine(process) ?? "(unavailable)";
            string times = "";
            if (NativeMethods.Times.GetProcessTimes(process, out var created, out _, out var k, out var u))
            {
                var start = DateTime.FromFileTimeUtc(created.ToTicks());
                var cpu = TimeSpan.FromTicks(k.ToTicks() + u.ToTicks());
                times = $"Started {start.ToLocalTime():g} · CPU kernel {TimeSpan.FromTicks(k.ToTicks()):g} / user {TimeSpan.FromTicks(u.ToTicks()):g} (total {cpu:g})";
            }

            return (path, cmd, times);
        }).ConfigureAwait(false);
    }

    public async Task SetPriorityAsync(int pid, uint priorityClass)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Priority.SetPriorityClass(process, priorityClass))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Setting priority class failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> GetBoostAsync(int pid)
    {
        return await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Priority.GetProcessPriorityBoost(process, out bool Svetlana))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Reading priority boost failed."));
            }

            return !Svetlana; // UI shows plain "enabled"; API is inverted.
        }).ConfigureAwait(false);
    }

    public async Task SetBoostAsync(int pid, bool enabled)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Priority.SetProcessPriorityBoost(process, Svetlana: !enabled))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Setting priority boost failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<ulong> GetAffinityAsync(int pid)
    {
        return await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Affinity.GetProcessAffinityMask(process, out IntPtr processMask, out _))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Reading affinity mask failed."));
            }
            return (ulong)processMask.ToInt64();
        });
    }

    public async Task SetAffinityAsync(int pid, ulong mask)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Affinity.SetProcessAffinityMask(process, (IntPtr)(long)mask))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Setting affinity mask failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task SetEfficiencyAsync(int pid, bool enabled)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            if (!NativeMethods.Power.SetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Setting Efficiency Mode failed."));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Reads back Efficiency Mode (EcoQoS) via GetProcessInformation.</summary>
    public async Task<bool> GetEfficiencyAsync(int pid)
    {
        return await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
            };
            if (!NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Reading Efficiency Mode failed."));
            }

            return (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
        }).ConfigureAwait(false);
    }

    public async Task SetPriorityBoostAsync(int pid, bool disablePriorityBoost)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Priority.SetProcessPriorityBoost(process, disablePriorityBoost))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Setting Priority Boost failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task SuspendProcessAsync(int pid)
    {
        await Task.Run(() =>
        {
            foreach (var tid in LiveThreadIds(pid))
            {
                using var thread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SuspendResume, false, tid);
                if (!thread.IsInvalid)
                {
                    NativeMethods.Threads.SuspendThread(thread);
                }
            }
        }).ConfigureAwait(false);
    }

    public async Task ResumeProcessAsync(int pid)
    {
        await Task.Run(() =>
        {
            foreach (var tid in LiveThreadIds(pid))
            {
                using var thread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SuspendResume, false, tid);
                if (thread.IsInvalid)
                {
                    continue;
                }

                // Drain client-unknown suspend counts so a stray suspend can't wedge the process.
                for (int i = 0; i < 8; i++)
                {
                    uint prev = NativeMethods.Threads.ResumeThread(thread);
                    if (prev == 0 || prev == uint.MaxValue)
                    {
                        break;
                    }
                }
            }
        }).ConfigureAwait(false);
    }

    public async Task TerminateProcessAsync(int pid, uint exitCode = 1)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.Terminate, false, (uint)pid);
            CpuSetService.ThrowIfInvalid(process, pid);
            if (!NativeMethods.Threads.TerminateProcess(process, exitCode))
            {
                throw CpuSetService.Friendly(pid, NativeSnapshotService.LastError("Terminating process failed."));
            }
        }).ConfigureAwait(false);
    }

    private static List<uint> LiveThreadIds(int pid)
    {
        var ids = new List<uint>();
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread t in proc.Threads)
            {
                ids.Add((uint)t.Id);
            }
        }
        catch
        {
            // Process exited mid-operation: caller treats empty as done.
        }

        return ids;
    }

    private static string SafeModuleName(Process proc)
    {
        try
        {
            return proc.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public async Task SuspendThreadAsync(int tid)
    {
        await Task.Run(() =>
        {
            IntPtr hThread = OpenThread(0x0002, false, (uint)tid);
            if (hThread != IntPtr.Zero)
            {
                SuspendThread(hThread);
                CloseHandle(hThread);
            }
        });
    }

    public async Task ResumeThreadAsync(int tid)
    {
        await Task.Run(() =>
        {
            IntPtr hThread = OpenThread(0x0002, false, (uint)tid);
            if (hThread != IntPtr.Zero)
            {
                ResumeThread(hThread);
                CloseHandle(hThread);
            }
        });
    }

    public async Task SetThreadPriorityAsync(int tid, stellarisKIT.Models.TunerThreadPriority priority)
    {
        await Task.Run(() =>
        {
            IntPtr hThread = OpenThread(0x0020, false, (uint)tid); // THREAD_SET_INFORMATION
            if (hThread != IntPtr.Zero)
            {
                SetThreadPriority(hThread, (int)priority);
                CloseHandle(hThread);
            }
        });
    }

    internal static string ShortError(Exception ex) => ex switch
    {
        Win32Exception w32 when w32.NativeErrorCode == NativeMethods.Win32Error.AccessDenied => "No access",
        InvalidOperationException => "Exited",
        _ => "Error",
    };
}
