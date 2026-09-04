using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StockReview.Core.Data;

namespace StockReviewWpf.Views.Web;

/// <summary>
/// 内嵌前端页面的 WebView2 宿主。
///
/// 生命周期与加载策略：
/// - Loaded 时才初始化（支持挂到隐藏 PreloadDock 预载）；
///   复用 App 启动时预热的共享 WebView2 环境（最多等 2 秒，超时按需创建）。
/// - 渐显时机 = "页面内容就绪"（JS 轮询 #app 有子节点后 postMessage 通知），
///   而非导航完成——白屏/空壳期用户不可见；8 秒超时兜底保证绝不隐身。
/// - 导航失败 / 浏览器进程崩溃 → 显示错误层 + 重试按钮（防呆与反馈）。
///
/// 注入脚本（AddScriptToExecuteOnDocumentCreatedAsync，文档创建前执行，早于 Vue 挂载）：
/// 1. 隐藏 splash / TitleBar / NavBar（HideChrome=true 时）
/// 2. 同步注入 window.electronAPI 兼容桥（全局名沿用旧版前端约定，wwwroot 产物按此名调用，禁止改名；DbHostObject 桥），保证前端页面首次 ipc() 即命中
/// 3. AutoTab 非空时自动点击指定文案的 el-radio-button 并隐藏页面自带 tab 栏
/// 所有脚本幂等（window.__wpfXxx 标志位），NavigationCompleted 后兜底重执行一次。
/// </summary>
public partial class WebChartView : UserControl
{
    private const string VirtualHost = "stock-review.local";
    private static readonly Uri BaseUri = new($"https://{VirtualHost}/index.html");

    /// <summary>JS 桥脚本（与实例无关）——静态缓存，避免每次导航重建 ~7KB 字符串</summary>
    private static readonly string ShimJs = BuildShimJs();
    /// <summary>数据探针脚本（静态，仅诊断用）</summary>
    private static readonly string ProbeJs = BuildProbeScript();
    /// <summary>就绪探针脚本（静态）</summary>
    private static readonly string ReadyProbeJs = BuildReadyProbeScript();

    /// <summary>渐显动画时长（毫秒），与原版页面入场动效一致</summary>
    private const int FadeInMs = 220;
    /// <summary>就绪探针超时兜底（毫秒）：超过后无论如何渐显，防止页面永久隐身</summary>
    private const int ReadyTimeoutMs = 8000;
    /// <summary>等待启动预热 WebView2 环境的上限（毫秒）</summary>
    private const int EnvWaitMs = 2000;

    /// <summary>内容已就绪（渐显完成）：MainWindow 用以判断导航入场是否跳过位移动画</summary>
    public bool IsContentReady { get; private set; }

    /// <summary>WebView2 已就绪（浏览器进程已连上，可执行 Reload/ExecuteScript）</summary>
    public bool IsWebViewReady => WebView.CoreWebView2 != null;

    /// <summary>
    /// 页面加载时快照的 DatabaseService.StatsDataVersion：
    /// MainViewModel 在导航回来时比对，若期间有交易/强股写入则自动硬刷新（统计页及时反映最新数据）。
    /// </summary>
    public long CapturedDataVersion { get; private set; } = -1;

    /// <summary>
    /// 硬刷新当前页面（标题栏"刷新"按钮 / 数据版本变更自动触发）：
    /// 清掉 HTTP 磁盘缓存后整页 Reload——SPA 重新挂载、所有数据经 electronAPI 桥重新查询，
    /// 等价于浏览器"不通过缓存的刷新"，不会复用页面内存中的旧数据。
    /// </summary>
    public async Task ReloadHardAsync()
    {
        var core = WebView.CoreWebView2;
        if (core == null) return;
        try
        {
            // 仅清磁盘缓存（静态资源重新从本地 wwwroot 取，代价极低）；
            // 不清 localStorage/IndexedDB，避免误伤页面自身偏好设置
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[WebChartView] 清理磁盘缓存失败（不阻塞刷新）({Route})", HashRoute);
        }
        CapturedDataVersion = DatabaseService.StatsDataVersion;
        _firstNavHandled = false;
        IsContentReady = false;
        Log.Information("[WebChartView] 硬刷新页面 ({Route})", HashRoute);
        core.Reload();
    }

    private readonly IDatabaseService _db;
    private WebBridge.DbHostObject? _hostObj;
    private bool _initialized;
    /// <summary>早期注入脚本（依赖实例属性，构建一次后缓存）</summary>
    private string? _earlyScript;
    /// <summary>首导航门控：探针/超时兜底只执行一次，后续子导航不重复跑（省 DB 查询与定时器）</summary>
    private bool _firstNavHandled;
    private string _targetUrl = string.Empty;

    #region 依赖属性
    public static readonly DependencyProperty HashRouteProperty =
        DependencyProperty.Register(nameof(HashRoute), typeof(string), typeof(WebChartView),
            new PropertyMetadata("statistics"));

    public static readonly DependencyProperty AutoTabProperty =
        DependencyProperty.Register(nameof(AutoTab), typeof(string), typeof(WebChartView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HideChromeProperty =
        DependencyProperty.Register(nameof(HideChrome), typeof(bool), typeof(WebChartView),
            new PropertyMetadata(true));

    /// <summary>hash 路由（不含 #），如 statistics / daily-pick</summary>
    public string HashRoute
    {
        get => (string)GetValue(HashRouteProperty);
        set => SetValue(HashRouteProperty, value);
    }

    /// <summary>要自动点击的 el-radio-button 文案（如"汇总统计"），空则不处理</summary>
    public string AutoTab
    {
        get => (string)GetValue(AutoTabProperty);
        set => SetValue(AutoTabProperty, value);
    }

    /// <summary>是否隐藏 页面的 TitleBar/NavBar（默认 true）</summary>
    public bool HideChrome
    {
        get => (bool)GetValue(HideChromeProperty);
        set => SetValue(HideChromeProperty, value);
    }
    #endregion

    public WebChartView()
    {
        _db = App.Host?.Services.GetRequiredService<DatabaseService>()
              ?? throw new InvalidOperationException("App.Host 尚未初始化，无法创建 WebChartView");
        InitializeComponent();
        // 首次内容就绪前隐藏（渐显入口见 FadeIn），消除白屏/加载过程可见期
        RootGrid.Opacity = 0;
    }

    public WebChartView(string hashRoute) : this() => HashRoute = hashRoute;

    // ============ 初始化与导航 ============

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _ = WebView.CoreWebView2 == null ? EnsureAndSetupAsync() : SetupAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 视图被 MainViewModel 缓存、切换导航时反复挂载/卸载：
        // 这里绝不能 Dispose WebView（否则切回来白屏），仅保持状态。
        // 真正的资源释放在 Shutdown()（应用退出时统一调用）。
    }

    /// <summary>初始化 WebView2：优先复用启动时预热的共享环境（浏览器进程已就绪）</summary>
    private async Task EnsureAndSetupAsync()
    {
        try
        {
            var env = App.SharedWebView2Environment;
            if (env == null)
            {
                for (var waited = 0; waited < EnvWaitMs && App.SharedWebView2Environment == null; waited += 100)
                    await Task.Delay(100);
                env = App.SharedWebView2Environment;
            }
            await WebView.EnsureCoreWebView2Async(env ?? await CoreWebView2Environment.CreateAsync());
            await SetupAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[WebChartView] WebView2 初始化失败 ({Route})", HashRoute);
            ShowError($"WebView2 初始化失败：{ex.Message}");
        }
    }

    /// <summary>配置 CoreWebView2（host object / 虚拟主机映射 / 事件 / 早期脚本）并导航</summary>
    private async Task SetupAsync()
    {
        try
        {
            var core = WebView.CoreWebView2!;

            // host object 桥：JS 通过 chrome.webview.hostObjects.__db 访问
            _hostObj = new WebBridge.DbHostObject(_db);
            core.AddHostObjectToScript("__db", _hostObj);

            // 虚拟主机映射：把输出目录 wwwroot 当作 https 域提供（同源、无 CORS）
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            if (!Directory.Exists(root))
            {
                Log.Error("[WebChartView] wwwroot 不存在: {Root}", root);
                ShowError($"静态资源目录缺失：{root}");
                return;
            }
            core.SetVirtualHostNameToFolderMapping(VirtualHost, root, CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;   // console 转发 + 就绪通知
            core.ProcessFailed += OnProcessFailed;             // 浏览器进程崩溃兜底

            // 关键：文档创建前（任何页面脚本运行前、Vue 挂载前）注入——构建一次后缓存
            _earlyScript ??= BuildEarlyScript();
            await core.AddScriptToExecuteOnDocumentCreatedAsync(_earlyScript);

            _targetUrl = new Uri(BaseUri, $"#{HashRoute}").ToString();
            // 快照当前数据版本：之后若用户在别的页面新增/修改交易，导航回本页时据此判断是否硬刷新
            CapturedDataVersion = DatabaseService.StatsDataVersion;
            Log.Information("[WebChartView] 加载 {Url} (AutoTab={AutoTab}, HideChrome={HideChrome})",
                _targetUrl, AutoTab, HideChrome);
            WebView.Source = new Uri(_targetUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[WebChartView] SetupAsync 失败 ({Route})", HashRoute);
            ShowError($"页面初始化失败：{ex.Message}");
        }
    }

    // ============ 导航完成 / 消息 / 进程失败 ============

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (!e.IsSuccess)
            {
                // 帧级失败（如 favicon）也走这里，需区分：仅主文档失败才提示重试
                var err = $"导航失败: {e.WebErrorStatus}";
                Log.Warning("[WebChartView] {Err} ({Route})", err, HashRoute);
                if (WebView.Source?.AbsoluteUri.StartsWith($"https://{VirtualHost}") == true)
                {
                    ShowError(err);
                    return;
                }
            }

            if (_firstNavHandled) return;
            _firstNavHandled = true;

            // 兜底：文档创建前脚本若因 DOM 未就绪失败，这里再执行一次（脚本自身幂等）
            await WebView.CoreWebView2!.ExecuteScriptAsync(_earlyScript);
            // 数据探针（仅首导航跑一次，结果经 console 转发进日志）
            await WebView.CoreWebView2!.ExecuteScriptAsync(ProbeJs);
            // 就绪探针：轮询 #app 出现真实内容后 postMessage（见 OnWebMessageReceived）
            await WebView.CoreWebView2!.ExecuteScriptAsync(ReadyProbeJs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[WebChartView] 注入脚本失败 ({Route})", HashRoute);
            FadeIn();
        }
        finally
        {
            // 超时兜底：ReadyTimeoutMs 内无论就绪探针是否回报都渐显，绝不停留隐身
            _ = RunTimeoutFallbackAsync();
        }
    }

    private async Task RunTimeoutFallbackAsync()
    {
        await Task.Delay(ReadyTimeoutMs);
        if (ErrorOverlay.Visibility != Visibility.Visible) FadeIn();
    }

    /// <summary>WebView2 事件均在 UI 线程触发，可直接操作 UI</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("__wpfConsole", out var lvl))
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                if (!string.IsNullOrEmpty(msg))
                    Log.Information("[WebChartView][console.{Level}] {Msg}", lvl.GetString(), msg);
            }
            // 页面内容就绪（Vue 挂载出真实内容）→ 渐显
            if (root.TryGetProperty("__wpfReady", out _))
            {
                Log.Information("[WebChartView] 页面内容就绪，渐显 ({Route})", HashRoute);
                FadeIn();
            }
        }
        catch (JsonException)
        {
            // 页面自有 postMessage（非 JSON 对象），忽略即可
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[WebChartView] WebMessage 处理异常");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error("[WebChartView] 浏览器进程异常: {Kind} ({Route})", e.ProcessFailedKind, HashRoute);
        ShowError($"内嵌浏览器进程异常退出（{e.ProcessFailedKind}），请重试");
    }

    // ============ UI 反馈 ============

    /// <summary>渐显内容（幂等；就绪通知/超时兜底/失败路径共用）</summary>
    private void FadeIn()
    {
        if (RootGrid.Opacity >= 1) { IsContentReady = true; return; }
        IsContentReady = true;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        RootGrid.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>显示错误层（用户可感知 + 可重试），并停止等待就绪</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
        FadeIn(); // 错误层也要可见
        Log.Error("[WebChartView] 显示错误层: {Msg}", message);
    }

    /// <summary>重试：隐藏错误层、重置渐隐状态、重新导航（已注册的文档级脚本对新文档依然生效）</summary>
    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_targetUrl)) return;
        Log.Information("[WebChartView] 用户触发重试 ({Route})", HashRoute);
        ErrorOverlay.Visibility = Visibility.Collapsed;
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 0;
        _firstNavHandled = false;
        IsContentReady = false;
        if (WebView.CoreWebView2 == null)
            _ = EnsureAndSetupAsync();
        else
            WebView.Source = new Uri(_targetUrl);
    }

    /// <summary>应用退出时统一释放：摘除 host object 并关闭 WebView（幂等，仅退出时调用）</summary>
    public void Shutdown()
    {
        try
        {
            if (WebView.CoreWebView2 == null) return;
            if (_hostObj != null)
                WebView.CoreWebView2.RemoveHostObjectFromScript("__db");
            ((IDisposable)WebView).Dispose();
            Log.Information("[WebChartView] 已释放 ({Route})", HashRoute);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[WebChartView] Shutdown 异常（可忽略）");
        }
    }

    // ============ 注入脚本构建 ============

    /// <summary>转义任意文本为可内嵌 JS 单引号字符串的字面量（防转义注入/语法破坏）</summary>
    private static string EscapeJs(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>需要隐藏的 chrome 元素 CSS 文本</summary>
    private string BuildChromeCss()
    {
        var css = "#splash{display:none!important}";
        if (HideChrome) css += ".title-bar,.navbar{display:none!important}";
        if (!string.IsNullOrEmpty(AutoTab)) css += ".tab-section{display:none!important}";
        return css;
    }

    /// <summary>AutoTab 轮询点击逻辑（幂等；150ms 轮询，上限 15 秒）</summary>
    private string BuildAutoTabJs()
    {
        if (string.IsNullOrEmpty(AutoTab)) return string.Empty;
        var target = EscapeJs(AutoTab);
        return "(function(){try{if(window.__wpfTab)return;window.__wpfTab=1;" +
               $"var target='{target}';" +
               "var tries=0;var timer=setInterval(function(){tries++;" +
               "if(document.querySelector('.statistics-content')){clearInterval(timer);return;}" +
               "var btns=document.querySelectorAll('.el-radio-button');" +
               "for(var i=0;i<btns.length;i++){if((btns[i].textContent||'').trim()===target){btns[i].click();break;}}" +
               "if(tries>100)clearInterval(timer);},150);}catch(e){console.error('[WPF-Embed] autotab:',e);}})();";
    }

    /// <summary>
    /// 文档创建前 + 导航完成后各执行一次的完整脚本（幂等）：
    /// 1) console 转发 2) 隐藏样式 3) electronAPI 桥 4) AutoTab 自动切换
    /// </summary>
    private string BuildEarlyScript()
    {
        var css = EscapeJs(BuildChromeCss());
        var autoTab = BuildAutoTabJs();
        return "(function(){try{" +
               // --- 0. console 转发（把页面 log/warn/error 与 window.onerror 发回本进程日志）---
               "if(!window.__wpfFwd){window.__wpfFwd=1;try{[" +
               "['log','info'],['warn','warn'],['error','error']].forEach(function(p){" +
               "var k=p[0],lv=p[1],orig=console[k]?console[k].bind(console):function(){};" +
               "console[k]=function(){try{chrome.webview.postMessage({__wpfConsole:lv," +
               "msg:Array.prototype.map.call(arguments,function(a){" +
               "try{return typeof a==='object'?JSON.stringify(a):String(a)}catch(e){return String(a)}}).join(' ').slice(0,500)});}" +
               "catch(e){}orig.apply(null,arguments);};});" +
               "window.addEventListener('error',function(ev){try{chrome.webview.postMessage({__wpfConsole:'error',msg:'JS异常: '+(ev.message||'')});}catch(e){}});" +
               "}catch(e){}}" +
               // --- 1. CSS（DOM 未就绪时挂 DOMContentLoaded 重试）---
               "if(!window.__wpfCss){window.__wpfCss=1;" +
               $"var s=document.createElement('style');s.textContent='{css}';" +
               "var m=function(){try{(document.head||document.documentElement).appendChild(s);}catch(e){}};" +
               "m();document.addEventListener('DOMContentLoaded',m);}" +
               // --- 2. electronAPI 桥（hostObjects 未就绪时留待兜底轮重试）---
               "if(!window.__wpfShim){try{var b=chrome.webview.hostObjects.__db;" +
               "if(b){" + ShimJs + "window.__wpfShim=1;}}catch(e){console.warn('[WPF-Embed] hostObject 未就绪，等待兜底注入');}}" +
               // --- 3. AutoTab ---
               autoTab +
               "}catch(e){console.error('[WPF-Embed] error:',e);}})();";
    }

    /// <summary>就绪探针：#app 渲染出子元素（Vue 挂载完成）或超时（8 秒）后回报</summary>
    private static string BuildReadyProbeScript() => @"
(function(){try{if(window.__wpfReadyProbe)return;window.__wpfReadyProbe=1;
var tries=0;var t=setInterval(function(){tries++;
var app=document.getElementById('app');
if((app&&app.children.length>0)||tries>80){clearInterval(t);
try{chrome.webview.postMessage({__wpfReady:1});}catch(e){}}},100);}catch(e){}})();";

    /// <summary>
    /// 数据探针：调用桥的关键接口并把结果发回本进程日志（诊断数据接入问题；仅首导航执行一次）。
    /// </summary>
    private static string BuildProbeScript() => @"
(async function(){
  function log(){try{chrome.webview.postMessage({__wpfConsole:'probe',msg:Array.prototype.map.call(arguments,function(a){
    try{return typeof a==='object'?JSON.stringify(a):String(a)}catch(e){return String(a)}}).join(' ')})}catch(e){}}
  try {
    if (!window.electronAPI) { log('[WPF-Probe] electronAPI 不存在！'); return; }
    if (!window.electronAPI.db) { log('[WPF-Probe] electronAPI.db 不存在！'); return; }
    log('[WPF-Probe] db 方法数:', Object.keys(window.electronAPI.db).length);
    var r1 = await window.electronAPI.db.count('trades').catch(function(e){return 'ERR:' + e});
    log('[WPF-Probe] trades count =', r1);
    var r2 = await window.electronAPI.db.count('dailyPicks').catch(function(e){return 'ERR:' + e});
    log('[WPF-Probe] dailyPicks count =', r2);
    var r3 = await window.electronAPI.db.getStatisticsSummary({}).catch(function(e){return 'ERR:' + e});
    log('[WPF-Probe] summary keys =', r3 && typeof r3 === 'object' ? Object.keys(r3).slice(0,10).join(',') : String(r3).slice(0,100));
  } catch (err) { log('[WPF-Probe] 异常:', String(err)); }
})();";

    /// <summary>window.electronAPI 兼容桥（名称沿用旧版前端约定，wwwroot 产物按此名调用，禁止改名；入参 b 为 hostObject 代理，所有方法返回 Promise）</summary>
    private static string BuildShimJs() => @"
(function(b){
  var S = (v) => v == null ? '' : (typeof v === 'object' ? JSON.stringify(v) : String(v));
  var P = (s) => { try { return JSON.parse(s); } catch { return s; } };
  window.electronAPI = {
    isElectron: true,
    db: {
      getAll: (t) => b.getAll(t).then(P),
      getById: (t, id) => b.getById(t, S(id)).then(P),
      add: (t, d) => b.add(t, JSON.stringify(d)).then(P),
      update: (t, id, d) => b.update(t, S(id), JSON.stringify(d)).then(P),
      delete: (t, id) => b.delete(t, S(id)).then(P),
      put: (t, d) => b.put(t, JSON.stringify(d)).then(P),
      bulkPut: (t, items) => b.bulkPut(t, JSON.stringify(items)).then(P),
      bulkAdd: (t, items) => b.bulkAdd(t, JSON.stringify(items)).then(P),
      clear: (t) => b.clear(t).then(P),
      count: (t) => b.count(t).then(r => parseInt(r || '0', 10)),
      whereEquals: (t, f, v) => b.whereEquals(t, f, S(v)).then(P),
      whereStartsWith: (t, f, v) => b.whereStartsWith(t, f, S(v)).then(P),
      whereAnyOf: (t, f, vals) => b.whereAnyOf(t, f, JSON.stringify(vals || [])).then(P),
      whereCompound: (t, cond) => b.whereCompound(t, JSON.stringify(cond || {})).then(P),
      whereBetween: (t, f, lo, hi) => b.whereBetween(t, f, S(lo), S(hi)).then(P),
      whereFirst: (t, f, v) => b.whereFirst(t, f, S(v)).then(P),
      whereCompoundFirst: (t, cond) => b.whereCompoundFirst(t, JSON.stringify(cond || {})).then(P),
      whereBetweenFirst: (t, f, lo, hi) => b.whereBetweenFirst(t, f, S(lo), S(hi)).then(P),
      orderBy: (t, f) => b.orderBy(t, f).then(P),
      orderByFirst: (t, f) => b.orderByFirst(t, f).then(P),
      orderByReverse: (t, f) => b.orderByReverse(t, f).then(P),
      orderByReverseFirst: (t, f) => b.orderByReverseFirst(t, f).then(P),
      orderByLimit: (t, f, n, rev) => b.orderByLimit(t, f, S(n), rev ? 'true' : 'false').then(P),
      getPage: (t, opts) => b.getPage(t, JSON.stringify(opts || {})).then(P),
      getStatisticsSummary: (opts) => b.getStatisticsSummary(opts ? JSON.stringify(opts) : null).then(P),
      getMonthlyWinRateStats: (m) => b.getMonthlyWinRateStats(S(m)).then(P),
      getTypeWinRateStats: () => b.getTypeWinRateStats().then(P),
      getTradeDistribution: () => b.getTradeDistribution().then(P),
      exportAll: () => b.exportAll().then(P),
      importAll: (d) => b.importAll(JSON.stringify(d)).then(P),
      deleteDatabase: () => b.deleteDatabase().then(P)
    },
    app: {
      getUserDataPath: () => Promise.resolve(''),
      getScreenshotsDir: () => Promise.resolve(''),
      getResourcesPath: () => Promise.resolve(''),
      deleteFile: () => Promise.resolve(true),
      triggerAutoSync: () => Promise.resolve(),
      getAutoStart: () => Promise.resolve(false),
      setAutoStart: () => Promise.resolve()
    },
    dataDir: {
      get: () => Promise.resolve(''),
      select: () => Promise.resolve(''),
      set: () => Promise.resolve()
    },
    http: {
      fetch: () => Promise.resolve(null),
      nodeFetch: () => Promise.resolve(null),
      browserFetch: () => Promise.resolve(null)
    },
    screenshot: {
      save: () => Promise.resolve(''),
      saveBatch: () => Promise.resolve([]),
      read: () => Promise.resolve(''),
      readBatch: () => Promise.resolve([]),
      delete: () => Promise.resolve(true),
      getStats: () => Promise.resolve({}),
      cleanup: () => Promise.resolve(0),
      cleanupOrphaned: () => Promise.resolve(0)
    },
    backup: {
      exportZip: () => Promise.resolve(''),
      autoBackup: () => Promise.resolve(''),
      importZip: () => Promise.resolve(false),
      importJson: () => Promise.resolve(false),
      importJsonFile: () => Promise.resolve(false),
      selectFile: () => Promise.resolve('')
    },
    cloudSync: {
      lazyInit: () => Promise.resolve(),
      testConnection: () => Promise.resolve(false),
      upload: () => Promise.resolve(false),
      download: () => Promise.resolve(false),
      list: () => Promise.resolve([]),
      delete: () => Promise.resolve(false),
      autoSync: () => Promise.resolve(),
      onProgress: () => () => {}
    },
    futu: {
      binary: () => Promise.resolve(null),
      checkOpenD: () => Promise.resolve({ alive: false }),
      startOpenD: () => Promise.resolve(false),
      subscribe: () => Promise.resolve(),
      unsubscribeAll: () => Promise.resolve(),
      subscribedCodes: () => Promise.resolve({ codes: [], alive: false }),
      getStartupOpendAlert: () => Promise.resolve(null),
      onPush: () => () => {},
      onOpendAlert: () => () => {}
    },
    chart: { openIntraday: () => Promise.resolve() },
    ocr: { baidu: () => Promise.resolve(null) },
    window: {
      minimize: () => Promise.resolve(),
      maximize: () => Promise.resolve(),
      close: () => Promise.resolve(),
      isMaximized: () => Promise.resolve(false)
    },
    pet: {
      openWindow: () => Promise.resolve(),
      closeWindow: () => Promise.resolve(),
      isOpen: () => Promise.resolve(false),
      dragStart: () => {},
      dragEnd: () => {},
      getBounds: () => Promise.resolve(null),
      setAlwaysOnTop: () => {},
      setRects: () => {},
      close: () => {},
      resizePanel: () => {},
      panelReady: () => {},
      setBubbleLayout: () => Promise.resolve(),
      setBaseSize: () => Promise.resolve(),
      onPositionSync: () => () => {},
      openPanelWindow: () => Promise.resolve(),
      showContextMenu: () => {},
      onMenuAction: () => () => {},
      getInsights: () => Promise.resolve([]),
      getDailyPicks: () => Promise.resolve([]),
      getSettings: () => Promise.resolve({}),
      saveSettings: () => Promise.resolve(),
      dbBackupGet: () => Promise.resolve(null),
      dbBackupSet: () => Promise.resolve(),
      listInstalledPets: () => Promise.resolve([]),
      installPet: () => Promise.resolve(false),
      uninstallPet: () => Promise.resolve(false),
      readPetMeta: () => Promise.resolve(null),
      saveLayout: () => Promise.resolve(false),
      getActivePet: () => Promise.resolve(''),
      setActivePet: () => Promise.resolve(false),
      getCatalog: () => Promise.resolve([]),
      getCursorScreenPoint: () => Promise.resolve({ x: 0, y: 0 }),
      getPetWindowScreenBounds: () => Promise.resolve(null),
      onActivePetUpdated: () => () => {},
      onSettingsUpdated: () => () => {},
      onAutoStartChanged: () => () => {},
      bubbleGetState: () => Promise.resolve(null),
      bubbleSaveState: () => Promise.resolve(),
      bubbleClaimLease: () => Promise.resolve(false),
      bubbleReleaseLease: () => Promise.resolve(),
      onBubbleQueueChanged: () => () => {}
    }
  };
  console.log('[WPF-Bridge] window.electronAPI 已注入, db methods:', Object.keys(window.electronAPI.db).length);
})(b);";
}


