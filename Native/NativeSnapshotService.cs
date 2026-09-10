using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace stellarisKIT.Native;

public sealed class SnapshotThread
{
    internal uint ThreadId { get; init; }
    internal long KernelTicks { get; init; }
    internal long UserTicks { get; init; }
    internal long StartAddress { get; init; }
    internal int Priority { get; init; }
    internal int BasePriority { get; init; }
    internal uint ContextSwitches { get; init; }
    internal uint State { get; init; }
    internal uint WaitReason { get; init; }
}

public sealed class SnapshotProcess
{
    internal uint ProcessId { get; init; }
    internal int ThreadCount { get; init; }
    internal long CycleTime { get; init; }
    internal List<SnapshotThread> Threads { get; } = new();
}

/// <summary>Best-effort wrappers for Windows native process/thread queries.</summary>
public sealed partial class NativeSnapshotService
{
    private const int ThreadStride = 80;
    private const int ThreadHeaderSize = 176;
    private const int MaxThreadsPerProcess = 5000;
    private const int MaxProcesses = 4096;

    internal bool Limited { get; private set; }

    internal List<SnapshotProcess> TrySnapshot()
    {
        var result = new List<SnapshotProcess>();
        Limited = false;
        try
        {
            uint needed;
            _ = NativeMethods.Ntdll.NtQuerySystemInformation(
                NativeMethods.Ntdll.SystemProcessInformation, null, 0, out needed);
            if (needed == 0 || needed > 256 * 1024 * 1024)
            {
                Limited = true;
                return result;
            }

            byte[] buffer = new byte[needed + 1024];
            int status = NativeMethods.Ntdll.NtQuerySystemInformation(
                NativeMethods.Ntdll.SystemProcessInformation, buffer, (uint)buffer.Length, out _);
            if (status != 0)
            {
                Limited = true;
                return result;
            }

            Parse(buffer, result);
        }
        catch
        {
            Limited = true;
        }

        return result;
    }

    private void Parse(byte[] buffer, List<SnapshotProcess> result)
    {
        int offset = 0;
        int guard = 0;
        while (offset >= 0 && offset + ThreadHeaderSize <= buffer.Length && guard++ < MaxProcesses)
        {
            int next = ReadInt32(buffer, offset);
            int threadCount = ReadInt32(buffer, offset + 4);
            if (threadCount < 0 || threadCount > MaxThreadsPerProcess)
            {
                Limited = true;
                return;
            }

            uint pid = ReadUInt32(buffer, offset + 80);
            long cycles = ReadInt64(buffer, offset + 24);
            var process = new SnapshotProcess
            {
                ProcessId = pid,
                ThreadCount = threadCount,
                CycleTime = cycles,
            };

            long threadsEnd = (long)offset + ThreadHeaderSize + (long)threadCount * ThreadStride;
            if (threadsEnd > buffer.Length)
            {
                Limited = true;
                return;
            }

            for (int i = 0; i < threadCount; i++)
            {
                int t = offset + ThreadHeaderSize + i * ThreadStride;
                process.Threads.Add(new SnapshotThread
                {
                    ThreadId = ReadUInt32(buffer, t + 48),
                    KernelTicks = ReadInt64(buffer, t),
                    UserTicks = ReadInt64(buffer, t + 8),
                    StartAddress = ReadInt64(buffer, t + 32),
                    Priority = ReadInt32(buffer, t + 56),
                    BasePriority = ReadInt32(buffer, t + 60),
                    ContextSwitches = ReadUInt32(buffer, t + 64),
                    State = ReadUInt32(buffer, t + 68),
                    WaitReason = ReadUInt32(buffer, t + 72),
                });
            }

            result.Add(process);
            if (next == 0)
            {
                break;
            }

            offset += next;
        }
    }

    private static int ReadInt32(byte[] b, int o) => BitConverter.ToInt32(b, o);
    private static uint ReadUInt32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    private static long ReadInt64(byte[] b, int o) => BitConverter.ToInt64(b, o);

    internal static string ThreadStateText(uint state) => state switch
    {
        0 => "Initialized", 1 => "Ready", 2 => "Running", 3 => "Standby",
        4 => "Terminated", 5 => "Waiting", 6 => "Transition", 7 => "DeferredReady",
        _ => $"State {state}",
    };

    internal static string WaitReasonText(uint reason) => reason switch
    {
        0 => "Executive", 1 => "FreePage", 2 => "PageIn", 3 => "PoolAllocation",
        4 => "DelayExecution", 5 => "Suspended", 6 => "UserRequest", 7 => "WrExecutive",
        8 => "WrFreePage", 9 => "WrPageIn", 10 => "WrPoolAllocation", 11 => "WrDelayExecution",
        12 => "WrSuspended", 13 => "WrUserRequest", 14 => "WrEventPair", 15 => "WrQueue",
        16 => "WrLpcReceive", 17 => "WrLpcReply", 18 => "WrVirtualMemory", 19 => "WrPageOut",
        20 => "WrRendezvous", 21 => "WrKeyedEvent", 22 => "WrTerminated", 23 => "WrProcessInSwap",
        24 => "WrCpuRateControl", 25 => "WrCalloutStack", 26 => "WrKernel", 27 => "WrResource",
        28 => "WrPushLock", 29 => "WrMutex", 30 => "WrQuantumEnd", 31 => "WrDispatchInt",
        32 => "WrPreempted", 33 => "WrYieldExecution", 34 => "WrFastMutex", 35 => "WrGuardedMutex",
        36 => "WrRundown", 37 => "WrAlertByThreadId", 38 => "WrDeferredPreempt",
        _ => $"Reason {reason}",
    };

    internal static long? TryGetWin32StartAddress(uint tid)
    {
        // Class 9 requires THREAD_QUERY_INFORMATION. Do not silently prefer the
        // limited handle: that was the reason all rows fell back to the common
        // ntdll loader address on the affected machine.
        foreach (NativeMethods.ThreadAccess access in new[]
        {
            NativeMethods.ThreadAccess.QueryInformation,
            NativeMethods.ThreadAccess.QueryLimitedInformation,
        })
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(access, false, tid);
                if (thread.IsInvalid)
                {
                    continue;
                }

                int status = NativeMethods.Ntdll.NtQueryInformationThread(
                    thread,
                    NativeMethods.Ntdll.ThreadQuerySetWin32StartAddress,
                    out IntPtr address,
                    IntPtr.Size,
                    out _);
                if (status == 0 && address != IntPtr.Zero)
                {
                    return address.ToInt64();
                }
            }
            catch
            {
                // Protected or exited thread.
            }
        }

        return null;
    }

    internal static string? TryGetThreadDescription(uint tid)
    {
        foreach (NativeMethods.ThreadAccess access in new[]
        {
            NativeMethods.ThreadAccess.QueryLimitedInformation,
            NativeMethods.ThreadAccess.QueryInformation,
        })
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(access, false, tid);
                if (thread.IsInvalid)
                {
                    continue;
                }

                // This is the supported API and handles the allocation/freeing
                // details for us. NOTE: check SUCCEEDED (hr >= 0), not == S_OK.
                // Threads named via NtSetInformationThread (all seven DWM role
                // threads) return success code 0x10000000 with a valid string
                // pointer — verified live. An == 0 check silently discards
                // every one of them and the Description column goes unnamed.
                if (NativeMethods.Threads.GetThreadDescription(thread, out IntPtr description) >= 0
                    && description != IntPtr.Zero)
                {
                    try
                    {
                        string? value = Marshal.PtrToStringUni(description);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Trim();
                        }
                    }
                    finally
                    {
                        NativeMethods.Handles.LocalFree(description);
                    }
                }

                // System Informer uses a real output buffer for class 38. A zero
                // length probe is not reliable here: Windows can return no useful
                // returnLength even though a 0x100-byte query succeeds. The old
                // probe therefore discarded valid DWM names before parsing them.
                const int InitialNameBufferSize = 0x100;
                int status;
                int bufferSize = InitialNameBufferSize;
                IntPtr info = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    status = NativeMethods.Ntdll.NtQueryInformationThreadName(
                        thread,
                        NativeMethods.Ntdll.ThreadNameInformation,
                        info,
                        bufferSize,
                        out uint returned);

                    if (status == NativeMethods.Ntdll.StatusBufferOverflow
                        || status == NativeMethods.Ntdll.StatusBufferTooSmall
                        || status == NativeMethods.Ntdll.StatusInfoLengthMismatch)
                    {
                        if (returned < 16 || returned > 65536)
                        {
                            continue;
                        }

                        Marshal.FreeHGlobal(info);
                        bufferSize = checked((int)returned);
                        info = Marshal.AllocHGlobal(bufferSize);
                        status = NativeMethods.Ntdll.NtQueryInformationThreadName(
                            thread,
                            NativeMethods.Ntdll.ThreadNameInformation,
                            info,
                            bufferSize,
                            out _);
                    }

                    if (status != 0)
                    {
                        continue;
                    }

                    // THREAD_NAME_INFORMATION contains a UNICODE_STRING. On
                    // x64 the Buffer pointer is at offset 8; on x86 it is at
                    // offset 4. Windows normally points it into the returned
                    // buffer, but accept an external pointer as System Informer
                    // does as well.
                    ushort length = (ushort)Marshal.ReadInt16(info, 0);
                    IntPtr nameBuffer = Marshal.ReadIntPtr(info, IntPtr.Size == 8 ? 8 : 4);
                    if (length == 0 || length > 32768 || nameBuffer == IntPtr.Zero)
                    {
                        continue;
                    }

                    string? nativeValue = Marshal.PtrToStringUni(nameBuffer, length / 2);
                    if (!string.IsNullOrWhiteSpace(nativeValue))
                    {
                        return nativeValue.Trim();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(info);
                }
            }
            catch
            {
                // Protected or already-exited thread; leave it unnamed.
            }
        }

        return null;
    }

    internal static string? TryGetCommandLine(SafeProcessHandle process)
    {
        try
        {
            byte[] pbi = new byte[IntPtr.Size * 6];
            int status = NtQueryInformationProcess(process, 0, pbi, (uint)pbi.Length, out _);
            if (status != 0) return null;
            IntPtr peb = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(pbi, IntPtr.Size))
                : new IntPtr(BitConverter.ToInt32(pbi, IntPtr.Size));
            if (peb == IntPtr.Zero) return null;
            if (!TryReadPointer(process, peb, IntPtr.Size == 8 ? 0x20 : 0x10, out IntPtr parameters)
                || parameters == IntPtr.Zero) return null;

            int commandLineOffset = IntPtr.Size == 8 ? 0x70 : 0x40;
            byte[] unicode = new byte[16];
            if (!TryRead(process, parameters + commandLineOffset, unicode)) return null;
            ushort length = BitConverter.ToUInt16(unicode, 0);
            IntPtr pointer = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(unicode, 8))
                : new IntPtr(BitConverter.ToInt32(unicode, 4));
            if (length == 0 || pointer == IntPtr.Zero || length > 32768) return null;
            byte[] text = new byte[length];
            return TryRead(process, pointer, text) ? Encoding.Unicode.GetString(text) : null;
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        SafeProcessHandle process, int infoClass, [Out] byte[] info, uint infoLength, out uint returnLength);

    private static bool TryRead(SafeProcessHandle process, IntPtr address, byte[] buffer)
    {
        try
        {
            return NativeMethods.Handles.ReadProcessMemory(process, address, buffer, (IntPtr)buffer.Length, out IntPtr read)
                && read.ToInt64() == buffer.Length;
        }
        catch { return false; }
    }

    private static bool TryReadPointer(SafeProcessHandle process, IntPtr address, int offset, out IntPtr value)
    {
        value = IntPtr.Zero;
        byte[] buffer = new byte[IntPtr.Size];
        if (!TryRead(process, address + offset, buffer)) return false;
        value = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));
        return true;
    }

    internal static Win32Exception LastError(string action) =>
        new(Marshal.GetLastWin32Error(), action);
}
