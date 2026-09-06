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

public partial class DailyPickView : UserControl, ITrayScreenshotLifecycle
{
    private readonly DailyPickViewModel _vm;

    /// <summary>内嵌"汇总统计"WebView（懒创建：首次切到 summary Tab 才实例化，避免启动/预热期白拉一个完整 SPA）。</summary>
    private Views.Web.WebChartView? _summaryWeb;

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

    /// <summary>数据版本变化时硬刷新内嵌汇总统计页（未创建/未就绪则跳过：首次懒创建时自会取到最新快照）。</summary>
    private void TryRefreshEmbeddedSummary()
    {
        if (_summaryWeb is { IsWebViewReady: true } web &&
            web.CapturedDataVersion != DatabaseService.StatsDataVersion)
        {
            _ = web.ReloadHardAsync();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 首次切到"汇总统计"Tab 才创建 WebView（懒加载）：避免每日擒牛页一加载就拉起完整 SPA。
        // 创建后常驻 SummaryTabHost（Tab 切走时容器 Hidden 而非 Collapsed，WebView 不卸载）。
        if (e.PropertyName == nameof(DailyPickViewModel.ActiveTab) && _vm.ActiveTab == "summary")
            EnsureSummaryWeb();
    }

    /// <summary>懒创建内嵌汇总统计 WebView（幂等）。</summary>
    private void EnsureSummaryWeb()
    {
        if (_summaryWeb != null) return;
        var web = new Web.WebChartView("daily-pick") { AutoTab = "汇总统计" };
        _summaryWeb = web;
        SummaryTabHost.Children.Add(web);
    }

    /// <summary>内存治理（2026-09-06 v2）：主窗隐藏到托盘时释放内嵌汇总 WebView。
    /// WebView2 控件 Dispose 后不可复用，故整实例移除；用户再点"汇总统计"Tab 时
    /// EnsureSummaryWeb 会自然重建（懒加载语义不变）。</summary>
    public void ReleaseEmbeddedWeb()
    {
        try
        {
            if (_summaryWeb == null) return;
            SummaryTabHost.Children.Remove(_summaryWeb);
            _summaryWeb.Shutdown();
            _summaryWeb = null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[DailyPickView] 释放内嵌汇总 WebView 异常");
        }
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

    /// <summary>内存治理（2026-09-06 v2）：视图切走时清空截图字符串驻留（切回全树重新 Loaded → 自动重载）</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _vm.ClearTransientScreenshots();
    }

    private void RequestShot(object sender)
    {
        if (sender is FrameworkElement { DataContext: DailyPickRecord rec })
            _vm.RequestScreenshot(rec);
    }

    // ===== 托盘隐藏/恢复的截图驻留生命周期（2026-09-06 P1，接口 ITrayScreenshotLifecycle）=====
    /// <summary>主窗隐藏到托盘：清空全部截图字符串驻留（与 View_Unloaded 同路径）。</summary>
    public void ReleaseTransientScreenshots() => _vm.ClearTransientScreenshots();

    /// <summary>主窗恢复显示：对可视树中已 realize 卡片的 Image 重发懒加载请求（非卡片图自动跳过）。</summary>
    public void ReloadVisibleScreenshots()
    {
        foreach (var img in VisualTreeUtil.EnumerateImages(this))
            RequestShot(img);
    }

    // ============ 图片预览 ============
    private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e) => _vm.ImagePreviewVisible = false;

    // 汇总统计 Tab 已改为内嵌前端页面（WebChartView），ScottPlot 渲染逻辑全部移除
}
