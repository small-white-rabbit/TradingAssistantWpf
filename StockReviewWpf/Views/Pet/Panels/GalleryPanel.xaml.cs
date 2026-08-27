using System;
using System.Windows;
using System.Windows.Controls;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class GalleryPanel : UserControl
{
    public GalleryPanel()
    {
        InitializeComponent();
        // 从 DI 解析外观管理服务（目录加载/安装/激活）；主机未就绪时降级空 VM
        try
        {
            DataContext = new PetGalleryPanelViewModel(App.RequireService<PetManagementService>());
        }
        catch (Exception)
        {
            DataContext = new PetGalleryPanelViewModel();
        }
    }

    /// <summary>
    /// 滚动接近底部（剩不到 1.5 屏）时请求下一批缩略图（+30）。
    /// 连续快速滚动会多次触发，VM 侧有全量授权上限与去重，开销可忽略。
    /// </summary>
    private void OnGalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        if (sv.VerticalOffset < sv.ScrollableHeight - sv.ViewportHeight * 1.5) return;
        if (DataContext is PetGalleryPanelViewModel vm)
            vm.RequestMoreThumbnails();
    }
}