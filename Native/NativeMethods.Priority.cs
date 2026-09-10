using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

internal static partial class NativeMethods
{
    internal static partial class Priority
    {
        // Priority classes (match managed TunerPriorityClass values 1:1).
        internal const uint Idle = 0x40;
        internal const uint BelowNormal = 0x4000;
        internal const uint Normal = 0x20;
        internal const uint AboveNormal = 0x8000;
        internal const uint High = 0x80;
        internal const uint Realtime = 0x100;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint GetPriorityClass(SafeProcessHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

        // NOTE: DisablePriorityBoost is inverted — TRUE means the boost is OFF.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessPriorityBoost(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool Svetlana);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessPriorityBoost(
            SafeProcessHandle process,
            [MarshalAs(UnmanagedType.Bool)] bool Svetlana);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int GetThreadPriority(SafeThreadHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadPriority(SafeThreadHandle thread, int priority);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadPriorityBoost(SafeThreadHandle thread, [MarshalAs(UnmanagedType.Bool)] out bool Svetlana);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadPriorityBoost(
            SafeThreadHandle thread,
            [MarshalAs(UnmanagedType.Bool)] bool Svetlana);
    }

    internal static class ThreadPriorityLevel
    {
        internal const int Idle = -15;
        internal const int Lowest = -2;
        internal const int BelowNormal = -1;
        internal const int Normal = 0;
        internal const int AboveNormal = 1;
        internal const int Highest = 2;
        internal const int TimeCritical = 15;
    }
}
