using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace stellarisKIT.Models
{
    public enum RadeonPackageCategory
    {
        Driver,
        Application,
        Telemetry,
        Utility
    }

    public enum SlimmerPreset
    {
        DisplayOnly,
        LowLatencyGaming,
        FullExperience,
        Custom
    }

    /// <summary>
    /// Package entry matching RadeonSoftwareSlimmer Tab 1: Packages (Drivers & MSIs)
    /// </summary>
    public partial class RadeonPackageItem : ObservableObject
    {
        public string Id { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Name { get => ProductName; set => ProductName = value; }
        public string LocationUrl { get; set; } = string.Empty;
        public string PackageType { get; set; } = "DRIVER";
        public string Description { get; set; } = string.Empty;
        public RadeonPackageCategory Category { get; set; } = RadeonPackageCategory.Driver;
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }

        public string FormattedSize => SizeBytes > 0
            ? $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
            : string.Empty;

        public bool IsRequired { get; set; }
        public bool IsRemovable => !IsRequired;

        [ObservableProperty]
        private bool _isSelected = true;
    }

    /// <summary>
    /// Scheduled Task entry matching RadeonSoftwareSlimmer Tab 2: Scheduled Tasks
    /// </summary>
    public partial class RadeonScheduledTaskItem : ObservableObject
    {
        public string Uri { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsTelemetry { get; set; }

        [ObservableProperty]
        private bool _isEnabled = true;
    }

    /// <summary>
    /// Display Driver Component matching RadeonSoftwareSlimmer Tab 3: Display Driver Components
    /// </summary>
    public partial class RadeonDisplayComponentItem : ObservableObject
    {
        public string Directory { get; set; } = string.Empty;
        public string InfFile { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
        public bool IsRemovable => !IsRequired;
        public bool IsTelemetry { get; set; }

        [ObservableProperty]
        private bool _isSelected = true;
    }

}