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
    private readonly DatabaseService _dbService;
    private readonly PetService _petService;
    private readonly OpenDService _openDService;
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

    // 宠物窗口引用
    private Views.Pet.PetWindow? _petWindow;

    public MainViewModel(
        DatabaseService dbService,
        PetService petService,
        OpenDService openDService,
        ViewUsageService viewUsage)
    {
        _dbService = dbService;
        _petService = petService;
        _openDService = openDService;
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
        // 统计汇总 WebView 不再启动预载：其后台加载会拉起一个浏览器进程并解析整包前端 SPA
        //（vendor-core/ui/charts 等数 MB JS），与 DailyPickView 内嵌图表、InsightsView 富文本
        // 编辑器在启动 30s 内并发拉起 4+ 个 WebView2 实例，抢占 CPU/磁盘/内存，是启动卡顿主因。
        // 改为首次点击"统计汇总"时按需创建（共享环境已预热，首屏仅 1-3s 渐显，见 WebChartView.FadeIn）。
    }

    /// <summary>
    /// 切换宠物窗口显示（对应 启动宠物.bat 的功能）
    /// </summary>
    [RelayCommand]
    private void TogglePet()
    {
        if (_petWindow == null || !_petWindow.IsVisible)
        {
            _petWindow = new Views.Pet.PetWindow
            {
                DataContext = App.Host?.Services.GetRequiredService<PetViewModel>()
            };
            // 手动注入 PetService，确保调度器提醒能传递到窗口
            var petService = App.Host?.Services.GetRequiredService<PetService>();
            if (petService != null)
                _petWindow.SetPetService(petService);
            _petWindow.Show();
            StatusText = "宠物已启动";
        }
        else
        {
            _petWindow.Close();
            _petWindow = null;
            StatusText = "宠物已关闭";
        }
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

    private System.Windows.Controls.UserControl GetCachedView(string key, Func<System.Windows.Controls.UserControl> factory)
    {
        if (_viewCache.TryGetValue(key, out var v))
        {
            _viewLru.Remove(key);
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

    // ===== 首次导航卡顿：启动后空闲预热视图缓存（自适应）=====
    // 首载卡顿根因：大 XAML 实例化（PatternOptimize/Insights 数百元素）+ VM 构造在 UI 线程
    // 同步执行，导航瞬间掉帧。缓存扩容后全部页面常驻 → 在启动完成、UI 空闲时逐个提前
    // 创建进缓存（ApplicationIdle 优先级让输入/渲染先走，不干扰用户操作），
    // 之后所有"首次导航"实际都是缓存命中（零构建，仅内容替换 + 动画）。
    //
    // 自适应预热（方案 C 分层配额）：按 ViewUsageService 近因衰减得分排序候选页。
    // - WebView2 页（dailypick/insights/statistics）启动期并发拉浏览器进程是 30s 卡顿主因，
    //   仅在得分 ≥ WebView2Gate 时取分最高的 1 个串行预热（Tier1 可回收槽位）。
    // - 轻量页（pattern/strong/yearmonth/cases/settings）按得分降序补足，得分 < SkipThreshold 跳过。
    // - 冷启动（会话数 < ActivationSessions）回退到 5 个轻量页默认集合。
    // - 双重预算：MaxPrewarmPages 或 MaxPrewarmMs 先到先停，保总启动速度。
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

    private static readonly System.Collections.Generic.HashSet<string> _webview2Keys = new() { "dailypick", "insights", "statistics" };

    private static readonly System.Collections.Generic.HashSet<string> _lightKeys = new() { "pattern", "strong", "yearmonth", "cases", "settings" };

    /// <summary>
    /// 计算本次预热候选集合（按预热优先级排序）。Tier1 至多 1 个高分 WebView2 页；
    /// Tier2 为得分 ≥ SkipThreshold 的轻量页降序，合计不超过 MaxPrewarmPages。
    /// 冷启动（未达 ActivationSessions）回退到 5 个轻量页默认集合。
    /// </summary>
    private System.Collections.Generic.List<(string key, Func<System.Windows.Controls.UserControl> factory)> ComputePrewarmSet()
    {
        var result = new System.Collections.Generic.List<(string, Func<System.Windows.Controls.UserControl>)>();

        if (!_viewUsage.IsAdaptiveActive)
        {
            foreach (var k in _lightKeys)
                if (_allViewFactories.TryGetValue(k, out var f)) result.Add((k, f));
            return result;
        }

        string? tier1Key = null;
        var tier1Score = 0.0;
        foreach (var k in _webview2Keys)
        {
            var s = _viewUsage.GetScore(k);
            if (s >= ViewUsageService.WebView2Gate && s > tier1Score)
            {
                tier1Score = s;
                tier1Key = k;
            }
        }
        if (tier1Key != null && _allViewFactories.TryGetValue(tier1Key, out var tf))
            result.Add((tier1Key, tf));

        var lightOrdered = _lightKeys
            .Select(k => (key: k, score: _viewUsage.GetScore(k)))
            .Where(x => x.score >= ViewUsageService.SkipThreshold)
            .OrderByDescending(x => x.score);
        foreach (var item in lightOrdered)
        {
            if (result.Count >= ViewUsageService.MaxPrewarmPages) break;
            if (_allViewFactories.TryGetValue(item.key, out var f)) result.Add((item.key, f));
        }

        return result;
    }

    /// <summary>
    /// 空闲预热：主窗口 Loaded 后延迟调用（MainWindow）。逐个创建视图并挂到隐藏停靠区
    /// （Hidden 保留布局槽）→ Loaded 事件触发（绑定评估、VM 异步加载启动）→ UpdateLayout
    /// 同步完成整个可视化树的 measure/arrange。
    /// 仅 new 不挂树是不够的：首次导航挂到内容区时仍要付全量布局+绑定渲染成本（实测仍卡顿的根因）。
    /// 之后所有"首次导航"实际只是把已布局完的视图从停靠区移到内容区——零构建零布局。
    /// 双重预算：页数达 MaxPrewarmPages 或耗时达 MaxPrewarmMs 先到先停，保总启动速度。
    /// 每步之间 Dispatcher.Yield(ApplicationIdle) 让输入/渲染/用户导航优先。
    /// </summary>
    public async void PreWarmViewCache()
    {
        try
        {
            var mw = System.Windows.Application.Current?.MainWindow as Views.Main.MainWindow;
            if (mw == null) return;

            var prewarmSet = ComputePrewarmSet();
            var startMs = Environment.TickCount64;
            var prewarmed = 0;

            foreach (var (key, factory) in prewarmSet)
            {
                // 双重预算先到先停：页数或耗时超额即结束（不破坏已预热视图）
                if (prewarmed >= ViewUsageService.MaxPrewarmPages) break;
                if (Environment.TickCount64 - startMs >= ViewUsageService.MaxPrewarmMs) break;

                // 让出 UI 线程：空闲优先级，输入/渲染/导航事件先处理
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (_viewCache.ContainsKey(key)) continue; // 用户已抢先导航创建

                var view = GetCachedView(key, factory);
                if (mw.IsPreloadDocked(view)) continue; // 已在停靠区（异常情况）

                // 挂到隐藏停靠区：触发 Loaded（绑定评估 + VM 数据加载）
                mw.AddToPreloadDock(view);
                // 等 Loaded 处理器与绑定跑完（Loaded 优先级在渲染前）
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Loaded);
                // 同步强制完整布局（measure/arrange 数百元素在此完成，导航时零布局成本）
                try { view.UpdateLayout(); } catch { /* 布局异常不阻塞预热 */ }
                prewarmed++;
            }
        }
        catch { /* 预热失败不影响功能（导航时按需创建） */ }
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
        SetCurrentView(GetCachedView("statistics", () => new Views.Web.WebChartView("statistics")));
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
    /// 刷新当前视图（对应 TitleBar 刷新按钮）。
    /// 视图有缓存：NavigateToXxx 命中缓存后返回同一实例，数据不会重载（刷新无效果的根因）。
    /// 正确做法：在缓存的视图上重跑其 VM 的 ReloadCommand——复用现有实例，零新增内存占用。
    /// </summary>
    [RelayCommand]
    private void RefreshCurrentView()
    {
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

