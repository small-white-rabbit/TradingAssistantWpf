using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;
using WpfToolkit.Controls;

namespace StockReviewWpf.Views.Main;

public partial class CasesView : UserControl, ITrayScreenshotLifecycle
{
    private readonly CasesViewModel _vm;
    private bool _transformsFixed;

    public CasesView()
    {
        InitializeComponent();
        _vm = new CasesViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>());
        DataContext = _vm;
        Loaded += (s, e) => UpdateCardColumns();
        SizeChanged += (s, e) => UpdateCardColumns();
        // 卡片视图滚动接近底部自动加载下一页（ListBox 内置 ScrollViewer 的 ScrollChanged 冒泡到 ListBox）
        CardsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CardsList_ScrollChanged));
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CasesViewModel.IsDetailVisible) && _vm.IsDetailVisible)
                LoadReflection(_vm.SelectedCase);
            // 每次打开大图预览：滚动位置归零，避免沿用上一张图的偏移
            if (e.PropertyName == nameof(CasesViewModel.IsImagePreviewVisible) && _vm.IsImagePreviewVisible)
                Dispatcher.BeginInvoke(new Action(() => PreviewScroll?.ScrollToHome()),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        };
        // 修复 DataTemplate 中 TranslateTransform/ScaleTransform 被 WPF 冻结导致的全屏异常
        // 在 ItemsControl 生成容器后异步替换冻结的 Transform
        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(
                new Action(() => FixFrozenTransforms()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void LoadReflection(CaseItem? item)
    {
        if (CaseReflectionRtb == null || item == null) return;
        CaseReflectionRtb.Document.Blocks.Clear();
        StockReviewWpf.Services.RichTextUtil.LoadInto(CaseReflectionRtb, item.Reflection);
    }

    // 列密度对齐 Electron 原版 .card-grid：grid-template-columns: repeat(auto-fill, minmax(300px,1fr)); gap:12px。
    // 列数 n = floor((视口宽 + gap) / (最小列宽 300 + gap))；虚拟化面板把"列数"翻译成槽位宽：
    // 槽位宽 = 视口宽 / n，卡片以 Min=Max 宽度绑定精确取该宽（不随行拉伸）。
    // 反思区宽 = 卡片宽 − 左右内边距 30（距卡片左右边 15px，由 Border Padding 15 提供）。
    private VirtualizingWrapPanel? _cardsPanel;

    // 槽位宽（同步给 VM.CardSlotWidth 作为卡片 Border 的 Min/MaxWidth，并作未实例化项的兜底宽）
    private double _pitch = 300;

    private void UpdateCardColumns()
    {
        if (ActualWidth <= 0) return;
        // ItemsPanelTemplate 内的面板不在 UserControl 命名域，需经可视化树查找（模板应用后才存在）
        _cardsPanel ??= FindVisualChild<VirtualizingWrapPanel>(CardsList);
        if (_cardsPanel == null) return;
        var outer = ActualWidth - 36;                                    // ListBox 左右各 18 边距
        var viewport = outer - SystemParameters.VerticalScrollBarWidth;  // 面板实际可用宽（扣除滚动条占位）
        var cols = Math.Max(1, (int)((viewport + 12) / 312));            // auto-fill minmax(300,1fr) gap 12
        _pitch = Math.Max(120, viewport / cols - 0.5); // 略收 0.5px 兜底浮点取整，保证面板恰好排下 cols 列
        // 真实高度模式：不设 ItemSize/ItemSizeProvider——vwp 2.5.4 会把该尺寸硬钳为测量约束，
        // 预估偏大=行底大空白、偏小=内容被下一行盖住。卡片以无限约束实测，高度=内容真实高度，
        // 行高=行内最高真实卡片。未实例化项的滚动定位用 FallbackItemSize 兜底（实例化后由面板
        // itemSizesCache 按真实尺寸自学习）；该属性带 AffectsMeasure 元数据，写入即触发重排。
        _cardsPanel.FallbackItemSize = new Size(_pitch, 400);
        if (DataContext is CasesViewModel vm)
        {
            vm.CardSlotWidth = _pitch - 12;        // 卡片 Border 宽 = 槽位 − 左右 Margin 6×2
            vm.ReflectionWidth = _pitch - 12 - 30; // 反思区宽 = 卡片宽 − 左右 Padding 15×2（距卡边 15px）
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } result) return result;
        }
        return null;
    }

    /// <summary>
    /// 递归遍历 ItemsControl 内所有 Border/Image，将冻结的 TranslateTransform/ScaleTransform 替换为可动画实例。
    /// 修复 WPF DataTemplate 中 Freezable 被密封导致 Storyboard 动画抛 InvalidOperationException 的问题。
    /// Bug: 日志单日 15.5万次 "无法在'System.Windows.Media.TranslateTransform'上激活'Y'属性，因为该对象已密封或已冻结"
    /// </summary>
    private void FixFrozenTransforms()
    {
        if (_transformsFixed) return;
        _transformsFixed = true;
        FixFrozenTransformsInContainer(CardsList);
        // 监听虚拟化容器的后续生成
        if (CardsList.ItemContainerGenerator != null)
        {
            CardsList.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                // 内存治理（2026-09-06 v3）：虚拟化后 StatusChanged 在滚动时频繁触发，
                // 仅在"一批容器生成完毕"时执行，避免中间状态多次全树遍历
                if (CardsList.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    return;
                Dispatcher.BeginInvoke(
                    new Action(() => FixFrozenTransformsInContainer(CardsList)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }
    }

    /// <summary>内存治理（2026-09-06 v2）：视图切走时清空截图字符串驻留（切回全树重新 Loaded → 自动重载）</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _vm.ClearTransientScreenshots();
    }

    private static void FixFrozenTransformsInContainer(DependencyObject parent)
    {
        if (parent == null) return;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.RenderTransform is TranslateTransform t && t.IsFrozen)
            {
                border.RenderTransform = new TranslateTransform(0, 0);
            }
            if (child is Image image && image.RenderTransform is ScaleTransform st && st.IsFrozen)
            {
                image.RenderTransform = new ScaleTransform(1, 1);
            }
            FixFrozenTransformsInContainer(child);
        }
    }

    // ============ 卡片悬停上浮动画（对齐原版 hover translateY(-5px) transition 0.3s） ============
    // 终值取整数像素并配合 UseLayoutRounding，避免亚像素偏移导致文字发虚
    private void CaseCard_MouseEnter(object sender, MouseEventArgs e) => AnimateCardLift(sender, -5);

    private void CaseCard_MouseLeave(object sender, MouseEventArgs e) => AnimateCardLift(sender, 0);

    private static void AnimateCardLift(object sender, double target)
    {
        if (sender is not Border b) return;

        // DataTemplate 里声明的 TranslateTransform 会被 WPF 模板优化冻结（IsFrozen=true），
        // 直接 BeginAnimation 会抛 InvalidOperationException（日志里单日 265 次）。
        // 冻结实例就地替换为新的可动画实例，一次性治本。
        if (b.RenderTransform is not TranslateTransform t || t.IsFrozen)
        {
            t = new TranslateTransform();
            b.RenderTransform = t;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
        };
        t.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // ============ 筛选交互 ============
    private void EntryType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.SelectedValue is string v)
            _vm.ChangeEntryTypeCommand.Execute(v);
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.SelectedValue is string v)
            _vm.ChangeSortCommand.Execute(v);
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.SearchCommand.Execute(null);
    }

    // ============ 滚动接近底部自动分页加载（替代手动"加载更多"按钮） ============
    private void CardsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeight <= 0 || e.ExtentHeight <= 0) return;
        // 剩余可滚动距离不足约一个视口（+80px 提前量）即触发下一页；
        // 首页内容不足两个视口时也会连锁补载，直到填满视口或没有更多。
        // HasMore / IsLoading 由 ViewModel.LoadMore 守卫，重复触发安全。
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ViewportHeight - 80)
            _vm.LoadMoreCmdCommand.Execute(null);
    }

    // ============ 卡片截图预览 ============
    private void CaseScreenshot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CaseItem item)
            _vm.PreviewImageCommand.Execute(item);
    }

    // ============ 列表行点击查看详情 ============
    private void ListRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CaseItem item)
            _vm.ShowDetailCommand.Execute(item);
    }

    // ============ 卡片信息区双击打开详情 ============
    // 反思较长/卡片较高时，底部"详情"按钮可能被下一行卡片遮挡或落在视口外，
    // 双击截图上方信息区（涨跌幅/案例类型/股票名称/时间，校准 Tab 还含校准数据）为同等入口。
    // 单击不触发：截图区域单击是大图预览，避免误触；双击仅响应 ClickCount==2。
    private void CardInfo_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is CaseItem item)
            _vm.ShowDetailCommand.Execute(item);
    }

    // ============ 详情弹窗 ============
    private void DetailOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _vm.IsDetailVisible = false;
    }

    private void DialogInner_Click(object sender, MouseButtonEventArgs e)
    {
        // 阻止冒泡到 overlay 关闭
    }

    private void DetailScreenshot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedCase != null)
            _vm.PreviewImageCommand.Execute(_vm.SelectedCase);
    }

    // ============ 图片预览 ============
    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _vm.IsImagePreviewVisible = false;
    }

    private void PreviewImage_Click(object sender, MouseButtonEventArgs e) => _vm.IsImagePreviewVisible = false;

    // ============ 截图懒加载 ============
    // 卡片进入可视区或分页重建换绑记录时，按需读盘该卡截图
    private void CaseImage_Loaded(object sender, RoutedEventArgs e) => RequestShot(sender);

    private void CaseImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestShot(sender);

    private void RequestShot(object sender)
    {
        if (sender is FrameworkElement { DataContext: CaseItem rec })
            _vm.RequestScreenshot(rec);
    }

    // ===== 托盘隐藏/恢复的截图驻留生命周期（2026-09-06 P1，接口 ITrayScreenshotLifecycle）=====
    /// <summary>主窗隐藏到托盘：清空全部截图字符串驻留（与 View_Unloaded 同路径）。</summary>
    public void ReleaseTransientScreenshots() => _vm.ClearTransientScreenshots();

    /// <summary>主窗恢复显示：对可视树中已 realize 卡片的 Image 重发懒加载请求
    ///（RequestShot 按 DataContext 类型过滤，图标/预览图自动跳过；虚拟化列表仅命中可视区±缓冲）。</summary>
    public void ReloadVisibleScreenshots()
    {
        foreach (var img in VisualTreeUtil.EnumerateImages(this))
            RequestShot(img);
    }
}