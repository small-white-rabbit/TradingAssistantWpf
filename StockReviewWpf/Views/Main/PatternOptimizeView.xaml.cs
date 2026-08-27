using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class PatternOptimizeView : UserControl
{
    private readonly PatternOptimizeViewModel _vm;

    public PatternOptimizeView()
    {
        InitializeComponent();
        _vm = new PatternOptimizeViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>(),
            App.RequireService<StockOcrService>(),
            App.RequireService<MarketDataAggregator>(),
            App.RequireService<MainViewModel>());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PatternOptimizeViewModel.DetailStandardForm)
            or nameof(PatternOptimizeViewModel.DetailNotes)
            or nameof(PatternOptimizeViewModel.DetailReflections))
            LoadDetailReflections();
        else if (e.PropertyName == nameof(PatternOptimizeViewModel.IsImagePreviewVisible) && _vm.IsImagePreviewVisible)
            ResetPreviewZoom();
        else if (e.PropertyName == nameof(PatternOptimizeViewModel.IsCaseCompareVisible) && _vm.IsCaseCompareVisible)
        {
            ResetCompareZoom(0);
            ResetCompareZoom(1);
        }
    }

    // ---- 形态详情富文本渲染 ----
    private void LoadDetailReflections()
    {
        if (DetailStandardFormRtb != null) RichTextUtil.LoadInto(DetailStandardFormRtb, _vm.DetailStandardForm);
        if (DetailNotesRtb != null) RichTextUtil.LoadInto(DetailNotesRtb, _vm.DetailNotes);
        if (DetailReflectionsRtb != null) RichTextUtil.LoadInto(DetailReflectionsRtb, _vm.DetailReflections);
    }

    // ============ 左侧类型导航 ============
    private void TypeItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TypeNavItem item)
            _vm.SelectTypeItemCommand.Execute(item);
    }

    // ============ 概览 tile：图示标签页 / 滚轮切换 / 点击预览 ============
    // tile 的标签页与图片位于 DataTemplate 内，无法作为字段访问，
    // 通过 Name 在视觉树中定位（每个 tile 实例独立）。
    private void TileTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border tab) return;
        if (tab.DataContext is not PatternStat stat) return;
        var index = tab.Name == "TileTabType" ? 1 : 0;
        if (index == 0 && string.IsNullOrEmpty(stat.StandardFormImage)) return;
        if (index == 1 && string.IsNullOrEmpty(stat.TypeImage)) return;
        ApplyTileTab(tab, index);
        e.Handled = true;
    }

    private void TileImageArea_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border area) ApplyTileTab(area, 0);
    }

    /// <summary>概览 tile 内层滚动到边界（或内容不超高）时把滚轮转发给外层主滚动区。
    /// WPF 嵌套 ScrollViewer 默认吞掉 MouseWheel，是页面滚动大面积“卡死”的根因。</summary>
    private void InnerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var noOverflow = sv.ExtentHeight <= sv.ViewportHeight + 1;
        var atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
        var atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 1 && e.Delta < 0;
        if (!noOverflow && !atTop && !atBottom) return;

        e.Handled = true; // 抢先标记，阻止内层自己消费
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sv
        };
        DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(sv);
        while (parent != null && parent is not ScrollViewer)
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        // 逐级向上找第一个 ScrollViewer 祖先（即主滚动区）并让滚轮生效
        (parent as UIElement)?.RaiseEvent(args);
    }

    private void TileImageArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border area || area.DataContext is not PatternStat stat) return;
        var showStandard = FindByName(area, "TileImgStandard")?.Visibility == Visibility.Visible;
        var url = showStandard
            ? (stat.StandardFormImage ?? stat.TypeImage)
            : (stat.TypeImage ?? stat.StandardFormImage);
        if (!string.IsNullOrEmpty(url)) _vm.OpenImagePreviewCommand.Execute(url);
        e.Handled = true;
    }

    private static void ApplyTileTab(DependencyObject reference, int index)
    {
        var root = FindTileRoot(reference);
        if (root == null || root.DataContext is not PatternStat stat) return;
        var tabStd = FindByName(root, "TileTabStandard") as Border;
        var tabType = FindByName(root, "TileTabType") as Border;
        var imgStd = FindByName(root, "TileImgStandard");
        var imgType = FindByName(root, "TileImgType");
        var empty = FindByName(root, "TileImgEmpty");

        var hasStd = !string.IsNullOrEmpty(stat.StandardFormImage);
        var hasType = !string.IsNullOrEmpty(stat.TypeImage);
        // 与原版 getCurrentImage 一致：当前图缺失时回退另一张
        FrameworkElement? shown = index == 0
            ? (hasStd ? imgStd : hasType ? imgType : null)
            : (hasType ? imgType : hasStd ? imgStd : null);

        if (imgStd != null) imgStd.Visibility = shown == imgStd ? Visibility.Visible : Visibility.Collapsed;
        if (imgType != null) imgType.Visibility = shown == imgType ? Visibility.Visible : Visibility.Collapsed;
        if (empty != null) empty.Visibility = shown == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabVisual(tabStd, index == 0);
        UpdateTabVisual(tabType, index == 1);
    }

    private static void UpdateTabVisual(Border? tab, bool active)
    {
        if (tab == null) return;
        tab.Background = new SolidColorBrush(active ? Colors.White : (Color)ColorConverter.ConvertFromString("#F0F0F0"));
        if (tab.Child is TextBlock tb)
            tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                active ? "#1B1E22" : "#6B7178"));
    }

    private static FrameworkElement? FindTileRoot(DependencyObject start)
    {
        var cur = start;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && fe.Name == "TileFormBlock") return fe;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var found = FindByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // ============ 概览 tile：双击行内编辑 ============
    // 用 PreviewMouseLeftButtonDown（隧道事件）：从根先于子元素到达 Border，
    // 任何子元素的冒泡拦截（Handled/鼠标捕获）都无法阻止双击触达此处。
    private void TileBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border block) return;
        // 每次按下均留痕（含 ClickCount=1 的首击）：下次复现可直接定位失败环节
        // —— 无此日志=事件未达；仅 ClickCount=1=双击未连击；=2 但无后续=Tag/DC 检查失败
        Serilog.Log.Information("[模式优化] tile 按下: Tag={Tag} ClickCount={Count}",
            block.Tag as string ?? "<非字符串>", e.ClickCount);
        if (e.ClickCount != 2) return;
        if (block.Tag is not string field || block.DataContext is not PatternStat stat) return;
        if (stat.EditingField == field) return; // 正在编辑该区块时忽略内部双击，避免重置已输入内容
        Serilog.Log.Information("[模式优化] 进入行内编辑: {Type} 字段={Field}", stat.TypeName, field);
        _vm.StartTileEdit(stat, field);
        e.Handled = true;
        // 编辑区由 DataTrigger 渲染，延迟到布局更新后聚焦，光标置于末尾
        Dispatcher.BeginInvoke(() =>
        {
            var tb = FindDescendant<TextBox>(block);
            if (tb == null) return;
            tb.Focus();
            tb.SelectionStart = tb.Text.Length;
            tb.SelectionLength = 0;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ============ 概览 tile：案例排行点击跳转 ============
    private void RankItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PatternCaseBrief brief)
            _vm.GoToCaseDetailCommand.Execute(brief);
    }

    // ============ 案例卡片：点击勾选对比 ============
    private void CaseCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CaseItem item)
            _vm.ToggleCaseSelectCommand.Execute(item);
    }

    // ============ 案例卡片：删除自定义案例（阻止冒泡触发勾选） ============
    private void CardDelete_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is CaseItem item)
            _vm.DeleteCustomCaseCommand.Execute(item);
    }

    // ============ 覆盖层点击关闭（仅点击遮罩本身） ============
    private void AddCaseOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsAddCaseVisible = false;
    }

    private void EditOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsEditPatternVisible = false;
    }

    private void SelectScreenshotOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsSelectScreenshotVisible = false;
    }

    private void CaseCompareOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsCaseCompareVisible = false;
    }

    private void DialogInner_Click(object sender, MouseButtonEventArgs e) { }

    // ============ 从案例选择截图 ============
    private void CaseScreenshotPick_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CaseItem item)
            _vm.SelectScreenshotCommand.Execute(item);
    }

    // ============ 案例对比：缩放与拖拽 ============
    private void CompareZoomIn_Click(object sender, RoutedEventArgs e) => ZoomCompare(GetCompareIndex(sender), 0.1);
    private void CompareZoomOut_Click(object sender, RoutedEventArgs e) => ZoomCompare(GetCompareIndex(sender), -0.1);
    private void CompareZoomReset_Click(object sender, RoutedEventArgs e) => ResetCompareZoom(GetCompareIndex(sender));

    private void CompareImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomCompare(GetCompareIndex(sender), e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    private static int GetCompareIndex(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "1" ? 1 : 0;

    private void ZoomCompare(int index, double delta)
    {
        var scale = index == 0 ? LeftScale : RightScale;
        var next = Math.Round(scale.ScaleX + delta, 2);
        if (next < 0.5 || next > 3) return;
        scale.ScaleX = scale.ScaleY = next;
        (index == 0 ? LeftZoomText : RightZoomText).Text = (int)Math.Round(next * 100) + "%";
    }

    private void ResetCompareZoom(int index)
    {
        var scale = index == 0 ? LeftScale : RightScale;
        scale.ScaleX = scale.ScaleY = 1;
        var tr = index == 0 ? LeftTranslate : RightTranslate;
        tr.X = tr.Y = 0;
        (index == 0 ? LeftZoomText : RightZoomText).Text = "100%";
    }

    private bool _compareDragging;
    private int _compareDragIndex = -1;
    private Point _compareDragStart;
    private Point _compareImgStart;

    private void CompareImg_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image img) return;
        var index = GetCompareIndex(img);
        if ((index == 0 ? LeftScale : RightScale).ScaleX <= 1) return; // 未放大时不允许拖拽
        _compareDragging = true;
        _compareDragIndex = index;
        _compareDragStart = e.GetPosition(null);
        var tr = index == 0 ? LeftTranslate : RightTranslate;
        _compareImgStart = new Point(tr.X, tr.Y);
        img.CaptureMouse();
        e.Handled = true;
    }

    private void CompareImg_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_compareDragging || GetCompareIndex(sender) != _compareDragIndex) return;
        var pos = e.GetPosition(null);
        var tr = _compareDragIndex == 0 ? LeftTranslate : RightTranslate;
        tr.X = _compareImgStart.X + (pos.X - _compareDragStart.X);
        tr.Y = _compareImgStart.Y + (pos.Y - _compareDragStart.Y);
        e.Handled = true;
    }

    private void CompareImg_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndCompareDrag(sender);

    private void CompareImg_MouseLeave(object sender, MouseEventArgs e) => EndCompareDrag(sender);

    private void EndCompareDrag(object sender)
    {
        if (sender is Image img && img.IsMouseCaptured) img.ReleaseMouseCapture();
        _compareDragging = false;
    }

    // ============ 图片预览：缩放与拖拽 ============
    private void PreviewZoomIn_Click(object sender, RoutedEventArgs e) => ZoomPreview(0.2);
    private void PreviewZoomOut_Click(object sender, RoutedEventArgs e) => ZoomPreview(-0.2);
    private void PreviewZoomReset_Click(object sender, RoutedEventArgs e) => ResetPreviewZoom();

    private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomPreview(e.Delta > 0 ? 0.2 : -0.2);
        e.Handled = true;
    }

    private void ZoomPreview(double delta)
    {
        var next = Math.Round(PreviewScale.ScaleX + delta, 2);
        if (next < 0.5 || next > 3) return;
        PreviewScale.ScaleX = PreviewScale.ScaleY = next;
        PreviewZoomText.Text = (int)Math.Round(next * 100) + "%";
    }

    private void ResetPreviewZoom()
    {
        PreviewScale.ScaleX = PreviewScale.ScaleY = 1;
        PreviewTranslate.X = PreviewTranslate.Y = 0;
        PreviewZoomText.Text = "100%";
    }

    private bool _previewDragging;
    private Point _previewDragStart;
    private Point _previewImgStart;

    private void PreviewImg_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _previewDragging = true;
        _previewDragStart = e.GetPosition(null);
        _previewImgStart = new Point(PreviewTranslate.X, PreviewTranslate.Y);
        if (sender is Image img) img.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewImg_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_previewDragging) return;
        var pos = e.GetPosition(null);
        PreviewTranslate.X = _previewImgStart.X + (pos.X - _previewDragStart.X);
        PreviewTranslate.Y = _previewImgStart.Y + (pos.Y - _previewDragStart.Y);
        e.Handled = true;
    }

    private void PreviewImg_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPreviewDrag(sender);

    private void PreviewImg_MouseLeave(object sender, MouseEventArgs e) => EndPreviewDrag(sender);

    private void EndPreviewDrag(object sender)
    {
        if (sender is Image img && img.IsMouseCaptured) img.ReleaseMouseCapture();
        _previewDragging = false;
    }

    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsImagePreviewVisible = false;
    }
}
