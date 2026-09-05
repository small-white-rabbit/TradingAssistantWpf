using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StockReviewWpf.Controls;

/// <summary>
/// wangeditor 富文本编辑器宿主（WebView2，对齐原版 RichTextEditor.vue）。
/// 内容统一为 HTML：编辑后的 HTML 经 <see cref="HtmlChanged"/> 回传宿主页面，
/// 由页面写入 VM；只读展示沿用 RichTextUtil.LoadInto（已兼容 HTML/RTF）。
/// </summary>
public partial class HtmlEditorControl : UserControl
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(HtmlEditorControl), new PropertyMetadata("请输入内容..."));

    /// <summary>编辑内容变化（HTML，空内容为空串）。</summary>
    public event EventHandler<string>? HtmlChanged;

    /// <summary>
    /// WebView2 内部滚轮转发（deltaY，向下为正）。
    /// WebView2 是独立 Win32 子窗口，滚轮消息不会路由回 WPF；编辑器内容已滚到边界
    /// （或本就无可滚动内容）时由页面 JS 转发到宿主，用于滚动外层弹窗的 ScrollViewer，
    /// 实现"光标在弹窗内任意位置都能滚动弹窗"。
    /// </summary>
    public event EventHandler<double>? WheelForwarded;

    /// <summary>
    /// 编辑器内容高度变化（px，含工具栏+编辑区）。editor.html 通过 ResizeObserver
    /// 把 body.scrollHeight 回传；宿主据此设置本控件 Height，实现"高度跟随内容自适应、
    /// 不在右侧加滚动条"。仅在内容驱动高度模式下触发。
    /// </summary>
    public event EventHandler<double>? ContentHeightChanged;

    private bool _ready;
    private string? _pendingHtml;

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>WebView2 实例（延迟创建，避免非编辑态常驻 ~80MB 运行时开销）。</summary>
    private Microsoft.Web.WebView2.Wpf.WebView2? _web;
    private Microsoft.Web.WebView2.Wpf.WebView2 Web => _web ??= new();

    public HtmlEditorControl()
    {
        InitializeComponent();
        Loaded += OnLoadedOnce;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 视图切走/弹窗关闭时释放 WebView2：单个 WebView2 运行时约 80-150MB，
        // 不释放会导致 InsightsView + YearMonthView 两个实例常驻。
        DisposeWebView();
    }

    /// <summary>释放 WebView2 并从 Host 移除，下次 Loaded 时延迟重建。</summary>
    private void DisposeWebView()
    {
        if (_web == null) return;
        try
        {
            // Close() 在 CoreWebView2 上不存在，用 NavigateToString("") + Dispose 替代
            _web.Dispose();
        }
        catch { /* 忽略关闭异常 */ }
        Host.Child = null;
        _web = null;
        _ready = false;
    }

    private async void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        // 每次加载时确保 WebView2 实例挂到 Host（Unloaded 会移除）
        if (Host.Child == null)
            Host.Child = Web;

        if (Web.CoreWebView2 != null) return; // 弹窗隐藏/重显不重复初始化
        try
        {
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent; // 透出外层圆角
            // 复用启动时预热的共享 WebView2 环境（与 WebChartView 一致），避免编辑器控件
            // 各自创建环境导致浏览器进程组/用户数据目录初始化分散；预热失败时为 null，
            // EnsureCoreWebView2Async(null) 退回按需创建，行为与旧版一致
            await Web.EnsureCoreWebView2Async(App.SharedWebView2Environment);
            if (Web.CoreWebView2 == null) return;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Editor", "editor.html");
            var uri = new UriBuilder(htmlPath)
            {
                Query = "placeholder=" + Uri.EscapeDataString(Placeholder ?? "")
            }.Uri;
            Web.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        catch
        {
            // WebView2 运行时缺失等异常：静默降级（页面保持空白边框，不阻断宿主弹窗）
        }
    }

    /// <summary>设置编辑内容（RTF 旧数据自动转 HTML）。</summary>
    public void SetHtml(string content)
    {
        var html = Services.RichTextUtil.ToHtml(content);
        if (!_ready)
        {
            _pendingHtml = html; // 页面未就绪先缓存，ready 后回放
            return;
        }
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.__setHtml({JsonSerializer.Serialize(html)})");
    }

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
            {
                _ready = true;
                if (_pendingHtml != null)
                {
                    var html = _pendingHtml;
                    _pendingHtml = null;
                    _ = Web.CoreWebView2.ExecuteScriptAsync(
                        $"window.__setHtml({JsonSerializer.Serialize(html)})");
                }
            }
            else if (type == "change")
            {
                var html = root.TryGetProperty("html", out var h) ? h.GetString() ?? "" : "";
                HtmlChanged?.Invoke(this, html);
            }
            else if (type == "wheel")
            {
                // 编辑器滚到边界后继续滚动 → 转发给宿主滚动外层弹窗
                var delta = root.TryGetProperty("deltaY", out var d) && d.TryGetDouble(out var dv) ? dv : 0;
                if (Math.Abs(delta) > 0.1)
                    WheelForwarded?.Invoke(this, delta);
            }
            else if (type == "height")
            {
                // 内容高度回传：宿主据此设置控件 Height（高度跟随内容自适应，不在右侧加滚动条）
                var h = root.TryGetProperty("height", out var hp) && hp.TryGetDouble(out var hv) ? hv : 0;
                if (h > 0)
                    ContentHeightChanged?.Invoke(this, h);
            }
        }
        catch
        {
            // 非预期消息体忽略
        }
    }
}
