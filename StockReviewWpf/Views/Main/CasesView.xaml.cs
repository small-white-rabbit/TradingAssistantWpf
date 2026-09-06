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

public partial class CasesView : UserControl, IItemSizeProvider
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
    // 槽位宽 = 视口宽 / n，StretchItems=True 让容器拉伸平分视口（等同 1fr），
    // 卡片左右各 6px Margin 拼出 12px 列间隙、行首行尾对称（原版 gap 行为）。
    private VirtualizingWrapPanel? _cardsPanel;

    // 可变行高参数（与 XAML 模板对应）：
    // 默认卡（有截图、无反思）内容合计 ≈ 380；截图块 240 高 + 8 间距 = 248；卡片底部 Margin 12。
    private const double BaseCardHeight = 380;
    private const double ShotBlockHeight = 248;
    private const double CalibrationBlockHeight = 140; // 卖点校准块：标签换行 + 3 行指标（估算上限）
    private const double RowGap = 12;

    private void UpdateCardColumns()
    {
        if (ActualWidth <= 0) return;
        // ItemsPanelTemplate 内的面板不在 UserControl 命名域，需经可视化树查找（模板应用后才存在）
        _cardsPanel ??= FindVisualChild<VirtualizingWrapPanel>(CardsList);
        if (_cardsPanel == null) return;
        // 可变行高模式下，快速滚动时未实例化卡片的位置由 ItemSizeProvider 按数据预估
        _cardsPanel.ItemSizeProvider ??= this;
        var outer = ActualWidth - 36;                                    // ListBox 左右各 18 边距
        var viewport = outer - SystemParameters.VerticalScrollBarWidth;  // 面板实际可用宽（扣除滚动条占位）
        var cols = Math.Max(1, (int)((viewport + 12) / 312));            // auto-fill minmax(300,1fr) gap 12
        var pitch = viewport / cols - 0.5; // 略收 0.5px 兜底浮点取整，保证面板恰好排下 cols 列
        _cardsPanel.ItemSize = new Size(Math.Max(120, pitch), BaseCardHeight + RowGap);
    }

    /// <summary>
    /// 按数据预估卡片槽位尺寸（AllowDifferentSizedItems=True 时快速滚动定位用）。
    /// 非精确值：可视区卡片仍以实测尺寸排列，预估偏差只在快速滚动瞬间存在。
    /// </summary>
    public Size GetSizeForItem(object item)
    {
        var width = _cardsPanel?.ItemSize.Width is > 0 ? _cardsPanel.ItemSize.Width : 300.0;
        var cardHeight = BaseCardHeight;
        if (item is CaseItem c)
        {
            if (!c.HasScreenshot) cardHeight -= ShotBlockHeight;        // 无截图卡矮一个截图块
            if (c.ShowReflection) cardHeight += EstimateReflectionHeight(c.ReflectionPlain);
            if (c.IsCalibrationTab) cardHeight += CalibrationBlockHeight;
        }
        return new Size(width, cardHeight + RowGap);
    }

    // 反思文本 12px 自动换行：卡片内宽约 270px ≈ 每行 20 个中文字符，行高约 17px；
    // 模板 MaxHeight=54（约 3 行）+ 底部 8px 间距
    private static double EstimateReflectionHeight(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var lines = (text.Length + 19) / 20;
        return Math.Min(54, lines * 17) + 8;
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
}