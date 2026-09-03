using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class YearMonthView : UserControl
{
    private YearMonthViewModel _viewModel = null!;
    private System.Windows.Threading.DispatcherTimer? _footerConfirmTimer;

    public YearMonthView()
    {
        InitializeComponent();
        _viewModel = new YearMonthViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>(),
            App.RequireService<StockOcrService>(),
            App.RequireService<MarketDataAggregator>());
        DataContext = _viewModel;

        // 写日弹窗：编辑器滚到边界后把滚轮转回宿主，滚动弹窗（WebView2 滚轮不进 WPF 路由）
        DiaryContentEditor.WheelForwarded += DiaryEditor_WheelForwarded;
        // editor.html 回传 body.scrollHeight → 设置编辑器高度跟随内容（+2 容 Host 1px 边框，避免裁剪）；
        // 外层 DiaryScroll 的 MaxHeight 已按视口约束，内容超高时由其滚动，不在编辑器内部加滚动条
        DiaryContentEditor.ContentHeightChanged += (_, h) =>
            DiaryContentEditor.Height = Math.Max(300, h + 2);

        var mvm = App.RequireService<MainViewModel>();

        // 打开写日记弹窗时，把旧内容预填进 wangeditor 编辑器（编辑/新建统一走这里）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(YearMonthViewModel.ShowDiaryDialog) && _viewModel.ShowDiaryDialog)
                DiaryContentEditor.SetHtml(_viewModel.DiaryContent);
        };
        // wangeditor 内容回传（HTML）→ VM（对齐原版 RichTextEditor v-model）
        DiaryContentEditor.HtmlChanged += (_, html) => _viewModel.DiaryContent = html;

        // 跨视图请求：延迟到 Loaded 后执行，避免构造阶段触发弹窗渲染阻塞首屏
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (mvm.PendingEditDiaryId is { } editId)
            {
                mvm.PendingEditDiaryId = null;
                _viewModel.LoadDiaryForEdit(editId);
            }
            else if (mvm.PendingOpenDiary)
            {
                mvm.PendingOpenDiary = false;
                _viewModel.OpenDiaryDialogCommand.Execute(null);
            }

            if (!string.IsNullOrEmpty(mvm.PendingHighlightDate))
                ApplyHighlight(mvm.PendingHighlightDate);
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // 订阅主 VM 的跨视图跳转高亮请求（Insights/案例 → 日历定位）
        mvm.PropertyChanged += MainVm_PropertyChanged;

        // "已到底部"页脚：滚动到最底部才显示（虚拟化列表无法在滚动内容尾部追加元素，改由滚动位置驱动）
        ContentScroll.Loaded += OnContentScrollLoaded;
    }

    private void OnContentScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (GetInnerScrollViewer(ContentScroll) is not { } sv) return;
        sv.ScrollChanged -= OnContentScrollChanged;
        sv.ScrollChanged += OnContentScrollChanged;
        UpdateFooterVisibility(sv);
    }

    private void OnContentScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv) UpdateFooterVisibility(sv);
    }

    private void UpdateFooterVisibility(System.Windows.Controls.ScrollViewer sv)
    {
        if (FooterBar == null) return;
        // 无可滚内容（本就到底）或偏移贴底时才显示
        var atBottom = sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 2;
        if (atBottom)
        {
            // 虚拟化列表测量期间贴底状态可能瞬时成立又消失，延迟确认后再显示，避免频闪
            _footerConfirmTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _footerConfirmTimer.Tick -= FooterConfirmTimer_Tick;
            _footerConfirmTimer.Tick += FooterConfirmTimer_Tick;
            if (!_footerConfirmTimer.IsEnabled) _footerConfirmTimer.Start();
        }
        else
        {
            _footerConfirmTimer?.Stop();
            FooterBar.Visibility = Visibility.Collapsed;
        }
    }

    private void FooterConfirmTimer_Tick(object? sender, EventArgs e)
    {
        _footerConfirmTimer?.Stop();
        if (FooterBar != null) FooterBar.Visibility = Visibility.Visible;
    }

    private void MainVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel mainVm) return;

        // 跨视图高亮跳转请求
        if (e.PropertyName == nameof(MainViewModel.PendingHighlightDate) &&
            !string.IsNullOrEmpty(mainVm.PendingHighlightDate))
        {
            ApplyHighlight(mainVm.PendingHighlightDate);
            mainVm.PendingHighlightDate = "";
        }
        // 日记编辑请求（从 Insights 日记本「编辑」按钮跳转）
        else if (e.PropertyName == nameof(MainViewModel.PendingEditDiaryId) &&
                 mainVm.PendingEditDiaryId is { } editId)
        {
            mainVm.PendingEditDiaryId = null;
            _viewModel.LoadDiaryForEdit(editId);
        }
        // 写日记请求（从 Insights 日记本「写日记」按钮跳转）
        else if (e.PropertyName == nameof(MainViewModel.PendingOpenDiary) &&
                 mainVm.PendingOpenDiary)
        {
            mainVm.PendingOpenDiary = false;
            _viewModel.OpenDiaryDialogCommand.Execute(null);
        }
    }

    private void ApplyHighlight(string date)
    {
        _viewModel.SelectDateCommand.Execute(date);
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => ScrollToDate(date)));
    }

    private void MonthButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonthButtonItem mbi)
        {
            _viewModel.SelectMonthCommand.Execute(mbi.Month.ToString());
        }
    }

    private void DateItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DateCell cell)
        {
            _viewModel.SelectDateCommand.Execute(cell.DateStr);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => ScrollToDate(cell.DateStr)));
        }
    }

    private void DayAddTrade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DayGroupData dg)
        {
            _viewModel.AddTradeCommand.Execute(dg.Date);
        }
    }

    private void TradeScreenshot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TradeRecord trade)
        {
            _viewModel.PreviewScreenshotCommand.Execute(trade);
            e.Handled = true;
        }
    }

    private void DeleteTrade_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TradeRecord trade)
        {
            _viewModel.DeleteTradeCommand.Execute(trade.Id);
            e.Handled = true;
        }
    }

    private void StrongStock_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is StrongStockItem stock)
        {
            _viewModel.ViewStrongStockCommand.Execute(stock);
            e.Handled = true;
        }
    }

    private void Overlay_Click(object sender, MouseButtonEventArgs e)
    {
        // 仅当直接点击遮罩本身（而非对话框内部元素冒泡上来）时才关闭
        if (e.OriginalSource != sender) return;
        _viewModel.CloseStrongDialogCommand.Execute(null);
        _viewModel.CloseDiaryDialogCommand.Execute(null);
        _viewModel.CloseScreenshotPreviewCommand.Execute(null);
        _viewModel.CloseFormCommand.Execute(null);
    }

    /// <summary>
    /// 新增记录弹窗打开时，滚轮在整个 App 上层都滚动弹窗内容：
    /// 光标在弹窗内 → ScrollViewer 已处理（Handled=true）直接跳过；
    /// 光标在遮罩/弹窗边缘 → 事件未处理，手动转发给弹窗的 ScrollViewer。
    /// </summary>
    private void TradeFormOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        FormScrollViewer.ScrollToVerticalOffset(FormScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// 写日记弹窗：弹窗展示不完全时，鼠标在任意位置滚轮都滚动弹窗内容（对齐交易记录弹窗）。
    /// 光标在弹窗内容上 → ScrollViewer 已处理直接跳过；在遮罩/标题等非滚动区 → 手动转发。
    /// </summary>
    private void DiaryOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (DiaryScroll == null) return;
        DiaryScroll.ScrollToVerticalOffset(DiaryScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// 写日记弹窗：WebView2 编辑器是独立子窗口，滚轮消息不会路由回 WPF；
    /// 编辑器滚到边界后由页面转发（WheelForwarded），这里接续滚动弹窗，
    /// 实现光标在弹窗内（含编辑器区域）任意位置都能滚动弹窗。
    /// </summary>
    private void DiaryEditor_WheelForwarded(object? sender, double deltaY)
    {
        if (DiaryScroll == null) return;
        DiaryScroll.ScrollToVerticalOffset(DiaryScroll.VerticalOffset - deltaY);
    }

    /// <summary>写日记弹窗：内容高度自适应视口（留 48px 呼吸边距），窗口过矮时弹窗内部可滚动。</summary>
    private void DiaryOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DiaryScroll == null) return;
        DiaryScroll.MaxHeight = Math.Max(200, e.NewSize.Height - 48);
    }

    /// <summary>
    /// 滚动内容区到指定日期所在的日分组（按 Tag 查找）。
    /// </summary>
    private void ScrollToDate(string dateStr)
    {
        if (ContentScroll == null) return;
        // 从 ItemsControl 模板中获取内部 ScrollViewer
        var scrollViewer = GetInnerScrollViewer(ContentScroll);
        if (scrollViewer == null) return;
        var target = FindByTag(ContentScroll, dateStr);
        if (target == null) return;
        var transform = target.TransformToAncestor(scrollViewer);
        var offset = transform.Transform(new System.Windows.Point(0, 0)).Y;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, offset - 12));
    }

    private static System.Windows.Controls.ScrollViewer? GetInnerScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            var found = GetInnerScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private static FrameworkElement? FindByTag(DependencyObject parent, string tag)
    {
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Tag is string t && t == tag)
                return fe;
            var found = FindByTag(child, tag);
            if (found != null) return found;
        }
        return null;
    }
}
