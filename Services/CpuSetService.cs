using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using stellarisKIT.Native;

namespace stellarisKIT.Services;

/// <summary>
/// Reads full CPU topology via <c>GetSystemCpuSetInformation</c> and reads/writes
/// per-process and per-thread default/selected CPU Sets. All native calls run on
/// worker threads; parsing is offset-based so layout changes degrade, not corrupt.
/// </summary>
public sealed class CpuSetService
{
    public async Task<List<CpuSetEntry>> GetTopologyAsync()
    {
        return await Task.Run(() =>
        {
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 122) // ERROR_INSUFFICIENT_BUFFER is the expected first answer
                {
                    throw new Win32Exception(err, "Querying CPU set buffer size failed.");
                }
            }

            if (needed == 0 || needed > 64 * 1024 * 1024)
            {
                throw new Win32Exception("CPU set information size looked invalid.");
            }

            byte[] buffer = new byte[needed];
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(buffer, needed, out _, IntPtr.Zero, 0))
            {
                throw NativeSnapshotService.LastError("Reading CPU set information failed.");
            }

            var list = new List<CpuSetEntry>();
            int offset = 0;
            int guard = 0;
            while (offset + 20 <= buffer.Length && guard++ < 2048)
            {
                uint size = BitConverter.ToUInt32(buffer, offset);
                byte type = buffer[offset + 4];
                if (size < 20 || offset + size > buffer.Length)
                {
                    break; // Truncated entry: keep what parsed cleanly.
                }

                if (type == (byte)CpuSetInformationType.CpuSetInformation)
                {
                    byte flags = buffer[offset + 19];
                    list.Add(new CpuSetEntry
                    {
                        Id = BitConverter.ToUInt32(buffer, offset + 8),
                        Group = BitConverter.ToUInt16(buffer, offset + 12),
                        LogicalIndex = buffer[offset + 14],
                        CoreIndex = buffer[offset + 15],
                        LastLevelCacheIndex = buffer[offset + 16],
                        NumaNodeIndex = buffer[offset + 17],
                        EfficiencyClass = buffer[offset + 18],
                        Parked = (flags & 0x01) != 0,
                        Allocated = (flags & 0x02) != 0,
                    });
                }

                offset += (int)size;
            }

            return list;
        }).ConfigureAwait(false);
    }

    public async Task<List<ulong>> GetProcessCpuSetsAsync(int pid)
    {
        return await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            ThrowIfInvalid(process, pid);
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(process, null, 0, out uint required))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 122)
                {
                    throw Friendly(pid, new Win32Exception(err, "Reading process CPU sets failed."));
                }
            }

            if (required == 0)
            {
                return new List<ulong>();
            }

            uint[] ids = new uint[required];
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(process, ids, required, out _))
            {
                throw Friendly(pid, NativeSnapshotService.LastError("Reading process CPU sets failed."));
            }

            return ids.Select(i => (ulong)i).ToList();
        }).ConfigureAwait(false);
    }

    public async Task SetProcessCpuSetsAsync(int pid, IReadOnlyList<ulong> ids)
    {
        await Task.Run(() =>
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            ThrowIfInvalid(process, pid);
            if (!NativeMethods.CpuSets.SetProcessDefaultCpuSets(process, ids.Select(i => (uint)i).ToArray(), (uint)ids.Count))
            {
                throw Friendly(pid, NativeSnapshotService.LastError("Setting process CPU sets failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<List<ulong>> GetThreadCpuSetsAsync(uint tid)
    {
        return await Task.Run(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryLimitedInformation, false, tid);
            ThrowIfInvalid(thread, tid);
            if (!NativeMethods.CpuSets.GetThreadSelectedCpuSets(thread, null, 0, out uint required))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 122)
                {
                    throw Friendly(tid, new Win32Exception(err, "Reading thread CPU sets failed."));
                }
            }

            if (required == 0)
            {
                return new List<ulong>();
            }

            uint[] ids = new uint[required];
            if (!NativeMethods.CpuSets.GetThreadSelectedCpuSets(thread, ids, required, out _))
            {
                throw Friendly(tid, NativeSnapshotService.LastError("Reading thread CPU sets failed."));
            }

            return ids.Select(i => (ulong)i).ToList();
        }).ConfigureAwait(false);
    }

    public async Task SetThreadCpuSetsAsync(uint tid, IReadOnlyList<ulong> ids)
    {
        await Task.Run(() =>
        {
            // Same lesson as thread affinity: the setter validates against
            // current state, so the handle needs QUERY_INFORMATION too.
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            ThrowIfInvalid(thread, tid);
            if (!NativeMethods.CpuSets.SetThreadSelectedCpuSets(thread, ids.Select(i => (uint)i).ToArray(), (uint)ids.Count))
            {
                throw Friendly(tid, NativeSnapshotService.LastError("Setting thread CPU sets failed."));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Ranks distinct EfficiencyClass values; rank 0 = highest class value.</summary>
    internal static Dictionary<byte, int> RankClasses(IEnumerable<CpuSetEntry> topology)
    {
        var ordered = topology.Select(c => c.EfficiencyClass).Distinct().OrderByDescending(v => v).ToList();
        var ranks = new Dictionary<byte, int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            ranks[ordered[i]] = i;
        }

        return ranks;
    }

    internal static void ThrowIfInvalid(SafeHandle handle, long id)
    {
        if (handle.IsInvalid)
        {
            throw Friendly(id, NativeSnapshotService.LastError($"Opening handle for ID {id} failed."));
        }
    }

    internal static Exception Friendly(long id, Exception inner)
    {
        if (inner is Win32Exception w32 && w32.NativeErrorCode == NativeMethods.Win32Error.AccessDenied)
        {
            return new Win32Exception(w32.NativeErrorCode,
                $"Access denied for ID {id} (protected process?). The change was not applied. {w32.Message}");
        }

        if (inner is Win32Exception gone &&
            (gone.NativeErrorCode == NativeMethods.Win32Error.InvalidHandle || gone.NativeErrorCode == 6))
        {
            return new InvalidOperationException($"Target ID {id} has exited.", inner);
        }

        return inner;
    }
}
