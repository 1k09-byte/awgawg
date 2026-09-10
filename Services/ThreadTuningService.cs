using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using stellarisKIT.Models;
using stellarisKIT.Native;

namespace stellarisKIT.Services;

/// <summary>
/// Exposes Thread-level mutations for priority, boost, affinity, ideal processor,
/// suspension and termination against native APIs off the UI thread.
/// </summary>
public sealed class ThreadTuningService
{
    public async Task SetPriorityAsync(uint tid, int priority)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.SetThreadPriority(thread, priority))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread priority failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> GetBoostAsync(uint tid)
    {
        return await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool Svetlana))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread priority boost failed."));
            }

            return !Svetlana;
        }).ConfigureAwait(false);
    }

    public async Task SetBoostAsync(uint tid, bool enabled)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: !enabled))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread priority boost failed."));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-thread Efficiency Mode (EcoQoS) via SetThreadInformation. Uses the
    /// THREAD_POWER_THROTTLING_EXECUTION_SPEED mask (0x1) — note this differs
    /// from the process-level mask (0x4) on purpose.
    /// </summary>
    public async Task SetEfficiencyAsync(uint tid, bool enabled)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var state = new ThreadPowerThrottlingState
            {
                Version = NativeMethods.Power.ThreadVersion,
                ControlMask = NativeMethods.Power.ThreadExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ThreadExecutionSpeed : 0,
            };
            if (!NativeMethods.Power.SetThreadInformation(
                thread, ThreadInformationClass.ThreadPowerThrottling,
                ref state, NativeMethods.Power.ThreadStateSize()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread Efficiency Mode failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> GetEfficiencyAsync(uint tid)
    {
        return await Task.Run(() =>
        {
            // NOTE: despite the docs, GetThreadInformation demands full
            // THREAD_QUERY_INFORMATION on this build — LIMITED fails with
            // access denied even on our own threads (verified live).
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var state = new ThreadPowerThrottlingState
            {
                Version = NativeMethods.Power.ThreadVersion,
            };
            if (!NativeMethods.Power.GetThreadInformation(
                thread, ThreadInformationClass.ThreadPowerThrottling,
                ref state, NativeMethods.Power.ThreadStateSize()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread Efficiency Mode failed."));
            }

            return (state.StateMask & NativeMethods.Power.ThreadExecutionSpeed) != 0;
        }).ConfigureAwait(false);
    }

    public async Task<(ushort Group, ulong Mask)> GetAffinityStateAsync(uint tid)
    {
        return await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var affinity))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread affinity failed."));
            }
            return (affinity.Group, affinity.Mask);
        }).ConfigureAwait(false);
    }

    public async Task<string> GetAffinityDescriptionAsync(uint tid)
    {
        var state = await GetAffinityStateAsync(tid).ConfigureAwait(false);
        var cpus = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((state.Mask & (1UL << i)) != 0) cpus.Add(i.ToString());
        }
        return $"Group {state.Group}: CPU {string.Join(", ", cpus)} (0x{state.Mask:X})";
    }

    public async Task<ulong> GetAffinityAsync(uint tid) =>
        (await GetAffinityStateAsync(tid).ConfigureAwait(false)).Mask;

    public Task SetAffinityAsync(uint tid, ulong mask) =>
        SetAffinityAsync(tid, null, mask);

    public async Task SetAffinityAsync(uint tid, ushort? group, ulong mask)
    {
        if (mask == 0) throw new ArgumentException("The affinity mask must contain at least one processor.", nameof(mask));

        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);

            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var current))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread affinity failed."));
            }

            // A GROUP_AFFINITY mask is local to its processor group. Never
            // treat CPU 0 in group 1 as CPU 64 in group 0. Preserve the live
            // group unless the caller explicitly selected another group.
            current.Group = group ?? current.Group;
            ushort active = NativeMethods.Affinity.GetActiveProcessorCount(current.Group);
            ulong validBits = active >= 64 ? ulong.MaxValue : ((1UL << active) - 1UL);
            if ((mask & ~validBits) != 0)
            {
                throw new InvalidOperationException($"CPU mask 0x{mask:X} contains processors unavailable in group {current.Group} (active CPUs: {active}).");
            }

            current.Mask = mask;
            if (!NativeMethods.Affinity.SetThreadGroupAffinity(thread, ref current, IntPtr.Zero))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread affinity failed."));
            }

            // Verify the kernel accepted the change before reporting success.
            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var applied)
                || applied.Group != current.Group
                || applied.Mask != mask)
            {
                throw new InvalidOperationException("Windows did not apply the requested thread affinity mask.");
            }
        }).ConfigureAwait(false);
    }

    public async Task SetIdealProcessorAsync(uint tid, ushort group, byte index)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            
            var proc = new ProcessorNumber 
            { 
                Group = group, 
                Number = index, 
                Reserved = 0 
            };

            if (!NativeMethods.Affinity.SetThreadIdealProcessorEx(thread, ref proc, IntPtr.Zero))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread ideal processor failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task SuspendThreadAsync(uint tid)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SuspendResume, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (NativeMethods.Threads.SuspendThread(thread) == uint.MaxValue)
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Suspending thread failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task ResumeThreadAsync(uint tid)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SuspendResume, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (NativeMethods.Threads.ResumeThread(thread) == uint.MaxValue)
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Resuming thread failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task TerminateThreadAsync(uint tid, uint exitCode = 1)
    {
        await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.Terminate, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Threads.TerminateThread(thread, exitCode))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Terminating thread failed."));
            }
        }).ConfigureAwait(false);
    }
}
