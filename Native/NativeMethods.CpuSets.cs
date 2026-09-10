using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

internal enum CpuSetInformationType : byte
{
    CpuSetInformation = 0,
}

internal static partial class NativeMethods
{
    internal static partial class CpuSets
    {
        // Standard Win32 two-pass idiom: call once with a zero-size buffer to get
        // the required size, allocate, then call again. process = NULL for
        // system-wide. NOTE: IntPtr, not SafeHandle — the LibraryImport
        // SafeHandle marshaller throws on null, and this call always passes
        // NULL here (verified live: NRE otherwise).
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetSystemCpuSetInformation(
            [Out] byte[]? information,
            uint bufferLength,
            out uint returnedLength,
            IntPtr process,
            uint flags);

        // NOTE: ID arrays are PULONG (32-bit), NOT 64-bit — verified live.
        // ulong[] here silently garbles every element past the first and the
        // kernel rejects multi-element lists with 813 (invalid CPU set IDs).
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessDefaultCpuSets(
            SafeProcessHandle process,
            [Out] uint[]? cpuSetIds,
            uint cpuSetIdsLength,
            out uint requiredLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessDefaultCpuSets(
            SafeProcessHandle process,
            [In] uint[] cpuSetIds,
            uint cpuSetIdsCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadSelectedCpuSets(
            SafeThreadHandle thread,
            [Out] uint[]? cpuSetIds,
            uint cpuSetIdsLength,
            out uint requiredLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadSelectedCpuSets(
            SafeThreadHandle thread,
            [In] uint[] cpuSetIds,
            uint cpuSetIdsCount);
    }
}

/// <summary>
/// One logical processor as reported by <c>GetSystemCpuSetInformation</c>,
/// parsed manually from the raw buffer (offsets, not marshaled structs) so a
/// future Windows layout change degrades instead of corrupting.
/// </summary>
public sealed class CpuSetEntry
{
    internal uint Id { get; init; }
    internal ushort Group { get; init; }
    internal byte LogicalIndex { get; init; }
    internal byte CoreIndex { get; init; }
    internal byte LastLevelCacheIndex { get; init; }
    internal byte NumaNodeIndex { get; init; }
    internal byte EfficiencyClass { get; init; }
    internal bool Parked { get; init; }
    internal bool Allocated { get; init; }
}
