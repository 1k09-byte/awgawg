using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

internal enum ProcessInformationClass : uint
{
    ProcessPowerThrottling = 4,
}

internal enum ThreadInformationClass : uint
{
    ThreadPowerThrottling = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerThrottlingState
{
    internal uint Version;
    internal uint ControlMask;
    internal uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ThreadPowerThrottlingState
{
    internal uint Version;
    internal uint ControlMask;
    internal uint StateMask;
}

internal static partial class NativeMethods
{
    internal static partial class Power
    {
        internal const uint Version = 1;
        internal const uint ExecutionSpeed = 0x4; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

        // NOTE: the thread constant differs from the process one on purpose:
        // THREAD_POWER_THROTTLING_EXECUTION_SPEED is 0x1, not 0x4.
        internal const uint ThreadVersion = 1;
        internal const uint ThreadExecutionSpeed = 0x1;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadPowerThrottlingState info,
            uint size);

        internal static uint StateSize() => (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();

        internal static uint ThreadStateSize() => (uint)Marshal.SizeOf<ThreadPowerThrottlingState>();
    }
}
