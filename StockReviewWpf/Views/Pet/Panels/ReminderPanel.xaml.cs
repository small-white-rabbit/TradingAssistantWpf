using System.Windows.Controls;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class ReminderPanel : UserControl
{
    private CustomReminderPanel? _inner;

    public ReminderPanel()
    {
        InitializeComponent();
        _inner = ReminderContent as CustomReminderPanel;
    }

    /// <summary>转发刷新到内部 CustomReminderPanel</summary>
    public void RefreshData()
    {
        _inner?.RefreshData();
    }
}
