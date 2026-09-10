namespace stellarisKIT.Models
{
    /// <summary>
    /// Snapshot of the CPU's physical topology, built once from
    /// GetLogicalProcessorInformationEx and cached for the session.
    /// </summary>
    public sealed class CoreTopology
    {
        public int TotalLogicalCores { get; init; }
        public int PhysicalCores { get; init; }

        /// <summary>Always 0 — the OS scheduler's own core is never assigned to devices.</summary>
        public int ReservedCore { get; init; } = 0;

        /// <summary>
        /// Group Affinity bitmasks representing full physical cores (EfficiencyClass == 0).
        /// On non-hybrid CPUs this contains every physical core's mask except the reserved one.
        /// </summary>
        public System.Collections.Generic.List<ulong> PerformanceCoreMasks { get; init; } = new();

        /// <summary>
        /// Group Affinity bitmasks representing full Efficiency cores (EfficiencyClass > 0).
        /// Empty on non-hybrid CPUs.
        /// </summary>
        public System.Collections.Generic.List<ulong> EfficiencyCoreMasks { get; init; } = new();

        public bool IsHybrid => EfficiencyCoreMasks.Count > 0;
    }
}
