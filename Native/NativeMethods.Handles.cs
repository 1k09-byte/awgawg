using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

internal static partial class NativeMethods
{
    internal static partial class Handles
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeProcessHandle OpenProcess(
            ProcessAccess desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeThreadHandle OpenThread(
            ThreadAccess desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint threadId);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentProcessId();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReadProcessMemory(
            SafeProcessHandle process,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            IntPtr size,
            out IntPtr bytesRead);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr LocalFree(IntPtr mem);
    }

    [Flags]
    internal enum ProcessAccess : uint
    {
        Terminate = 0x0001,
        SetInformation = 0x0200,
        QueryInformation = 0x0400,
        VirtualMemoryRead = 0x0010,
        SuspendResume = 0x0800,
        QueryLimitedInformation = 0x1000,
        Synchronize = 0x00100000,
    }

    [Flags]
    internal enum ThreadAccess : uint
    {
        Terminate = 0x0001,
        SuspendResume = 0x0002,
        GetContext = 0x0008,
        SetInformation = 0x0020,
        QueryInformation = 0x0040,
        QueryLimitedInformation = 0x0800,
    }

    internal static class Win32Error
    {
        internal const int Success = 0;
        internal const int AccessDenied = 5;
        internal const int InvalidHandle = 6;
        internal const int InvalidParameter = 87;
        internal const int PartialCopy = 299;
    }
}
