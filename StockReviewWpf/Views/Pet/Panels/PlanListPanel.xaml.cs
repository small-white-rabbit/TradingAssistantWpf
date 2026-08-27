using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class PlanListPanel : UserControl
{
    public PlanListPanel()
    {
        InitializeComponent();
        var svc = App.Host?.Services.GetService(typeof(StockReview.Core.Services.TradePlanService))
                  as StockReview.Core.Services.TradePlanService;
        Log.Information("[PlanListPanel] 构造: service={(svc != null)} planCount={Count}", svc != null, svc?.Plans.Count ?? 0);
        var vm = new PlanListPanelViewModel(svc);
        DataContext = vm;
        // 点击股票名 → 请求打开分时图（转发给宿主宠物窗口）
        vm.OpenIntradayChartRequested += code =>
        {
            if (Window.GetWindow(this) is PetPanelWindow panelWindow &&
                panelWindow.Owner is PetWindow petWindow)
                petWindow.ShowIntradayChart(code);
        };
        Log.Information("[PlanListPanel] 构造完成: HasData={HasData}", vm.HasData);
    }

    public void RefreshData()
    {
        if (DataContext is PlanListPanelViewModel vm)
            vm.LoadFromService();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && DataContext is PlanListPanelViewModel vm)
        {
            var status = fe.Tag as string ?? "all";
            vm.FilterByStatus(status);
        }
    }
}