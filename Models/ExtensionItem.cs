using CommunityToolkit.Mvvm.ComponentModel;

namespace stellarisKIT.Models
{
    public partial class ExtensionItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        
        // Null means not supported on this engine
        public string? ChromiumExtensionId { get; set; } 
        public string? FirefoxAddonSlug { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; } = true;

        [ObservableProperty]
        public partial bool IsAvailable { get; set; } = true;
    }
}
