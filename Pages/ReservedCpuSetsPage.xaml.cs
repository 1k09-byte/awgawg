using Microsoft.UI.Xaml.Controls;
using stellarisKIT.ViewModels;

namespace stellarisKIT.Pages
{
    public sealed partial class ReservedCpuSetsPage : Page
    {
        public ReservedCpuSetsViewModel ViewModel { get; } = new();

        public ReservedCpuSetsPage()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }
    }
}
