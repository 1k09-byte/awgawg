using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorNumber
{
    internal ushort Group;
    internal byte Number;
    internal byte Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GroupAffinity
{
    internal ulong Mask;
    internal ushort Group;
    internal ushort Reserved0;
    internal ushort Reserved1;
    internal ushort Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    internal uint LowDateTime;
    internal uint HighDateTime;

    internal readonly long ToTicks() => ((long)HighDateTime << 32) | LowDateTime;
}

internal static partial class NativeMethods
{
    internal static partial class Affinity
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial ushort GetActiveProcessorCount(ushort groupNumber);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial ushort GetActiveProcessorGroupCount();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessAffinityMask(
            SafeProcessHandle process,
            out IntPtr processAffinityMask,
            out IntPtr systemAffinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessAffinityMask(SafeProcessHandle process, IntPtr affinityMask);

        // Returns the previous mask; 0 (IntPtr.Zero) means failure.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr SetThreadAffinityMask(SafeThreadHandle thread, IntPtr affinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadGroupAffinity(
            SafeThreadHandle thread,
            out GroupAffinity groupAffinity);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadGroupAffinity(
            SafeThreadHandle thread,
            ref GroupAffinity groupAffinity,
            IntPtr previousGroupAffinity);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadIdealProcessorEx(
            SafeThreadHandle thread,
            IntPtr previousIdeal, // pass NULL to skip
            out ProcessorNumber currentIdeal);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadIdealProcessorEx(
            SafeThreadHandle thread,
            ref ProcessorNumber ideal,
            IntPtr previousIdeal);
    }

    internal static partial class Times
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(
            SafeProcessHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadTimes(
            SafeThreadHandle thread,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int QueryThreadCycleTime(SafeThreadHandle thread, out ulong cycleTime);
    }

    internal static partial class Threads
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint SuspendThread(SafeThreadHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint ResumeThread(SafeThreadHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateThread(SafeThreadHandle thread, uint exitCode);

        // Returns HRESULT (0 = S_OK). Frees with LocalFree — never leak the pointer.
        [LibraryImport("kernel32.dll")]
        internal static partial int GetThreadDescription(SafeThreadHandle thread, out IntPtr description);

        [LibraryImport("kernel32.dll")]
        internal static partial int SetThreadDescription(SafeThreadHandle thread, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }
}
