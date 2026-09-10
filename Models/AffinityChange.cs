using System;

namespace stellarisKIT.Models
{
    /// <summary>
    /// Single tracked registry modification — used by the undo/redo stack
    /// and the "View Changes" dialog.
    /// </summary>
    public sealed record AffinityChange(
        string DeviceId,
        string DeviceName,
        string PropertyName,   // "MsiEnabled", "DevicePolicy", "DevicePriority", "AffinityMask"
        object? OldValue,
        object? NewValue,
        DateTime Timestamp);
}
