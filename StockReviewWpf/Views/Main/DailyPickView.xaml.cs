using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class DailyPickView : UserControl
{
    private readonly DailyPickViewModel _vm;

    public DailyPickView()
    {
        InitializeComponent();
        _vm = new DailyPickViewModel(
            App.RequireService<DatabaseService>(),
            App.RequireService<ImageService>(),
            App.RequireService<StockOcrService>(),
            App.RequireService<MarketDataAggregator>());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        // 注意：WPF 的 Unloaded 在每次导航离开（脱离可视化树）都会触发，并非仅视图被驱逐时。
        // 此处只复位瞬态 UI 状态，不做订阅退订——退订会让首次导航后 VM 事件永久失联。
        //（ActiveTab 复位到 daily：对应原版 tab 切换行为，切回时回主页签）
        Unloaded += (_, _) =>
        {
            _vm.IsDialogVisible = false;
            _vm.ImagePreviewVisible = false;
            if (_vm.ActiveTab != "daily")
                _vm.ActiveTab = "daily";
        };
        // 重新挂载（导航回来）时若期间有交易/强股写入（数据版本变化），
        // 硬刷新内嵌的汇总统计 WebView，避免"汇总统计"Tab 显示旧数据
        Loaded += (_, _) => TryRefreshEmbeddedSummary();
    }

    /// <summary>数据版本变化时硬刷新内嵌汇总统计页（首次挂载时 WebView 尚未就绪则跳过，加载时自会取到最新快照）。</summary>
    private void TryRefreshEmbeddedSummary()
    {
        if (SummaryWeb.IsWebViewReady &&
            SummaryWeb.CapturedDataVersion != DatabaseService.StatsDataVersion)
        {
            _ = SummaryWeb.ReloadHardAsync();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
    }

    // ============ 对话框 ============
    private void DialogOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == DialogOverlay)
            _vm.IsDialogVisible = false;
    }

    private void CancelDialog_Click(object sender, RoutedEventArgs e) => _vm.IsDialogVisible = false;

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

    // ============ 代码/名称回车自动回填 ============
    private void StockInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        _ = _vm.OnFormEnter();
    }

    // ============ 弹窗日期切换：自动回填该日行情 ============
    // forceUpdate=true：日期已变，价格/涨幅必须用新日期收盘价强制覆盖，不能沿用旧值
    private void FormPickDate_Changed(object sender, EventArgs e)
    {
        _ = _vm.AutoFetchStockData(forceUpdate: true);
    }

    // ============ 截图懒加载 ============
    // 卡片进入可视区（虚拟化面板实例化容器）或回收复用换绑记录时，按需读盘该卡截图
    private void CardImage_Loaded(object sender, RoutedEventArgs e) => RequestShot(sender);

    private void CardImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestShot(sender);

    private void RequestShot(object sender)
    {
        if (sender is FrameworkElement { DataContext: DailyPickRecord rec })
            _vm.RequestScreenshot(rec);
    }

    // ============ 图片预览 ============
    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e) => _vm.ImagePreviewVisible = false;

    // 汇总统计 Tab 已改为内嵌前端页面（WebChartView），ScottPlot 渲染逻辑全部移除
}
