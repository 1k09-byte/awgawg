using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace stellarisKIT.Services
{
    /// <summary>
    /// Detects physical core count and hybrid (P-core / E-core) topology via
    /// GetLogicalProcessorInformationEx.  Result is cached — hardware topology
    /// does not change at runtime.
    /// </summary>
    public static class TopologyService
    {
        private static CoreTopology? _cached;
        private static readonly object _lock = new();

        public static CoreTopology Get()
        {
            if (_cached is not null) return _cached;
            lock (_lock)
            {
                _cached ??= Detect();
            }
            return _cached;
        }

        // ── Win32 P/Invoke ──────────────────────────────────────────────────

        private const int RelationProcessorCore = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            int RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);

        // Documented SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX header (variable-length).
        // https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-system_logical_processor_information_ex
        [StructLayout(LayoutKind.Sequential)]
        private struct SLPI_EX_HEADER
        {
            public int Relationship;  // LOGICAL_PROCESSOR_RELATIONSHIP enum
            public uint Size;         // Total size of this entry including trailing data
        }

        // PROCESSOR_RELATIONSHIP for RelationProcessorCore.
        // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship
        //
        // Layout (64-bit):
        //   BYTE  Flags           — 1 if SMT-capable core
        //   BYTE  EfficiencyClass — 0 = Performance, 1+ = Efficiency (non-hybrid CPUs: all 0)
        //   BYTE  Reserved[20]
        //   WORD  GroupCount
        //   GROUP_AFFINITY[ANYSIZE_ARRAY]
        //
        // We only need EfficiencyClass and the first GROUP_AFFINITY.Mask.
        //
        // GROUP_AFFINITY (16 bytes):
        //   KAFFINITY Mask (8 bytes on x64)
        //   WORD      Group
        //   WORD      Reserved[3]

        private const int PROCESSOR_REL_OFFSET = 8;       // offset past SLPI_EX_HEADER
        private const int FLAGS_OFFSET         = 0;       // Flags byte inside PROCESSOR_RELATIONSHIP
        private const int EFFICIENCY_OFFSET    = 1;       // EfficiencyClass byte
        private const int GROUP_COUNT_OFFSET   = 22;      // GroupCount WORD
        private const int GROUP_AFFINITY_OFFSET= 24;      // first GROUP_AFFINITY

        private static CoreTopology Detect()
        {
            int logicalCount = Math.Max(1, Environment.ProcessorCount);

            var perfCoreMasks = new List<ulong>();
            var effCoreMasks = new List<ulong>();
            int physicalCoreCount = 0;

            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
                if (len == 0) throw new InvalidOperationException("GetLogicalProcessorInformationEx returned 0 length");

                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                    uint offset = 0;
                    while (offset < len)
                    {
                        IntPtr entryPtr = buf + (int)offset;
                        var header = Marshal.PtrToStructure<SLPI_EX_HEADER>(entryPtr);

                        if (header.Relationship == RelationProcessorCore)
                        {
                            physicalCoreCount++;

                            IntPtr procRel = entryPtr + PROCESSOR_REL_OFFSET;
                            byte efficiencyClass = Marshal.ReadByte(procRel, EFFICIENCY_OFFSET);

                            // Read the GROUP_AFFINITY.Mask (first 8 bytes of the first GROUP_AFFINITY struct)
                            long maskRaw = Marshal.ReadInt64(procRel + GROUP_AFFINITY_OFFSET);
                            ulong mask = unchecked((ulong)maskRaw);

                            // Extract full physical core mask for this group
                            if (efficiencyClass == 0)
                                perfCoreMasks.Add(mask);
                            else
                                effCoreMasks.Add(mask);
                        }

                        offset += header.Size;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TopologyService.Detect failed: {ex.Message}");
                // Graceful fallback: assume homogeneous SMT-2 cores.
                physicalCoreCount = Math.Max(1, logicalCount / 2);
                perfCoreMasks.Clear();
                effCoreMasks.Clear();
                int logicalIndex = 0;
                for (int i = 0; i < physicalCoreCount; i++)
                {
                    ulong mask = 0;
                    if (logicalIndex < logicalCount) mask |= (1UL << logicalIndex++);
                    if (logicalIndex < logicalCount) mask |= (1UL << logicalIndex++);
                    perfCoreMasks.Add(mask);
                }
            }

            // Remove reserved core 0 from the assignable performance list.
            if (perfCoreMasks.Count > 0)
                perfCoreMasks.RemoveAt(0);

            return new CoreTopology
            {
                TotalLogicalCores = logicalCount,
                PhysicalCores = physicalCoreCount,
                ReservedCore = 0, // OS uses Core 0
                PerformanceCoreMasks = perfCoreMasks,
                EfficiencyCoreMasks = effCoreMasks
            };
        }
    }
}
