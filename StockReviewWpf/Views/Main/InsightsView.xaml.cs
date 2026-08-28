using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class InsightsView : UserControl
{
    private readonly InsightsViewModel _vm;

    public InsightsView()
    {
        InitializeComponent();
        _vm = new InsightsViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>(),
            App.RequireService<StockOcrService>(),
            App.RequireService<MainViewModel>());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ContentEditor.HtmlChanged += ContentEditor_HtmlChanged;
        // WebView2 滚轮不进 WPF 路由：编辑器滚到边界后转回宿主，滚动弹窗（对齐写日弹窗）
        ContentEditor.WheelForwarded += (_, deltaY) =>
            EditDialogScroll.ScrollToVerticalOffset(EditDialogScroll.VerticalOffset - deltaY);

        // 写日记弹窗（从交易记录页移植）：编辑器滚轮转发 + 内容回传 + 内容高度自适应
        DiaryEditContentEditor.WheelForwarded += DiaryEditEditor_WheelForwarded;
        DiaryEditContentEditor.HtmlChanged += (_, html) => _vm.DiaryContent = html;
        // editor.html 回传 body.scrollHeight → 设置编辑器高度跟随内容（+2 容 Host 1px 边框，避免裁剪）；
        // 外层 DiaryEditScroll 的 MaxHeight 已按视口约束，内容超高时由其滚动，不在编辑器内部加滚动条
        DiaryEditContentEditor.ContentHeightChanged += (_, h) =>
            DiaryEditContentEditor.Height = Math.Max(300, h + 2);
    }

    /// <summary>
    /// 内层 ScrollViewer（心得/日记列表区）转发滚轮到外层 OuterScroll。
    /// 嵌套 ScrollViewer 默认会"吃掉"滚轮事件（即使自身不滚动也 e.Handled=true），
    /// 导致外层页面级滚动失效，鼠标滚轮不动。这里在 Preview 阶段接管，
    /// 直接滚动外层并阻止内层处理，恢复滚轮体验。
    /// </summary>
    private void InnerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (OuterScroll == null) return;
        OuterScroll.ScrollToVerticalOffset(OuterScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// 心得编辑弹窗：光标在遮罩/非滚动区时滚轮也滚动弹窗内容
    /// （光标在弹窗内容上时 ScrollViewer 已自行处理，e.Handled=true 会跳过）。
    /// </summary>
    private void EditOverlay_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        EditDialogScroll.ScrollToVerticalOffset(EditDialogScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InsightsViewModel.IsEditVisible) && _vm.IsEditVisible)
            ContentEditor.SetHtml(_vm.FormContent);
        else if (e.PropertyName == nameof(InsightsViewModel.ShowDiaryDialog) && _vm.ShowDiaryDialog)
            DiaryEditContentEditor.SetHtml(_vm.DiaryContent);
        else if (e.PropertyName == nameof(InsightsViewModel.IsDetailVisible) && _vm.IsDetailVisible)
            LoadDetailContent(_vm.SelectedInsight);
        else if (e.PropertyName == nameof(InsightsViewModel.IsDiaryDetailVisible) && _vm.IsDiaryDetailVisible)
            LoadDiaryDetail(_vm.SelectedDiary);
        else if (e.PropertyName == nameof(InsightsViewModel.SelectedDiary) && _vm.IsDiaryDetailVisible)
            LoadDiaryDetail(_vm.SelectedDiary);
    }

    // ===== 写日记弹窗（从交易记录页移植） =====

    /// <summary>点击遮罩关闭写日记弹窗。</summary>
    private void DiaryEditOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _vm.CloseDiaryDialogCommand.Execute(null);
    }

    /// <summary>写日记弹窗：光标在遮罩/非滚动区时滚轮也滚动弹窗内容。</summary>
    private void DiaryEditOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (DiaryEditScroll == null) return;
        DiaryEditScroll.ScrollToVerticalOffset(DiaryEditScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>WebView2 编辑器滚到边界后转发滚轮，接续滚动弹窗。</summary>
    private void DiaryEditEditor_WheelForwarded(object? sender, double deltaY)
    {
        if (DiaryEditScroll == null) return;
        DiaryEditScroll.ScrollToVerticalOffset(DiaryEditScroll.VerticalOffset - deltaY);
    }

    /// <summary>写日记弹窗：内容高度自适应视口（留 48px 呼吸边距）。</summary>
    private void DiaryEditOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DiaryEditScroll == null) return;
        DiaryEditScroll.MaxHeight = Math.Max(200, e.NewSize.Height - 48);
    }

    // 写心得弹窗：股票代码框回车 → 立即按代码补名称
    private void StockCodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.StockCodeFilledCommand.Execute(null);
    }

    // 写心得弹窗：股票代码框失焦 → 按代码补名称（兜底）
    private void StockCodeInput_LostFocus(object sender, RoutedEventArgs e)
    {
        _vm.StockCodeFilledCommand.Execute(null);
    }

    // 写心得弹窗：股票名称框回车 → 立即按名称补代码
    private void StockNameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.StockNameFilledCommand.Execute(null);
    }

    // 写心得弹窗：股票名称框失焦 → 按名称补代码（兜底）
    private void StockNameInput_LostFocus(object sender, RoutedEventArgs e)
    {
        _vm.StockNameFilledCommand.Execute(null);
    }

    private void LoadDiaryDetail(DiaryItem? item)
    {
        if (DiaryDetailRtb == null || item == null) return;
        DiaryDetailRtb.Document.Blocks.Clear();
        RichTextUtil.LoadInto(DiaryDetailRtb, item.Summary);
        // 回退：富文本渲染失败或内容为空时，显示纯文本摘要
        if (DiaryDetailRtb.Document.Blocks.Count == 0 && !string.IsNullOrEmpty(item.PlainContent))
        {
            DiaryDetailRtb.Document.Blocks.Add(new Paragraph(new Run(item.PlainContent)));
        }
    }

    private void LoadDetailContent(InsightItem? item)
    {
        if (DetailRtb == null || item == null) return;
        DetailRtb.Document.Blocks.Clear();
        RichTextUtil.LoadInto(DetailRtb, item.Content);
        // 对齐原版 .detail-body：15px / #606266 / line-height 1.8 / 段距 12px
        DetailRtb.Document.FontSize = 15;
        var bodyBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x60, 0x62, 0x66));
        bodyBrush.Freeze();
        DetailRtb.Document.Foreground = bodyBrush;
        foreach (var p in DetailRtb.Document.Blocks.OfType<Paragraph>())
        {
            p.LineHeight = 27;
            p.Margin = new Thickness(0, 0, 0, 12);
        }
    }

    // wangeditor 内容回传（HTML）→ VM（对齐原版 RichTextEditor v-model）
    private void ContentEditor_HtmlChanged(object? sender, string html) => _vm.FormContent = html;

    private void DiaryDetailOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _vm.IsDiaryDetailVisible = false;
    }

    /// <summary>双击日记卡片/纸张内容区 → 打开详情弹窗</summary>
    private void DiaryCard_DetailClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        DiaryItem? item = null;
        if (sender is FrameworkElement el && el.Tag is DiaryItem d)
            item = d;
        else if (sender is FrameworkElement el2 && el2.DataContext is DiaryItem d2)
            item = d2;
        if (item == null) return;
        _vm.ViewDiaryCommand.Execute(item);
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.SelectedValue is string v)
            _vm.SetSortCommand.Execute(v);
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _vm.SearchCommand.Execute(null);
    }

    private void InsightScreenshot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Tag 传 base64 显示串；sender 可能是列表缩略 Image 或详情弹窗 Border
        if (sender is FrameworkElement { Tag: string url })
            _vm.PreviewScreenshotCommand.Execute(url);
    }

    private void EditOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsEditVisible = false;
    }

    private void DialogInner_Click(object sender, MouseButtonEventArgs e) { }

    private void DetailOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsDetailVisible = false;
    }

    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) _vm.IsImagePreviewVisible = false;
    }

    private void PreviewImage_Click(object sender, MouseButtonEventArgs e) => _vm.IsImagePreviewVisible = false;


    private void FormScreenshotRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string data)
            _vm.RemoveFormScreenshotByDataCommand.Execute(data);
    }

    // 编辑弹窗内 Ctrl+V 触发粘贴截图并 OCR 识别（对应 Electron 的 @paste 监听）
    private void EditDialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _vm.PasteAndRecognizeCommand.Execute(null);
            e.Handled = true;
        }
    }
}
