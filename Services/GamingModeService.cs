using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using stellarisKIT.Models;
using stellarisKIT.Native;

namespace stellarisKIT.Services;

public enum CpuBoundState
{
    /// <summary>Not enough samples yet to decide.</summary>
    Sampling,
    /// <summary>Sustained high CPU: the process looks CPU-bound.</summary>
    Detected,
    /// <summary>CPU usage is below the detection threshold.</summary>
    NotDetected,
}

/// <summary>
/// Per-window CPU-bound detection state machine. Deliberately NOT part of
/// <see cref="GamingModeService"/>, which is shared app-wide: each Threads
/// window samples its own target process, so each owns its own detector.
/// Three consecutive samples at >= 50% of one core count as "CPU-bound" —
/// that is the regime where raising priority actually changes scheduling.
/// </summary>
public sealed class CpuBoundDetector
{
    // Detection: 3 samples x ~2 s tick = ~6 s of sustained load.
    private const int HotSampleThreshold = 3;
    // Percent of a SINGLE core. A single-threaded game pegging one core reads
    // ~100 here even on a 32-thread machine.
    private const double HotPercentThreshold = 50.0;

    private int _hotSamples;
    private bool _cpuBound;

    public bool IsCpuBound => _cpuBound;

    /// <summary>
    /// Feeds one whole-process CPU sample (percent of one core). The first
    /// call only seeds and returns Sampling.
    /// </summary>
    public CpuBoundState Observe(double cpuPercentOfOneCore)
    {
        if (cpuPercentOfOneCore < 0)
        {
            // Invalid sample (process died, access denied): don't reset the streak.
            return _cpuBound ? CpuBoundState.Detected : CpuBoundState.Sampling;
        }

        if (cpuPercentOfOneCore >= HotPercentThreshold)
        {
            _hotSamples++;
        }
        else
        {
            _hotSamples = 0;
        }

        _cpuBound = _hotSamples >= HotSampleThreshold;

        return _cpuBound ? CpuBoundState.Detected
             : _hotSamples > 0 ? CpuBoundState.Sampling
             : CpuBoundState.NotDetected;
    }

    /// <summary>Resets the detection streak (e.g. on manual restore).</summary>
    public void Reset()
    {
        _hotSamples = 0;
        _cpuBound = false;
    }
}

/// <summary>
/// One-shot result summary for the Gaming mode toggle, formatted for the status line.
/// </summary>
public sealed class GamingModeResult
{
    public bool Success { get; init; }
    public int LoweredCount { get; init; }
    public int EcoCount { get; init; }
    public int FailedCount { get; init; }
    public string TargetBefore { get; init; } = "?";
    public string TargetAfter { get; init; } = "?";
    public string Error { get; init; } = string.Empty;

    public string Summary =>
        Success
            ? $"target {TargetBefore} → {TargetAfter} · {LoweredCount} background lowered · {EcoCount} in Efficiency mode" +
              (FailedCount > 0 ? $" · {FailedCount} skipped (no access)" : "")
            : $"failed: {Error}";
}

/// <summary>
/// Gaming mode for the Threads window: raises the target process to Above
/// Normal (Efficiency mode forced OFF for the target), lowers every other
/// mutable background process to Below Normal AND turns their Efficiency
/// mode (EcoQoS) ON, and restores all original priority classes and eco
/// states when switched off (also on window close, so the system is never
/// left boosted).
///
/// CPU-bound detection is a pure counter state machine fed by the window's
/// 2 s tick: three consecutive samples at >= 50% of one core count as
/// "CPU-bound" — that is the regime where raising priority actually changes
/// scheduling. Detection never lowers anything else; that is opt-in via the
/// Gaming mode switch.
///
/// All changes funnel through one restore map so that "restore" always means
/// "the state before stellarisKIT touched anything", no matter how many
/// times auto-raise and gaming mode overlapped.
/// </summary>
public sealed class GamingModeService
{
    /// <summary>
    /// PID → (original priority class, original Efficiency mode). Eco is null
    /// when the original state could not be read — such processes get their
    /// priority lowered but are never eco-toggled. Restoration targets these.
    /// </summary>
    private readonly ConcurrentDictionary<int, (uint Priority, bool? Eco)> _restoreMap = new();

    public bool IsActive { get; private set; }

    /// <summary>Number of processes we have changed and can restore.</summary>
    public int RestorableCount => _restoreMap.Count;

    /// <summary>
    /// Detection fired: raise the target Normal → Above Normal. Returns true
    /// when a raise was applied. Processes already above Normal are left
    /// alone (never pushed towards High/Realtime automatically) and never
    /// raised out of Below Normal/Idle — a throttled process was set there
    /// on purpose, either by the user or by the OS.
    /// </summary>
    public async Task<bool> AutoRaiseTargetAsync(int pid)
    {
        return await Task.Run(() =>
        {
            uint current = ReadPriorityClass(pid);
            if (current != NativeMethods.Priority.Normal)
            {
                return false; // already elevated, below normal, or unreadable
            }

            // Record the pre-change value so a later restore puts things back.
            if (!_restoreMap.ContainsKey(pid))
            {
                _restoreMap[pid] = (current, null);
            }

            return TrySetPriorityClass(pid, NativeMethods.Priority.AboveNormal);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Activates Gaming mode: records every mutable process's priority class,
    /// raises the target to High (never Realtime), lowers other background processes to
    /// Below Normal. Critical system processes, this app, and processes we
    /// can't read (no restore possible) are never touched.
    /// </summary>
    public async Task<GamingModeResult> ActivateAsync(
        int targetPid,
        IReadOnlyCollection<int>? protectedPids = null)
    {
        protectedPids ??= new[] { targetPid };

        return await Task.Run(() =>
        {
            try
            {
                int lowered = 0;
                int ecoCount = 0;
                int failed = 0;
                string targetBefore = "?";
                string targetAfter = "?";

                foreach (Process proc in Process.GetProcesses())
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = proc.Id;
                        name = proc.ProcessName + ".exe";
                    }
                    catch
                    {
                        continue; // died mid-enumeration
                    }

                    if (pid <= 4 || ProcessTuningService.IsSelf(pid))
                    {
                        continue;
                    }

                    uint original = ReadPriorityClass(pid);
                    if (original == 0)
                    {
                        continue; // no access — can't restore later, don't touch
                    }

                    // First time we see this process: remember its natural state
                    // (priority + Efficiency mode). If a priority-only change
                    // (CPU-bound auto-raise) recorded it earlier without eco,
                    // capture eco now so this pass can toggle and restore it.
                    if (!_restoreMap.TryGetValue(pid, out var known))
                    {
                        _restoreMap[pid] = (original, ReadEcoState(pid));
                    }
                    else if (known.Eco is null)
                    {
                        _restoreMap[pid] = (known.Priority, ReadEcoState(pid));
                    }

                    if (pid == targetPid || protectedPids.Contains(pid))
                    {
                        continue; // all game processes stay untouched (handled below)
                    }

                    if (ProcessTuningService.IsCritical(name, pid))
                    {
                        // Critical system processes (like DWM) get bumped to Above Normal to prevent starving
                        if (original is NativeMethods.Priority.Normal or NativeMethods.Priority.BelowNormal)
                        {
                            TrySetPriorityClass(pid, NativeMethods.Priority.AboveNormal);
                        }
                        continue;
                    }

                    // Lower ordinary background load into Below Normal +
                    // Efficiency mode. Already-low processes (Idle/BelowNormal)
                    // and manually-set High/Realtime ones are respected for
                    // priority; eco is only toggled where it was readable so
                    // restore can always put it back.
                    if (original is NativeMethods.Priority.Normal or NativeMethods.Priority.AboveNormal)
                    {
                        bool priorityOk = TrySetPriorityClass(pid, NativeMethods.Priority.BelowNormal);
                        bool? ecoOriginal = _restoreMap[pid].Eco;
                        bool ecoOk = !ecoOriginal.HasValue || TrySetEco(pid, true);

                        if (priorityOk)
                        {
                            lowered++;
                            if (ecoOriginal.HasValue && ecoOk)
                            {
                                ecoCount++;
                            }
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }

                // Target: Normal/Below Normal/Idle → High; anything already
                // above that is respected as a deliberate choice. Realtime is
                // never selected automatically.
                uint targetOriginal = ReadPriorityClass(targetPid);
                targetBefore = ProcessTuningService.PriorityName(targetOriginal);

                bool ok = true;
                if (targetOriginal is NativeMethods.Priority.Normal or NativeMethods.Priority.BelowNormal or NativeMethods.Priority.Idle)
                {
                    if (!_restoreMap.ContainsKey(targetPid))
                    {
                        _restoreMap[targetPid] = (targetOriginal, null);
                    }

                    ok = TrySetPriorityClass(targetPid, NativeMethods.Priority.High);
                }

                // Target: Efficiency mode OFF so the game runs at full
                // performance. The original state was captured above when
                // readable; force OFF even when unreadable (one-way change,
                // but a game with Eco on is never what the user wants).
                bool? targetEco = ReadEcoState(targetPid);
                if (targetEco == true)
                {
                    if (_restoreMap.TryGetValue(targetPid, out var known))
                    {
                        if (known.Eco is null)
                        {
                            _restoreMap[targetPid] = (known.Priority, targetEco);
                        }
                    }
                    else
                    {
                        _restoreMap[targetPid] = (targetOriginal, targetEco);
                    }

                    TrySetEco(targetPid, false);
                }

                targetAfter = ProcessTuningService.PriorityName(ReadPriorityClass(targetPid));
                IsActive = true;

                return new GamingModeResult
                {
                    Success = ok,
                    LoweredCount = lowered,
                    EcoCount = ecoCount,
                    FailedCount = failed,
                    TargetBefore = targetBefore,
                    TargetAfter = targetAfter,
                    Error = ok ? string.Empty : NativeSnapshotService.LastError("Setting priority class failed.").Message,
                };
            }
            catch (Exception ex)
            {
                return new GamingModeResult { Success = false, Error = ex.Message };
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores every priority class captured since activation (including the
    /// auto-raised target). Safe to call repeatedly; no-ops when the map is
    /// empty. Only pids that still exist fail silently.
    /// </summary>
    public void Deactivate()
    {
        foreach ((int pid, (uint priority, bool? eco)) in _restoreMap)
        {
            TrySetPriorityClass(pid, priority);
            if (eco.HasValue)
            {
                TrySetEco(pid, eco.Value);
            }
        }

        _restoreMap.Clear();
        IsActive = false;
    }

    private static uint ReadPriorityClass(int pid)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return 0;
            }

            uint cls = NativeMethods.Priority.GetPriorityClass(process);
            return cls == 0 ? 0 : cls;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TrySetPriorityClass(int pid, uint cls)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return false;
            }

            return NativeMethods.Priority.SetPriorityClass(process, cls);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the process's Efficiency mode (EcoQoS) state via
    /// GetProcessInformation. Null when unreadable — callers must not
    /// toggle eco for such processes, because restore would be impossible.
    /// </summary>
    private static bool? ReadEcoState(int pid)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return null;
            }

            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (!NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                return null;
            }

            return (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets Efficiency mode (EcoQoS) for one process. Best effort.</summary>
    private static bool TrySetEco(int pid, bool enabled)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return false;
            }

            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            return NativeMethods.Power.SetProcessInformation(
                process, ProcessInformationClass.ProcessPowerThrottling,
                ref state, NativeMethods.Power.StateSize());
        }
        catch
        {
            return false;
        }
    }
}
