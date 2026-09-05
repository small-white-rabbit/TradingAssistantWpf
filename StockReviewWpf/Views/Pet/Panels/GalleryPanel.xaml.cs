using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class GalleryPanel : UserControl
{
    /// <summary>可见扫描的前向余量（项）：3 列布局下多授权 3 行，滚动到之前图已下载完。</summary>
    private const int VisibleLookaheadItems = 9;

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

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GalleryScroll.ScrollChanged += OnGalleryScrollChanged;
        GalleryItems.ItemContainerGenerator.StatusChanged += OnGeneratorStatusChanged;
        // 面板复用（第二次打开）时容器可能已生成，立即补扫一次
        ScanVisibleThumbnails();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        GalleryScroll.ScrollChanged -= OnGalleryScrollChanged;
        GalleryItems.ItemContainerGenerator.StatusChanged -= OnGeneratorStatusChanged;
    }

    /// <summary>容器生成完成（首次加载/搜索/筛选重建）后首轮扫描：此时才有可靠的 ActualHeight/坐标。</summary>
    private void OnGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (GalleryItems.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            ScanVisibleThumbnails();
    }

    /// <summary>
    /// 滚动/布局变化时按可见区域驱动缩略图授权。
    /// 旧逻辑"距整份列表底部 1.5 屏才扩批"在目录 200+ 时触发点在 ~93% 滚动深度，
    /// 第 30 项之后的卡片长期空白，且滚到最底后滚轮事件 VerticalChange=0 无法继续扩批。
    /// </summary>
    private void OnGalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;
        ScanVisibleThumbnails();
    }

    /// <summary>
    /// 扫描当前视口内可见的卡片，取最后一个可见项下标交给 VM 扩容下载预算。
    /// 坐标统一相对 ScrollViewer 视口：容器 TransformToAncestor(ScrollViewer) 得到的
    /// [top, bottom] 与 [0, ViewportHeight] 求交即可见（不能与 VerticalOffset 内容坐标混用）。
    /// </summary>
    private void ScanVisibleThumbnails()
    {
        if (DataContext is not PetGalleryPanelViewModel vm) return;
        var generator = GalleryItems.ItemContainerGenerator;
        if (generator.Status != GeneratorStatus.ContainersGenerated) return;

        var lastVisible = -1;
        var viewportBottom = GalleryScroll.ViewportHeight > 0 ? GalleryScroll.ViewportHeight : ActualHeight;
        for (var i = 0; i < GalleryItems.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            try
            {
                var topLeft = container.TransformToAncestor(GalleryScroll).Transform(new Point(0, 0));
                var bottom = topLeft.Y + container.ActualHeight;
                if (bottom >= 0 && topLeft.Y <= viewportBottom)
                    lastVisible = i;
            }
            catch (Exception)
            {
                // 容器尚未完成布局（TransformToAncestor 可能抛 InvalidOperationException），跳过本轮
            }
        }

        if (lastVisible >= 0)
            vm.EnsureThumbnailsThroughViewIndex(lastVisible + VisibleLookaheadItems);
    }
}
