using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class StrongStocksView : UserControl
{
    private readonly StrongStocksViewModel _vm;

    public StrongStocksView()
    {
        InitializeComponent();
        _vm = new StrongStocksViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>(),
            App.RequireService<StockOcrService>(),
            App.RequireService<MarketDataAggregator>());
        DataContext = _vm;
    }

    // ============ 弹窗背景点击关闭 ============
    private void Overlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        if (sender == AddDialogOverlay) _vm.IsAddDialogVisible = false;
        else if (sender == EditDialogOverlay) _vm.IsEditDialogVisible = false;
        else if (sender == LinkDialogOverlay) _vm.IsLinkDialogVisible = false;
        else if (sender == ViewDialogOverlay) _vm.IsViewDialogVisible = false;
    }

    private void CloseAddDialog_Click(object sender, RoutedEventArgs e) => _vm.IsAddDialogVisible = false;
    private void CloseEditDialog_Click(object sender, RoutedEventArgs e) => _vm.IsEditDialogVisible = false;
    private void CloseLinkDialog_Click(object sender, RoutedEventArgs e) => _vm.IsLinkDialogVisible = false;
    private void CloseViewDialog_Click(object sender, RoutedEventArgs e) => _vm.IsViewDialogVisible = false;
    private void CloseImagePreview_Click(object sender, RoutedEventArgs e) => _vm.IsImagePreviewVisible = false;
    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e) => _vm.IsImagePreviewVisible = false;

    // ============ 截图懒加载 ============
    // 卡片进入可视区（虚拟化面板实例化容器）或回收复用换绑记录时，按需读盘该卡截图
    private void CardImage_Loaded(object sender, RoutedEventArgs e) => RequestShot(sender);

    /// <summary>内存治理（2026-09-06 v2）：视图切走时清空截图字符串驻留（切回全树重新 Loaded → 自动重载）</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _vm.ClearTransientScreenshots();
    }

    private void CardImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestShot(sender);

    private void RequestShot(object sender)
    {
        if (sender is FrameworkElement { DataContext: StockReviewWpf.ViewModels.StrongStockItem rec })
            _vm.RequestScreenshot(rec);
    }

    // ============ 截图 ============
    private void ScreenshotArea_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "选择截图"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var bytes = File.ReadAllBytes(dlg.FileName);
                var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".bmp" => "image/bmp",
                    ".gif" => "image/gif",
                    _ => "image/png"
                };
                var base64 = $"data:{mime};base64," + Convert.ToBase64String(bytes);
                _vm.AttachScreenshotFromBase64(base64);
                _ = _vm.RecognizeAndFill(base64);
            }
            catch (Exception ex)
            {
                _vm.StatusText = "读取图片失败: " + ex.Message;
            }
        }
    }

    private void PasteScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Clipboard.ContainsImage())
        {
            var bmp = System.Windows.Clipboard.GetImage();
            if (bmp != null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var base64 = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                _vm.AttachScreenshotFromBase64(base64);
                _ = _vm.RecognizeAndFill(base64);
                return;
            }
        }
        _vm.StatusText = "剪贴板中没有图片";
    }

    // ============ 代码/名称回车回填 ============
    private void StockInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        _ = _vm.OnFormEnter();
    }

    // ============ 日期变更回填 ============
    private void FormDate_Changed(object sender, EventArgs e)
    {
        _ = _vm.OnFormDateChanged();
    }
}
