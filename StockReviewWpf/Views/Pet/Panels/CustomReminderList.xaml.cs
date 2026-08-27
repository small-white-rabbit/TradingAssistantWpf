using System.Windows.Controls;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class CustomReminderList : UserControl
{
    public CustomReminderList()
    {
        InitializeComponent();
        DataContext = new CustomReminderPanelViewModel();
    }
}
