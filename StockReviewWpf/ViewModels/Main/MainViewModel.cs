using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Pet;
using StockReviewWpf.Models;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 主窗口 ViewModel - 对应 Vue App.vue + 路由管理
/// 管理导航、窗口状态和宠物窗口控制
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ViewUsageService _viewUsage;

    [ObservableProperty]
    private string _appTitle = App.AppTitle;

    [ObservableProperty]
    private string _versionText = $"v{App.AppVersion}";

    [ObservableProperty]
    private string _statusText = "就绪";

    // 导航状态（对应 Vue Router 的路由切换）
    [ObservableProperty]
    private bool _isDailyPickView;

    [ObservableProperty]
    private bool _isInsightsView;

    [ObservableProperty]
    private bool _isStatisticsView;

    [ObservableProperty]
    private bool _isPatternOptimizeView;

    [ObservableProperty]
    private bool _isStrongStocksView;

    [ObservableProperty]
    private bool _isYearMonthView = true;

    [ObservableProperty]
    private bool _isCasesView;

    [ObservableProperty]
    private bool _isSettingsView;

    // 当前视图内容
    [ObservableProperty]
    private object? _currentView;

    // 跨视图跳转时携带的高亮日期（例如 Insights → YearMonth 定位某日）
    [ObservableProperty]
    private string _pendingHighlightDate = "";

    // 跨视图请求：跳转到交易记录页并自动打开写日记对话框（Insights 日记本入口）
    [ObservableProperty]
    private bool _pendingOpenDiary;

    // 跨视图请求：编辑指定日记（预填旧内容）。Id 对应 dailySummaries.id
    [ObservableProperty]
    private int? _pendingEditDiaryId;

    public MainViewModel(
        ViewUsageService viewUsage)
    {
        _viewUsage = viewUsage;

        // 注意：默认视图（YearMonthView 等）的创建延迟到 MainWindow.Loaded 之后，
        // 避免在主线程同步构造 MainViewModel 时即创建 YearMonthView 引发死锁。
        // 见 App.xaml.cs 中 Show() 后触发的 InitializeDefaultView。
    }

    /// <summary>
    /// 在 MainWindow 已显示、UI 线程空闲后调用，创建并切换到默认落地页（年月回顾）。
    /// 必须延后于 MainViewModel 构造，否则 YearMonthView 的创建会在 MainViewModel
    /// 同步构造期间发生，导致 UI 线程死锁（窗口永远不显示）。
    /// </summary>
    public void InitializeDefaultView()
    {
        NavigateToYearMonth();
        // 统计汇总 WebView 不在启动期预载：其加载会拉起浏览器渲染进程并解析整包前端 SPA
        //（vendor-core/ui/charts 等数 MB JS）+ 经 host-object 桥发起统计聚合查询，
        // 与启动高峰并发会抢占 CPU/磁盘/UI 线程。改为阶段 2 延迟预热
        //（启动 20s 后 ApplicationIdle，见 PreWarmStatisticsDelayedAsync）；
        // 用户更早点击则按需创建（共享环境已预热，仅 SPA 解析 1-2s 后渐显）。
    }

    // ===== 导航方法（对应 Vue Router 的路由） =====
    // 视图实例缓存：切换页面复用已创建的视图，避免每次重建 + 全量重载（切换卡顿主因）。
    // LRU 上限 = 导航页总数（8）：全部缓存，切换永远命中缓存（仅做内容替换 + 动画，零重建）。
    // 旧值 3 在 >3 个页面间轮换时每次都驱逐重建（UI 线程同步执行 XAML 实例化 +
    // VM 构造 + 数据加载 + WebView2 重初始化），并摧毁统计页的 WebView2 预载。
    // 内存控制不靠驱逐重建（牺牲流畅度），由各视图在 Unloaded 自行释放重型资源
    //（StatisticsView.ClearAllPlots 已是此模式）。
    private const int MaxCachedViews = 8;
    private readonly System.Collections.Generic.LinkedList<string> _viewLru = new();
    private readonly System.Collections.Generic.Dictionary<string, System.Windows.Controls.UserControl> _viewCache = new();

    /// <summary>应用退出时统一释放缓存的 WebView 视图（host object 摘除 + 关闭内嵌浏览器）</summary>
    public void DisposeWebViewCache()
    {
        foreach (var view in _viewCache.Values)
            (view as Views.Web.WebChartView)?.Shutdown();
        _viewCache.Clear();
    }

    /// <summary>
    /// 内存治理（2026-09-06 v2）：主窗隐藏到托盘时释放全部 WebView2 浏览器进程。
    /// 统计页/擒牛汇总页各带一个独立 msedgewebview2 进程（150-400M/个），托盘常驻期
    /// 完全无用却持续驻留——这是"关闭主程序界面后仍占 900M"的重要构成。
    /// WebView2 控件 Dispose 后不可复用，故统计页整实例替换（用户再点 Tab 时按需重建）；
    /// 擒牛内嵌页移除实例，点"汇总统计"Tab 时懒重建（原语义不变）。
    /// </summary>
    public void ReleaseWebViewsOnHide()
    {
        try
        {
            var mw = System.Windows.Application.Current?.MainWindow as Views.Main.MainWindow;

            if (_viewCache.TryGetValue("statistics", out var statView) && statView is Views.Web.WebChartView oldStat)
            {
                mw?.RemoveFromPreloadDock(oldStat);          // 停靠区挂着则先摘除（幂等）
                // 若该页正激活：保留 CurrentView 引用（窗口隐藏期无视觉影响，旧实例仅剩
                // 轻量壳），恢复显示时 RecoverWebViewsOnShow 会整实例重建替换
                _viewCache.Remove("statistics");
                _viewLru.Remove("statistics");
                oldStat.Shutdown();
            }

            if (_viewCache.TryGetValue("dailypick", out var dp) && dp is Views.Main.DailyPickView dpv)
                dpv.ReleaseEmbeddedWeb();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Services.MemoryProbe.LogSnapshot("主窗隐藏");
            Serilog.Log.Information("[内存] 主窗隐藏：WebView2 已全部释放（托管堆 {Mb:N0}MB）",
                GC.GetTotalMemory(false) / 1048576.0);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[内存] 主窗隐藏释放 WebView 异常（不影响托盘功能）");
        }
    }

    /// <summary>主窗从托盘恢复：重建"隐藏期被释放且当时正激活"的视图（目前仅统计页会被整实例清掉）。
    /// 其余视图缓存原样保留、内嵌 WebView 由用户点击 Tab 时懒重建。</summary>
    public void RecoverWebViewsOnShow()
    {
        try
        {
            if (_viewLru.First?.Value is string lastKey && !_viewCache.ContainsKey(lastKey)
                && _allViewFactories.TryGetValue(lastKey, out var factory))
            {
                Serilog.Log.Information("[内存] 主窗恢复：重建激活视图 {Key}", lastKey);
                SetCurrentView(GetCachedView(lastKey, factory));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[内存] 主窗恢复重建视图异常");
        }
    }

    private System.Windows.Controls.UserControl GetCachedView(string key, Func<System.Windows.Controls.UserControl> factory)
    {
        // 统一先清理再入队（幂等）：隐藏托盘期被释放的 statistics 键保留在 LRU 中，
        // 缓存 miss 重建时若不先 Remove 会产生重复键
        _viewLru.Remove(key);
        if (_viewCache.TryGetValue(key, out var v))
        {
            _viewLru.AddFirst(key);
            return v;
        }

        var view = factory();
        _viewCache[key] = view;
        _viewLru.AddFirst(key);

        while (_viewLru.Count > MaxCachedViews)
        {
            var evictKey = _viewLru.Last!.Value;
            _viewLru.RemoveLast();
            if (_viewCache.TryGetValue(evictKey, out var evicted))
            {
                _viewCache.Remove(evictKey);
                // 释放视图底层资源（ScottPlot Plot、WebView2 等）
                TryDisposeView(evicted);
            }
        }
        return view;
    }

    // ===== 首次导航卡顿：启动后空闲预热视图缓存（全量覆盖）=====
    // 首载卡顿根因：大 XAML 实例化（PatternOptimize/Insights 数百元素）+ VM 构造在 UI 线程
    // 同步执行，导航瞬间掉帧（实测 300-800ms，2026-09-03 日志）。缓存扩容后全部页面常驻 →
    // 在启动完成、UI 空闲时逐个提前创建进缓存（ApplicationIdle 优先级让输入/渲染先走，
    // 不干扰用户操作），之后所有"首次导航"实际都是缓存命中（零构建，仅内容替换 + 动画）。
    //
    // 两阶段预热（2026-09-05 修订）：此前 8 页全量预热把统计汇总 SPA 也在启动期
    // 拉起（数 MB JS + 浏览器渲染进程 + 桥接聚合查询），与落地页/宠物/更新检查并发，
    // 是"升级后启动/导航明显卡顿"的主因之一。现拆为：
    //   阶段1（启动空闲期）：7 个 WPF 原生视图（dailypick/insights 内嵌 WebView 已懒加载，
    //     构造期不拉浏览器进程），ApplicationIdle 分摊；
    //   （2026-09-06 v2）原阶段2 统计页 SPA 延迟预载已删（浏览器进程常驻不合算，见 InitializeDefaultView）。
    private static readonly System.Collections.Generic.Dictionary<string, Func<System.Windows.Controls.UserControl>> _allViewFactories = new()
    {
        ["dailypick"] = () => new Views.Main.DailyPickView(),
        ["insights"] = () => new Views.Main.InsightsView(),
        ["statistics"] = () => new Views.Web.WebChartView("statistics"),
        ["pattern"] = () => new Views.Main.PatternOptimizeView(),
        ["strong"] = () => new Views.Main.StrongStocksView(),
        ["yearmonth"] = () => new Views.Main.YearMonthView(),
        ["cases"] = () => new Views.Main.CasesView(),
        ["settings"] = () => new Views.Main.SettingsView(),
    };

    private static readonly System.Collections.Generic.HashSet<string> _lightKeys = new() { "pattern", "strong", "yearmonth", "cases", "settings" };

    /// <summary>
    /// 阶段 1 预热候选（启动空闲期）：7 个 WPF 原生视图（dailypick/insights 构造期
    /// 不再拉起 WebView——内嵌汇总页懒加载、编辑器在 Collapsed 弹窗内）。
    /// 轻量页在前、dailypick/insights 殿后，各自按使用得分降序（自适应未激活时保持默认顺序）。
    /// </summary>
    private System.Collections.Generic.List<(string key, Func<System.Windows.Controls.UserControl> factory)> ComputePrewarmSet()
    {
        var result = new System.Collections.Generic.List<(string, Func<System.Windows.Controls.UserControl>)>();

        System.Collections.Generic.IEnumerable<string> OrderByScore(
            System.Collections.Generic.IEnumerable<string> keys) =>
            !_viewUsage.IsAdaptiveActive
                ? keys
                : keys.OrderByDescending(k => _viewUsage.GetScore(k));

        foreach (var k in OrderByScore(_lightKeys))
            if (_allViewFactories.TryGetValue(k, out var f)) result.Add((k, f));
        // dailypick/insights 现为纯 WPF 构造（无启动期浏览器进程），排在轻量页之后
        foreach (var k in OrderByScore(new[] { "dailypick", "insights" }))
            if (_allViewFactories.TryGetValue(k, out var f)) result.Add((k, f));

        return result;
    }

    /// <summary>
    /// 空闲预热（两阶段）：主窗口 Loaded 后延迟调用（MainWindow）。
    /// 阶段 1（本方法）：逐个创建 7 个 WPF 原生视图并挂到隐藏停靠区
    /// （Hidden 保留布局槽）→ Loaded 事件触发（绑定评估、VM 异步加载启动）→ UpdateLayout
    /// 同步完成整个可视化树的 measure/arrange。
    /// 仅 new 不挂树是不够的：首次导航挂到内容区时仍要付全量布局+绑定渲染成本（实测仍卡顿的根因）。
    /// 之后所有"首次导航"实际只是把已布局完的视图从停靠区移到内容区——零构建零布局。
    /// 双重预算：页数达 MaxPrewarmPages、实际 UI 工作量达 MaxPrewarmWorkMs（仅计创建+布局，
    /// 不含空闲等待）或墙钟达 MaxPrewarmMs（异常兜底）先到先停。
    /// 每步之间 Dispatcher.Yield(ApplicationIdle) 让输入/渲染/用户导航优先。
    /// 阶段 2（<see cref="PreWarmStatisticsDelayedAsync"/>）：统计汇总 SPA 延迟 20s 后
    /// 在 ApplicationIdle 单独预载，避免数 MB JS 解析 + 浏览器进程 + 桥接聚合查询挤占启动高峰。
    /// </summary>
    public async Task PreWarmViewCache()
    {
        try
        {
            var mw = System.Windows.Application.Current?.MainWindow as Views.Main.MainWindow;
            if (mw == null) return;

            var prewarmSet = ComputePrewarmSet();
            var startMs = Environment.TickCount64;
            var prewarmed = 0;
            var workMs = 0L;

            foreach (var (key, factory) in prewarmSet)
            {
                // 三重预算先到先停（不破坏已预热视图）
                if (prewarmed >= ViewUsageService.MaxPrewarmPages) break;
                if (Environment.TickCount64 - startMs >= ViewUsageService.MaxPrewarmMs) break;
                if (workMs >= ViewUsageService.MaxPrewarmWorkMs) break;

                // 让出 UI 线程：空闲优先级，输入/渲染/导航事件先处理
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (_viewCache.ContainsKey(key)) continue; // 用户已抢先导航创建

                var itemStart = Environment.TickCount64;
                var view = GetCachedView(key, factory);
                if (mw.IsPreloadDocked(view)) continue; // 已在停靠区（异常情况）

                // 挂到隐藏停靠区：触发 Loaded（绑定评估 + VM 数据加载）
                mw.AddToPreloadDock(view);
                // 等 Loaded 处理器与绑定跑完（Loaded 优先级在渲染前）
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Loaded);
                // 同步强制完整布局（measure/arrange 数百元素在此完成，导航时零布局成本）
                try { view.UpdateLayout(); } catch { /* 布局异常不阻塞预热 */ }
                // 工作量预算只计真实耗时（创建+布局），空闲等待不挤占预算
                workMs += Environment.TickCount64 - itemStart;
                prewarmed++;
            }

            Serilog.Log.Information("[预热] 阶段1完成：{Count} 个 WPF 视图已入缓存（UI 工作量 {WorkMs}ms，墙钟 {WallMs}ms）",
                prewarmed, workMs, Environment.TickCount64 - startMs);

            // 预热完成后折叠停靠区：Hidden 的停靠区仍参与布局 pass，7 棵完整视图树
            // 会在每次 resize/布局变化时被无谓 measure/arrange；Collapsed 彻底退出布局，
            // Loaded 状态保持、后续摘除挂载行为不变。这是全量预热的"隐形成本"对冲。
            mw.CollapsePreloadDock();

            // 内存治理（2026-09-06 v2）：删除阶段2 统计页 SPA 后台预载。
            // 完整浏览器渲染进程 + 数 MB JS 常驻内存只换"点开快 1~2s"，用户主窗关闭后
            // 仍驻留（实测 900M 抱怨的主因之一）。现改为点击"统计汇总"时按需创建
            //（共享 WebView2 环境启动时已预热，SPA 有渐显就绪探针，体验可控）。
        }
        catch (Exception ex)
        {
            // 预热失败不影响功能（导航时按需创建）
            Serilog.Log.Warning(ex, "[预热] 异常终止");
        }
    }

    /// <summary>
    /// 释放被驱逐视图的底层资源。
    /// - IHeavyResourceView（StatisticsView 等）：统一驱逐清理钩子（清图表位图+退订）
    /// - WebView2（WebChartView）：必须显式 Shutdown，CoreWebView2 持有独立浏览器进程的
    ///   COM 引用，仅置 DataContext=null 等待 GC 不会及时释放。
    /// </summary>
    private static void TryDisposeView(System.Windows.Controls.UserControl view)
    {
        try
        {
            (view as Views.IHeavyResourceView)?.ReleaseHeavyResources();
            (view as Views.Web.WebChartView)?.Shutdown();

            // 通用清理：从可视化树移除（断开绑定链路，让 VM 可被 GC）
            view.DataContext = null;
        }
        catch { /* 清理失败不阻塞导航 */ }
    }

    partial void OnIsDailyPickViewChanged(bool value) { if (value) NavigateToDailyPick(); }
    partial void OnIsInsightsViewChanged(bool value) { if (value) NavigateToInsights(); }
    partial void OnIsStatisticsViewChanged(bool value) { if (value) NavigateToStatistics(); }
    partial void OnIsPatternOptimizeViewChanged(bool value) { if (value) NavigateToPatternOptimize(); }
    partial void OnIsStrongStocksViewChanged(bool value) { if (value) NavigateToStrongStocks(); }
    partial void OnIsYearMonthViewChanged(bool value) { if (value) NavigateToYearMonth(); }
    partial void OnIsCasesViewChanged(bool value) { if (value) NavigateToCases(); }
    partial void OnIsSettingsViewChanged(bool value) { if (value) NavigateToSettings(); }

    /// <summary>
    /// 设置当前导航视图：先从预载停靠区摘除（预热视图挂在那里；WPF 元素挂到内容区前必须脱离原父级），
    /// 再赋给 CurrentView（触发入场动画）。
    /// </summary>
    private void SetCurrentView(System.Windows.Controls.UserControl view)
    {
        if (System.Windows.Application.Current?.MainWindow is Views.Main.MainWindow mw)
            mw.RemoveFromPreloadDock(view);
        CurrentView = view;
    }

    private void NavigateToDailyPick()
    {
        _viewUsage.RecordNavigation("dailypick");
        SetCurrentView(GetCachedView("dailypick", () => new Views.Main.DailyPickView()));
        StatusText = "每日擒牛";
    }

    private void NavigateToInsights()
    {
        _viewUsage.RecordNavigation("insights");
        SetCurrentView(GetCachedView("insights", () => new Views.Main.InsightsView()));
        StatusText = "洞察分析";
    }

    private void NavigateToStatistics()
    {
        // 统计页走 WebView 预载：SetCurrentView 内统一摘除预载停靠区
        _viewUsage.RecordNavigation("statistics");
        var view = GetCachedView("statistics", () => new Views.Web.WebChartView("statistics"));
        // 页面加载后有交易/强股写入（版本号变化）→ 自动硬刷新，保证"当月分析"及时反映最新数据
        if (view is Views.Web.WebChartView web && web.IsWebViewReady &&
            web.CapturedDataVersion != StockReview.Core.Data.DatabaseService.StatsDataVersion)
        {
            _ = web.ReloadHardAsync();
        }
        SetCurrentView(view);
        StatusText = "统计分析";
    }

    private void NavigateToPatternOptimize()
    {
        _viewUsage.RecordNavigation("pattern");
        SetCurrentView(GetCachedView("pattern", () => new Views.Main.PatternOptimizeView()));
        StatusText = "形态优化";
    }

    private void NavigateToStrongStocks()
    {
        _viewUsage.RecordNavigation("strong");
        SetCurrentView(GetCachedView("strong", () => new Views.Main.StrongStocksView()));
        StatusText = "强势股池";
    }

    private void NavigateToYearMonth()
    {
        _viewUsage.RecordNavigation("yearmonth");
        SetCurrentView(GetCachedView("yearmonth", () => new Views.Main.YearMonthView()));
        StatusText = "年月回顾";
    }

    private void NavigateToCases()
    {
        _viewUsage.RecordNavigation("cases");
        SetCurrentView(GetCachedView("cases", () => new Views.Main.CasesView()));
        StatusText = "案例库";
    }

    private void NavigateToSettings()
    {
        _viewUsage.RecordNavigation("settings");
        SetCurrentView(GetCachedView("settings", () => new Views.Main.SettingsView()));
        StatusText = "设置";
    }

    /// <summary>
    /// 跨视图跳转：导航到「年月回顾」并高亮指定日期（用于 Insights/案例 关联跳转）。
    /// 设置 PendingHighlightDate 后由 YearMonthView 订阅 PropertyChanged 完成定位滚动。
    /// </summary>
    public void NavigateToYearMonthWithDate(string date)
    {
        PendingHighlightDate = date;
        IsDailyPickView = false;
        IsInsightsView = false;
        IsStatisticsView = false;
        IsPatternOptimizeView = false;
        IsStrongStocksView = false;
        IsCasesView = false;
        IsSettingsView = false;
        IsYearMonthView = true;
    }

    /// <summary>
    /// 请求跳转到交易记录页并打开写日记对话框（供 Insights 日记本入口调用）。
    /// 通过设置 PendingOpenDiary 标志，由 YearMonthView 在创建后自动打开日记弹窗。
    /// </summary>
    public void RequestOpenDiary()
    {
        NavigateToYearMonthWithDate(DateTime.Now.ToString("yyyy-MM-dd"));
        // 在导航完成后设置，确保 YearMonthView 的 PropertyChanged 订阅能收到通知
        PendingOpenDiary = true;
    }

    /// <summary>请求编辑指定日记：携带日记 Id 并跳转到其日期，由 YearMonthView 预填旧内容。</summary>
    public void RequestEditDiary(int id, string date)
    {
        PendingOpenDiary = false;
        NavigateToYearMonthWithDate(string.IsNullOrEmpty(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date);
        // 在导航完成后设置，确保 YearMonthView 的 PropertyChanged 订阅能收到通知
        PendingEditDiaryId = id;
    }

    /// <summary>
    /// 导航命令（对应点击顶部导航栏）。参数决定目标视图。
    /// 重置所有导航状态后再点亮目标，由对应的 OnIs*Changed 分部方法触发实际切换。
    /// </summary>
    [RelayCommand]
    private void Navigate(string view)
    {
        IsDailyPickView = false;
        IsInsightsView = false;
        IsStatisticsView = false;
        IsPatternOptimizeView = false;
        IsStrongStocksView = false;
        IsYearMonthView = false;
        IsCasesView = false;
        IsSettingsView = false;

        switch (view)
        {
            case "dailyPick": IsDailyPickView = true; break;
            case "insights": IsInsightsView = true; break;
            case "statistics": IsStatisticsView = true; break;
            case "patternOptimize": IsPatternOptimizeView = true; break;
            case "strongStocks": IsStrongStocksView = true; break;
            case "yearMonth": IsYearMonthView = true; break;
            case "cases": IsCasesView = true; break;
            case "settings": IsSettingsView = true; break;
        }
    }

    /// <summary>
    /// 刷新当前视图（对应 TitleBar 刷新按钮）——语义为"重新载入当前页面，不通过缓存"。
    /// - WebView2 视图（汇总统计等）：硬刷新 = 清磁盘缓存 + 整页 Reload，SPA 重挂载、数据全部重查；
    /// - 其余视图：重跑其 VM 的 ReloadCommand（视图实例缓存，导航命中缓存后数据不会自动重载）。
    /// </summary>
    [RelayCommand]
    private void RefreshCurrentView()
    {
        if (CurrentView is Views.Web.WebChartView web)
        {
            if (!web.IsWebViewReady) return;
            _ = web.ReloadHardAsync();
            StatusText = "已重新载入";
            return;
        }

        var vm = (CurrentView as System.Windows.FrameworkElement)?.DataContext;
        if (vm == null) return;
        var cmd = vm.GetType().GetProperty("ReloadCommand")?.GetValue(vm) as System.Windows.Input.ICommand;
        if (cmd != null && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            StatusText = "已刷新";
        }
        // 无 ReloadCommand 的视图（设置页等）无可刷新数据，保持现状
    }
}

