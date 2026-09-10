using CommunityToolkit.Mvvm.ComponentModel;

namespace stellarisKIT.Models
{
    public partial class AffinityDeviceItem : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        // Graphics / Network / Usb / Audio
        [ObservableProperty]
        private string category = string.Empty;

        // PCI\VEN_xxxx&DEV_xxxx... instance path (used later to target IRQ/affinity policy).
        [ObservableProperty]
        private string deviceInstanceId = string.Empty;

        // Row selection for the tuning pass (MSI mode checkbox column).
        [ObservableProperty]
        private bool isChecked = true;

        // Read-only layout placeholders until the tuning pass lands.
        [ObservableProperty]
        private string irqText = "—";

        [ObservableProperty]
        private string affinityText = "—";

        [ObservableProperty]
        private bool isVisible = true;

        // Dialog state (bound two-way; stored only — writers land in the tuning pass).
        [ObservableProperty]
        private bool msiEnabled;

        [ObservableProperty]
        private double msiLimit = 1;

        [ObservableProperty]
        private double maxMsiLimit = 1;

        [ObservableProperty]
        private string msiLimitText = "—";

        [ObservableProperty]
        private string devicePolicyShort = "—";

        [ObservableProperty]
        private string devicePriorityShort = "Undefined";

        [ObservableProperty]
        private string selectedPriority = "Undefined";

        [ObservableProperty]
        private string selectedPolicy = "IrqPolicyMachineDefault";

        [ObservableProperty]
        private string selectedThreadCountText = string.Empty;

        [ObservableProperty]
        private bool isProcessorMaskExpanded = true;

        public System.Collections.ObjectModel.ObservableCollection<ProcessorCoreGroup> CoreGroups { get; } = new();

        public string CategoryGlyph => Category switch
        {
            "Graphics" => "G",
            "Network" => "N",
            "Usb" => "U",
            "Audio" => "A",
            _ => "?"
        };

        public string CategoryLabel => Category switch
        {
            "Graphics" => "Graphics card",
            "Network" => "Network adapter",
            "Usb" => "USB controller",
            "Audio" => "Audio controller",
            _ => Category
        };

        public string MsiEnabledText => MsiEnabled ? "Enabled" : "Disabled";

        partial void OnMsiEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(MsiEnabledText));
        }
    }

    public partial class ProcessorThreadItem : ObservableObject
    {
        public int Index { get; set; }

        public string Label => $"Thread {Index}";

        [ObservableProperty]
        private bool isChecked;
    }

    /// <summary>Humanized dropdown option carrying the raw registry value.</summary>
    public sealed record PolicyOption(string Label, string Value);

    public partial class ProcessorCoreGroup : ObservableObject
    {
        public int CoreIndex { get; set; }

        public string Title => $"Core {CoreIndex}";

        public System.Collections.ObjectModel.ObservableCollection<ProcessorThreadItem> Threads { get; } = new();
    }
}
