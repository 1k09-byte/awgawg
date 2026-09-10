using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

internal static partial class NativeMethods
{
    internal static partial class Ntdll
    {
        internal const int SystemProcessInformation = 5;
        internal const int ThreadQuerySetWin32StartAddress = 9;
        internal const int ThreadNameInformation = 38;
        internal const int StatusBufferOverflow = unchecked((int)0x80000005);
        internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);
        internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        // NTSTATUS return (0 = success). Undocumented/version-fragile: every caller
        // must validate sizes and degrade gracefully — see NativeSnapshotService.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQuerySystemInformation(
            int systemInformationClass,
            [Out] byte[]? systemInformation,
            uint systemInformationLength,
            out uint returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationThread(
            SafeThreadHandle thread,
            int threadInformationClass,
            out IntPtr threadInformation,
            int threadInformationLength,
            out uint returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationThreadName(
            SafeThreadHandle thread,
            int threadInformationClass,
            IntPtr threadInformation,
            int threadInformationLength,
            out uint returnLength);

    }
}
