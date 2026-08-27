using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class CasesView : UserControl
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
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CasesViewModel.IsDetailVisible) && _vm.IsDetailVisible)
                LoadReflection(_vm.SelectedCase);
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

    // 用户要求满屏一排放 8 张：可用宽度 / (222 卡宽 + 12 间距) 取整，最小1列最多8列
    // 1920 全屏 ≈ 1872 / 234 = 8 列；更窄屏幕自动降列，卡片宽随列宽收缩（MaxWidth=320 封顶）
    private void UpdateCardColumns()
    {
        var available = ActualWidth - 40; // 减去 ScrollViewer Padding
        if (available < 240) { _vm.CardColumns = 1; return; }
        var cols = (int)(available / 234);
        _vm.CardColumns = Math.Max(1, Math.Min(8, cols));
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
                Dispatcher.BeginInvoke(
                    new Action(() => FixFrozenTransformsInContainer(CardsList)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }
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