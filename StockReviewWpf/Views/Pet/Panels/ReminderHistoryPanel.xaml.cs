using System.Windows.Controls;
using StockReview.Core.Services;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class ReminderHistoryPanel : UserControl
{
    public ReminderHistoryPanel()
    {
        InitializeComponent();
        // 从 DI 获取 ReminderHistoryService（可能为 null，VM 内部处理）
        var historyService = App.Host?.Services.GetService(typeof(ReminderHistoryService)) as ReminderHistoryService;
        DataContext = new ReminderHistoryPanelViewModel(historyService);
    }

    /// <summary>外部（面板每次打开时）刷新历史数据</summary>
    public void RefreshData()
    {
        if (DataContext is ReminderHistoryPanelViewModel vm)
            vm.LoadFromService();
    }
}
