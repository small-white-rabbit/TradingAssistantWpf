using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.MarketData;

namespace StockReview.Core.Services;

public partial class PlanSchedulerService : IHostedService
{
    // ===== 依赖注入 =====
    private readonly DatabaseService _db;
    private readonly MarketDataAggregator _marketData;
    private readonly Futu.FutuAdapter? _futuAdapter;
    private readonly IPetStore _petStore;
    private readonly ITradePlanStore _tradePlanStore;
    private readonly IPetSettingsStore _settingsStore;
    private readonly ICustomRemindersStore _customRemindersStore;
    private readonly ISellPointDetector _sellPointDetector;
    private readonly IBuyPointDetector _buyPointDetector;
    private readonly ISignalEventStore _signalEventStore;
    private readonly IMultiFactorEngine _multiFactorEngine;
    private readonly IMarketTimeService _marketTime;

    // ===== 配置 =====
    public MonitorConfig Config { get; } = new();

    /// <summary>卖点信号类型集合 - 对应 SELL_SIGNAL_TYPES</summary>
    private static readonly HashSet<string> SellSignalTypes = new()
    {
        "surge_pullback", "volume_stagnant", "break_ma5", "break_ma10",
        "break_ma30", "break_support", "intraday_divergence",
        "vwap_breakdown", "large_order_outflow", "pattern_similarity",
        "surge_angle", "kline_pattern", "intraday_pattern", "ma_pressure"
    };

    /// <summary>形态相似度信号类型 - 对应 PATTERN_SIMILARITY_TYPES</summary>
    private static readonly HashSet<string> PatternSimilarityTypes = new()
    {
        "pattern_similarity"
    };

    /// <summary>默认因子权重</summary>
    private static readonly Dictionary<string, decimal> DefaultFactorWeights = new()
    {
        ["surge_angle"] = 0.15m,
        ["volume_stagnant"] = 0.20m,
        ["ma_pressure"] = 0.20m,
        ["kline_pattern"] = 0.15m,
        ["intraday_pattern"] = 0.10m,
        ["vwap_breakdown"] = 0.10m,
        ["large_order_outflow"] = 0.05m,
        ["pattern_similarity"] = 0.05m
    };

    // ===== 状态字段 =====
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private bool _running;
    private DateTime _lastTickTime;

    /// <summary>信号状态缓存（去重）- key: "planId:sigType"</summary>
    private readonly ConcurrentDictionary<string, SignalStateEntry> _signalStates = new();

    /// <summary>限频器 - key: "stockCode:type"</summary>
    private readonly ConcurrentDictionary<string, RateLimitRecord> _rateLimiter = new();

    /// <summary>波内限发状态 - key: stockCode</summary>
    private readonly ConcurrentDictionary<string, WaveGateState> _waveGateStates = new();

    /// <summary>快照缓存 - key: stockCode</summary>
    private readonly ConcurrentDictionary<string, List<PriceSnapshot>> _snapshotCache = new();

    /// <summary>秒级价格轨迹 - key: stockCode（时间升序，仅保留最近约16分钟，供时间窗口快速涨跌检测）</summary>
    private readonly ConcurrentDictionary<string, List<LiveTrailPoint>> _liveTrail = new();

    /// <summary>快照内存缓冲（批量落地）- key: stockCode</summary>
    private readonly ConcurrentDictionary<string, List<PriceSnapshot>> _snapshotBuffer = new();

    /// <summary>日K线缓存 (TTL: 当日)</summary>
    private readonly ConcurrentDictionary<string, (List<KLineData> Data, DateTime ExpiresAt)> _dailyKlineCache = new();

    /// <summary>资金流向缓存 (TTL: 5分钟)</summary>
    private readonly ConcurrentDictionary<string, (object? Data, DateTime ExpiresAt)> _capitalFlowCache = new();

    /// <summary>批量行情缓存 (TTL: 由 Settings.RefreshIntervalMs 决定，3/5/10 秒三挡)</summary>
    private readonly ConcurrentDictionary<string, (StockQuote Data, DateTime ExpiresAt)> _batchQuoteCache = new();

    /// <summary>已提醒的目标价级别 - key: "planId:level" (当日去重)</summary>
    private readonly ConcurrentDictionary<string, bool> _levelHitNotified = new();

    /// <summary>当日已触发动作型提醒 - key: "planId:actionType" (当日一次)</summary>
    private readonly ConcurrentDictionary<string, bool> _actionEmittedToday = new();

    /// <summary>盘后提醒已通知状态</summary>
    private AfterMarketNotifiedState _afterMarketNotified = new();

    /// <summary>盘后 snooze 截止时间</summary>
    private long _afterMarketSnoozeUntil;

    /// <summary>盘后上次提醒时间</summary>
    private long _afterMarketLastReminder;

    /// <summary>盘前提醒日期</summary>
    private string _lastPreMarketReminderDate = "";

    /// <summary>非交易日提醒日期</summary>
    private string _lastNonTradingDayReminderDate = "";
    /// <summary>上次非交易日心情设置日期（防止每秒重复触发 SetMood）</summary>
    private string _lastNonTradingDayMoodSetDate = "";

    /// <summary>盘前 MA5 检查状态</summary>
    private PreCloseMA5State _preCloseMA5State = new();

    /// <summary>当前时间状态</summary>
    private TimeStatus _currentTimeStatus = TimeStatus.NonWorking;

    /// <summary>上次快照记录时间</summary>
    private DateTime _lastSnapshotTime;

    /// <summary>上次快照落地时间</summary>
    private DateTime _lastSnapshotFlushTime;

    /// <summary>自定义提醒上次检查时间</summary>
    private DateTime _lastCustomReminderCheck;

    /// <summary>上次 idle insight 时间</summary>
    private DateTime _lastIdleInsightTime;

    /// <summary>上次回放补全日期</summary>
    private string _lastBackfillDate = "";

    /// <summary>今日已评估信号标志</summary>
    private string _lastEvaluateDate = "";

    /// <summary>今日已自进化标志</summary>
    private string _lastAutoOptimizeDate = "";

    /// <summary>当前日期（用于跨天检测）</summary>
    private string _currentDate = "";

    /// <summary>推送驱动检测的防重入标记（按股票，对齐 Electron _pushDetectRunning）</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pushDetectRunning = new();

    /// <summary>检测执行期间到达的新推送标记（trailing 补跑，对齐 Electron _pushDetectQueued）</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pushDetectQueued = new();

    /// <summary>富途订阅状态（订阅成功后置位；断线/订阅失败时复位以触发重订）</summary>
    private bool _futuSubscribed;

    /// <summary>推送/连接事件是否已绑定（防止重订时重复挂事件处理器）</summary>
    private bool _futuHandlerBound;

    /// <summary>上次富途订阅尝试时间</summary>
    private DateTime _lastFutuSubscribeAttempt;

    /// <summary>富途订阅重试计数</summary>
    private int _futuSubscribeRetryCount;

    /// <summary>优化参数（从 localStorage/DB 加载）</summary>
    private readonly Dictionary<string, object> _optimizedParams = new();

    // ===== 中国时区 =====
    private static readonly TimeZoneInfo ChinaTz = CnTimeZone.Get;

    /// <summary>
    /// 获取当前东八区时间
    /// </summary>
    private DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ChinaTz);

    /// <summary>Unix 时间戳（毫秒）</summary>

    /// <summary>Unix 时间戳（毫秒）</summary>
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ===== 构造函数 =====

    public PlanSchedulerService(
        DatabaseService db,
        MarketDataAggregator marketData,
        Futu.FutuAdapter? futuAdapter,
        IPetStore petStore,
        ITradePlanStore tradePlanStore,
        IPetSettingsStore settingsStore,
        ICustomRemindersStore customRemindersStore,
        ISellPointDetector sellPointDetector,
        IBuyPointDetector buyPointDetector,
        ISignalEventStore signalEventStore,
        IMultiFactorEngine multiFactorEngine,
        IMarketTimeService marketTime)
    {
        _db = db;
        _marketData = marketData;
        _futuAdapter = futuAdapter;
        _petStore = petStore;
        _tradePlanStore = tradePlanStore;
        _settingsStore = settingsStore;
        _customRemindersStore = customRemindersStore;
        _sellPointDetector = sellPointDetector;
        _buyPointDetector = buyPointDetector;
        _signalEventStore = signalEventStore;
        _multiFactorEngine = multiFactorEngine;
        _marketTime = marketTime;
    }

    // ============================================================================
    // IHostedService 实现
    // ============================================================================
    // IHostedService 实现
    // ============================================================================

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        Log.Information("[计划调度] 服务启动");

        // 确保快照表存在
        EnsureSnapshotTable();

        // 加载持久化参数
        LoadAutoOptimizedParams();
        LoadOptimizedParams();

        // 启动主循环（1秒间隔，对应 JS 的 setInterval(tick, 1000)）
        _ = RunTickLoop(_cts.Token);

        return Task.CompletedTask;
    }


    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        _cts?.Cancel();

        // 落地剩余快照
        await FlushSnapshotsAsync();

        // 清理富途订阅
        CleanupFutuSubscriptionAfterClose();

        Log.Information("[计划调度] 服务停止");
        await Task.CompletedTask;
    }

    // ============================================================================
    // 主循环 tick() - 对应 planScheduler.js tick()
    // ============================================================================

    /// <summary>
    /// 主循环 - 每 1 秒执行一次 tick
    /// 子任务异常隔离：每个子任务独立 try-catch，单个失败不影响其他
    /// </summary>

    // ============================================================================
    // 主循环 tick() - 对应 planScheduler.js tick()
    // ============================================================================

    /// <summary>
    /// 主循环 - 每 1 秒执行一次 tick
    /// 子任务异常隔离：每个子任务独立 try-catch，单个失败不影响其他
    /// </summary>
    private async Task RunTickLoop(CancellationToken token)
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (_running && !token.IsCancellationRequested)
        {
            try
            {
                await Tick();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[计划调度] tick 异常");
            }

            try
            {
                await _timer.WaitForNextTickAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 主调度 tick - 对应 planScheduler.js tick()
    /// 所有子任务通过 RunTask 包装实现异常隔离
    /// </summary>
    private async Task Tick()
    {
        var now = Now;
        _lastTickTime = now;

        // 跨天检测
        var todayStr = _marketTime.FormatDate(now);
        if (_currentDate != todayStr)
        {
            _currentDate = todayStr;
            OnDayChanged();
        }

        // 子任务异常隔离
        await RunTask("handleTimeStatus", () => HandleTimeStatusAsync(now));
        await RunTask("checkCustomReminders", () => CheckCustomRemindersAsync(now));
        await RunTask("cleanRateLimit", () => { CleanRateLimit(); return Task.CompletedTask; });
        await RunTask("flushSnapshots", () => FlushSnapshotsAsync());
        await RunTask("cleanupExpiredCaches", () => { CleanupExpiredCaches(); return Task.CompletedTask; });
    }

    /// <summary>
    /// 子任务异常隔离包装 - 对应 planScheduler.js 的 runTask 模式
    /// </summary>
    private async Task RunTask(string name, Func<Task> task)
    {
        try
        {
            await task();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 子任务 {Name} 异常", name);
        }
    }

    /// <summary>
    /// 跨天回调 - 重置当日状态
    /// </summary>
    private void OnDayChanged()
    {
        _signalStates.Clear();
        _rateLimiter.Clear();
        _waveGateStates.Clear();
        _liveTrail.Clear();
        _levelHitNotified.Clear();
        _actionEmittedToday.Clear();
        _preCloseMA5State = new PreCloseMA5State();
        _afterMarketNotified = new AfterMarketNotifiedState();
        _afterMarketSnoozeUntil = 0;
        _afterMarketLastReminder = 0;
        _lastBackfillDate = "";
        _lastEvaluateDate = "";
        _lastAutoOptimizeDate = "";
        _dailyKlineCache.Clear();

        Log.Information("[计划调度] 跨天重置: {Date}", _currentDate);
    }

    // ============================================================================
    // 时间状态处理 - 对应 planScheduler.js handleTradingTime / handlePreMarket / handleAfterMarket 等
    // ============================================================================

    /// <summary>
    /// 时间状态调度入口 - 判断当前时段并路由到对应处理器
    /// </summary>

    // ============================================================================
    // 时间状态处理 - 对应 planScheduler.js handleTradingTime / handlePreMarket / handleAfterMarket 等
    // ============================================================================

    /// <summary>
    /// 时间状态调度入口 - 判断当前时段并路由到对应处理器
    /// </summary>
    private async Task HandleTimeStatusAsync(DateTime now)
    {
        // 非交易日
        if (!_marketTime.IsTradingDay(now))
        {
            if (_currentTimeStatus != TimeStatus.NonTradingDay)
            {
                _currentTimeStatus = TimeStatus.NonTradingDay;
                Log.Information("[计划调度] 进入非交易日模式");
            }
            await HandleNonTradingDayAsync();
            return;
        }

        var hours = _marketTime.GetHours(now);

        // 非工作时段 (20:00 - 次日 8:00)
        if (hours >= 20 || hours < 8)
        {
            if (_currentTimeStatus != TimeStatus.NonWorking)
            {
                _currentTimeStatus = TimeStatus.NonWorking;
                Log.Information("[计划调度] 进入非工作时段");
            }
            await HandleNonWorkingTimeAsync();
            return;
        }

        // 盘前 (8:00 - 9:30)
        if (hours < 9.5m)
        {
            if (_currentTimeStatus != TimeStatus.PreMarket)
            {
                _currentTimeStatus = TimeStatus.PreMarket;
                Log.Information("[计划调度] 进入盘前模式");
                // 冷启动回放补全
                _ = BackfillTodayEventsAsync();
            }
            await HandlePreMarketAsync();
            return;
        }

        // 交易时段 (9:30 - 15:00)
        if (hours >= 9.5m && hours < 15)
        {
            if (_currentTimeStatus != TimeStatus.Trading)
            {
                _currentTimeStatus = TimeStatus.Trading;
                Log.Information("[计划调度] 进入交易模式");
                // 确保富途订阅
                _ = EnsureFutuSubscriptionAsync();
            }
            await HandleTradingTimeAsync();
            return;
        }

        // 盘后 (15:00 - 20:00)
        if (_currentTimeStatus != TimeStatus.AfterMarket)
        {
            _currentTimeStatus = TimeStatus.AfterMarket;
            Log.Information("[计划调度] 进入盘后模式");
            // 盘后处理
            _ = HandleAfterMarketAsync();
            // 清理富途订阅
            CleanupFutuSubscriptionAfterClose();
            // 盘后信号评估 + 自进化：串行管线（评估→漏报分析→优化）。
            // 修复前两个 fire-and-forget 并发启动，而 AutoOptimize 内部又调评估——
            // 竞态窗口内（双方都未设 _lastEvaluateDate）会导致全天事件被并发评估两次
            _ = RunAfterMarketEvolutionAsync();
        }
        await HandleAfterMarketAsync();
    }

    /// <summary>盘后进化管线：先评估（含漏报复盘），再自进化（内部评估因当日去重直接跳过）</summary>

    /// <summary>盘后进化管线：先评估（含漏报复盘），再自进化（内部评估因当日去重直接跳过）</summary>
    private async Task RunAfterMarketEvolutionAsync()
    {
        await EvaluateTodaySignalsAsync();
        await AutoOptimizeParamsAsync();
    }

    /// <summary>
    /// 交易时段处理 - 对应 planScheduler.js handleTradingTime
    /// </summary>
    private async Task HandleTradingTimeAsync()
    {
        var now = Now;
        var (phase, _) = _marketTime.GetIntradayPhase(now);

        // 每 tick 保活富途订阅（幂等：全部覆盖时立即返回；
        // 断线自愈重连 + 盘中新添计划增量补订，对齐 Electron handleTradingTime 内调用 _ensureFutuSubscription）
        _ = EnsureFutuSubscriptionAsync();

        // 午休时段跳过
        if (phase == IntradayPhase.Lunch)
        {
            return;
        }

        // 收盘集合竞价时段也跳过（价格已基本定格）
        if (phase == IntradayPhase.CloseAuction)
        {
            return;
        }

        // 获取今日计划 + 持仓过夜计划（含备份导入的旧日期计划，对齐 Electron getMonitoringPlans）
        var todayPlans = _tradePlanStore.TodayPlans;
        var yesterdayPlans = _tradePlanStore.MonitoringPlans;

        // 合并可监控计划
        var monitorablePlans = todayPlans
            .Concat(yesterdayPlans)
            .Where(IsPlanMonitorable)
            .ToList();

        if (monitorablePlans.Count == 0)
        {
            // 空闲时显示随机心得
            await ShowIdleInsightAsync();
            return;
        }

        // 获取唯一股票代码
        var stockCodes = monitorablePlans
            .Select(p => p.StockCode)
            .Distinct()
            .ToList();

        // 批量获取行情
        var dataMap = await FetchBatchDataWithCache(stockCodes);

        // 遍历每个计划检查信号
        foreach (var plan in monitorablePlans)
        {
            if (!dataMap.TryGetValue(plan.StockCode, out var data) || data == null)
                continue;

            // 行情请求期间计划可能已被执行
            if (!IsPlanMonitorable(plan))
                continue;

            // checkPlanSignals: 全量信号检查（快速涨跌/封板/目标价/止损价/卖点/买点）
            await CheckPlanSignals(plan, data);

            // checkTodayPlan: 今日计划盘中监控（与 checkPlanSignals 共用 N1 去重）
            await CheckTodayPlan(plan, data);
        }

        // 快照记录
        await RecordSnapshotsAsync(dataMap);

        // 尾盘 MA5 检查（14:30-15:00 每 5 分钟）
        await CheckPreCloseMA5Async();
    }

    /// <summary>
    /// 盘前处理 - 对应 planScheduler.js handlePreMarket
    /// </summary>
    private async Task HandlePreMarketAsync()
    {
        var now = Now;
        var hours = _marketTime.GetHours(now);

        // 睡觉时段（20:00 - 次日 8:00）不推送
        if (hours >= 20 || hours < 8)
        {
            return;
        }

        var todayPlans = _tradePlanStore.TodayPlans;

        // 启动时或跨天推送一次今日计划
        if (todayPlans.Count > 0 && ShouldShowPreMarketReminder())
        {
            var planText = string.Join("\n", todayPlans.Select(p =>
                $"  {p.StockName}({p.StockCode}) {PlanTypeText(p.PlanType)} @ {p.TargetPrice}"));

            _petStore.AddReminder(new ReminderRequest
            {
                Type = "trade",
                Level = ReminderLevel.Hint,
                Title = "今日交易计划",
                Content = $"今天您有 {todayPlans.Count} 条交易计划：\n{planText}",
                Importance = 3
            });
            _lastPreMarketReminderDate = _marketTime.FormatDate(now);
            try
            {
                using var conn = _db.CreateConnection();
                SaveConfig(conn, "pet_last_pre_market_reminder_date", _lastPreMarketReminderDate);
            }
            catch { /* 持久化失败不影响本次已推送的提醒 */ }
        }

        // 9:25-9:30 集合竞价时段检测低开/高开
        if (hours >= 9 + 25m / 60m && hours < 9.5m)
        {
            var yesterdayPlans = _tradePlanStore.MonitoringPlans;
            var monitorablePlans = todayPlans
                .Concat(yesterdayPlans)
                .Where(IsPlanMonitorable)
                .ToList();

            if (monitorablePlans.Count > 0)
            {
                var stockCodes = monitorablePlans.Select(p => p.StockCode).Distinct().ToList();
                var dataMap = await FetchBatchDataWithCache(stockCodes);

                foreach (var plan in monitorablePlans)
                {
                    if (dataMap.TryGetValue(plan.StockCode, out var data) && data != null)
                    {
                        await CheckOvernightSellSignalsAsync(plan, data);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 盘后处理 - 对应 planScheduler.js handleAfterMarket
    /// </summary>
    private async Task HandleAfterMarketAsync()
    {
        var now = Now;
        var hours = _marketTime.GetHours(now);
        var todayStr = _marketTime.FormatDate(now);

        // 收盘后超过 30 分钟（15:30 后）不再推送
        if (hours > 15.5m)
        {
            SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
            ClearAfterMarketSnooze();
            return;
        }

        // 当日收盘提醒只提醒一次
        var notified = LoadAfterMarketNotified();
        if (notified.Date == todayStr && notified.Done)
        {
            SaveAfterMarketNotified(notified);
            return;
        }

        // 检查 snooze
        var snoozeUntil = _afterMarketSnoozeUntil;
        var nowMs = NowMs;
        if (snoozeUntil > 0 && nowMs < snoozeUntil)
        {
            return;
        }
        if (snoozeUntil > 0 && nowMs >= snoozeUntil)
        {
            ClearAfterMarketSnooze();
        }

        // 读取盘后提醒间隔
        var intervalMin = _settingsStore.Settings.AfterMarketReminderInterval;
        if (intervalMin <= 0) intervalMin = 3;
        var intervalMs = intervalMin * 60 * 1000;

        // 限频
        if (nowMs - _afterMarketLastReminder < intervalMs)
        {
            return;
        }

        var pendingPlans = _tradePlanStore.PendingTodayPlans;
        if (pendingPlans.Count == 0)
        {
            return;
        }

        SaveAfterMarketLastReminder(nowMs);

        // 今日信号总结
        var todaySummary = CollectTodaySignalSummary();

        var planNames = string.Join("\n", pendingPlans.Select(p =>
            $"  {p.StockName}({p.StockCode}) {PlanTypeText(p.PlanType)}"));
        var planIds = pendingPlans.Select(p => p.Id).ToList();

        var sections = new List<string>();
        if (todaySummary.Count > 0)
        {
            sections.Add("今日信号总结：");
            sections.AddRange(todaySummary);
            sections.Add("");
        }
        sections.Add($"未完成计划（{pendingPlans.Count} 条待处理）：");
        sections.Add(planNames);
        sections.Add("");
        sections.Add("请选择处理方式：");

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "after_market",
            Level = ReminderLevel.Alert,
            Title = $"收盘提醒 - {pendingPlans.Count} 条计划待处理",
            Content = string.Join("\n", sections),
            Importance = 5,
            Persistent = true,
            Actions = new List<ReminderAction>
            {
                new() { Type = "after_market_record", Label = "添加交易记录", PlanIds = planIds },
                new() { Type = "after_market_continue", Label = "继续执行", PlanIds = planIds },
                new() { Type = "after_market_complete", Label = "全部完成", PlanIds = planIds },
                new() { Type = "after_market_dismiss", Label = "稍后提醒", PlanIds = planIds }
            }
        });

        SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
    }

    /// <summary>
    /// 非工作时段处理 - 对应 planScheduler.js handleNonWorkingTime
    /// </summary>
    private async Task HandleNonWorkingTimeAsync()
    {
        // 非工作时段不推送任何交易相关提醒
        // mood 由 petStore.updateTimeStatus() 自动管理
        await Task.CompletedTask;
    }

    /// <summary>
    /// 非交易日处理 - 对应 planScheduler.js handleNonTradingDay
    /// </summary>
    private async Task HandleNonTradingDayAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        var holidayName = _marketTime.GetHolidayName();

        if (!string.IsNullOrEmpty(holidayName))
        {
            // 每日仅首次进入非交易日时设置心情和提醒，避免每秒重复触发
            if (_lastNonTradingDayMoodSetDate != todayStr)
            {
                _lastNonTradingDayMoodSetDate = todayStr;
                _petStore.SetMood(MoodType.Celebrating);
                _petStore.ScheduleMoodRestore(3000);
            }

            if (ShouldShowNonTradingDayReminder())
            {
                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "system",
                    Level = ReminderLevel.Hint,
                    Title = $"{holidayName}快乐",
                    Content = $"今天是{holidayName}，市场休市。\n祝您节日愉快！",
                    Importance = 2
                });
                _lastNonTradingDayReminderDate = todayStr;
                try
                {
                    using var conn = _db.CreateConnection();
                    SaveConfig(conn, "pet_last_non_trading_day_reminder_date", todayStr);
                }
                catch { /* 持久化失败不影响本次已推送的提醒 */ }
            }
        }

        // 市场摘要播报
        await ShowMarketDigestAsync();
    }

    /// <summary>
    /// 用全量分时数据自算 VWAP = Σ(price×volume) / Σ(volume)（对齐 Electron）
    /// 失败时返回 0，由调用方降级到上一快照的均价
    /// </summary>
    private async Task<decimal> FetchTrendsVwapAsync(string stockCode)
    {
        try
        {
            List<IntradayPoint>? trends;
            if (_trendsCache.TryGetValue(stockCode, out var cached) &&
                (Now - cached.FetchedAt).TotalSeconds < TrendsCacheTtlSec)
            {
                trends = cached.Data;
            }
            else
            {
                trends = await _marketData.GetIntradayAsync(stockCode);
                _trendsCache[stockCode] = (trends, Now);
            }

            if (trends == null || trends.Count == 0) return 0;

            decimal cumVol = 0, cumVolPrice = 0;
            foreach (var t in trends)
            {
                if (t.Volume > 0 && t.Price > 0)
                {
                    cumVol += t.Volume;
                    cumVolPrice += t.Price * t.Volume;
                }
            }
            if (cumVol > 0)
            {
                var calculatedVwap = cumVolPrice / cumVol;
                if (calculatedVwap > 0) return calculatedVwap;
            }
            // 降级：用接口最后一条的 avgPrice
            var last = trends[^1];
            return last.AvgPrice > 0 ? last.AvgPrice : 0;
        }
        catch
        {
            return 0; // 静默降级，由调用方用上一快照均价兜底
        }
    }

    /// <summary>
    /// 获取快照 - 对应 planScheduler.js getSnapshots（内存缓存优先）
    /// </summary>
    public List<PriceSnapshot> GetSnapshots(string stockCode)
    {
        if (_snapshotCache.TryGetValue(stockCode, out var cache))
        {
            lock (cache)
            {
                return cache.ToList();
            }
        }
        return new List<PriceSnapshot>();
    }

    /// <summary>
    /// 保存快照到数据库 - 对应 planScheduler.js saveSnapshot
    /// </summary>

    // ============================================================================
    // 数据获取缓存 - 对应 planScheduler.js fetchBatchDataWithCache / fetchDailyKlinesWithCache 等
    // ============================================================================

    /// <summary>
    /// 批量获取行情（带缓存，TTL 由 RefreshIntervalMs 决定）- 对应 planScheduler.js fetchBatchDataWithCache
    /// </summary>
    public async Task<Dictionary<string, StockQuote>> FetchBatchDataWithCache(List<string> stockCodes)
    {
        var result = new Dictionary<string, StockQuote>();
        var now = Now;
        var cacheTtl = TimeSpan.FromMilliseconds(Math.Max(3000, _settingsStore.Settings.RefreshIntervalMs));

        // 检查缓存
        foreach (var code in stockCodes)
        {
            if (_batchQuoteCache.TryGetValue(code, out var cached))
            {
                if (cached.ExpiresAt > now)
                {
                    result[code] = cached.Data;
                }
            }
        }

        // 获取未缓存或过期的
        var toFetch = stockCodes
            .Where(c => !result.ContainsKey(c))
            .Distinct()
            .ToList();

        if (toFetch.Count > 0)
        {
            var quotes = await _marketData.GetQuotesAsync(toFetch);
            var expiry = now.Add(cacheTtl);

            foreach (var quote in quotes)
            {
                result[quote.Code] = quote;
                _batchQuoteCache[quote.Code] = (quote, expiry);
            }
        }

        return result;
    }

    /// <summary>
    /// 获取日K线（带缓存 TTL=5分钟）- 对应 planScheduler.js fetchDailyKlinesWithCache
    /// 空结果同样 5 分钟短 TTL 自动重试，并打日志使失败可见
    /// </summary>
    public async Task<List<KLineData>> FetchDailyKlinesWithCache(string stockCode)
    {
        var now = Now;
        if (_dailyKlineCache.TryGetValue(stockCode, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Data;
        }

        var klines = await _marketData.GetDailyKLinesAsync(stockCode);

        if (klines.Count > 0)
        {
            // 成功：缓存 5 分钟（对齐 Electron DAILY_KLINE_CACHE_TTL=5min）。
            // 盘中日K的最后一根是今日实时K线（close=当前最新价，富途/东财均如此），
            // MA5/MA10/MA30 与行情软件口径一致的前提就是这根K线准实时。
            // 旧实现缓存到当日结束 → 今日收盘价冻结在首次拉取时刻，全天均线基于过时价格。
            _dailyKlineCache[stockCode] = (klines, now.AddMinutes(5));
        }
        else
        {
            // 失败：短TTL（5分钟）后自动重试，避免空结果被缓存一整天导致检测降级
            _dailyKlineCache[stockCode] = (klines, now.AddMinutes(5));
            Log.Warning("[计划调度] {Code} 日K线获取为空，5分钟后重试（卖点关键位/ATR检测降级中）", stockCode);
        }

        return klines;
    }

    /// <summary>
    /// 获取资金流向（带缓存 TTL=5分钟）- 对应 planScheduler.js fetchCapitalFlowWithCache
    /// 富途不可用时返回 null 自动跳过
    /// </summary>
    public async Task<object?> FetchCapitalFlowWithCache(string stockCode)
    {
        var now = Now;
        if (_capitalFlowCache.TryGetValue(stockCode, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Data;
        }

        // 富途资金流向（简化：返回 null 表示不可用）
        object? capitalFlow = null;
        var expiry = now.AddMinutes(5);
        _capitalFlowCache[stockCode] = (capitalFlow, expiry);

        return await Task.FromResult(capitalFlow);
    }

    /// <summary>前一交易日的每日擒牛（对齐原版 loadLatestTradingDayPicks，读本地 dailyPicks 表）</summary>
    private List<(string Code, string Name)> LoadLatestTradingDayPicks()
    {
        try
        {
            var expectedDate = _marketTime.FormatDate(_marketTime.GetPreviousTradingDay(Now));
            using var conn = _db.CreateConnection();
            return conn.Query("SELECT stockCode AS Code, stockName AS Name FROM dailyPicks WHERE pickDate = @d",
                    new { d = expectedDate })
                .Select(r => ((string)r.Code, (string)r.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MA5检查] 读取每日擒牛失败");
            return new List<(string, string)>();
        }
    }

    /// <summary>
    /// 加载最近交易日的擒牛股
    /// </summary>
    private async Task<List<DailyPick>> LoadLatestTradingDayPicksAsync()
    {
        try
        {
            using var conn = _db.CreateConnection();
            var prevTradingDay = _marketTime.FormatDate(_marketTime.GetPreviousTradingDay(Now));
            const string sql = @"
                SELECT stockCode AS StockCode, stockName AS StockName, pickDate AS PickDate, remark AS Reason
                FROM dailyPicks WHERE pickDate = @PickDate ORDER BY id DESC LIMIT 20";
            var picks = conn.Query<DailyPick>(sql, new { PickDate = prevTradingDay }).ToList();
            return await Task.FromResult(picks);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 加载擒牛数据失败");
            return new List<DailyPick>();
        }
    }

    // ============================================================================
    // 今日信号总结 - 对应 planScheduler.js collectTodaySignalSummary
    // ============================================================================

    /// <summary>
    /// 收集今日触发过的信号总结（用于盘后总结）
    /// </summary>

    // ============================================================================
    // 今日信号总结 - 对应 planScheduler.js collectTodaySignalSummary
    // ============================================================================

    /// <summary>
    /// 收集今日触发过的信号总结（用于盘后总结）
    /// </summary>
    public List<string> CollectTodaySignalSummary()
    {
        var summaries = new List<string>();
        var planMap = new Dictionary<string, (TradePlan Plan, List<(string Type, SignalStateEntry State)> Signals)>();

        // 按 planId 分组信号
        foreach (var (key, state) in _signalStates)
        {
            var parts = key.Split(':');
            if (parts.Length < 2) continue;
            var planId = parts[0];
            var sigType = parts[1];
            if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(sigType)) continue;

            var plan = _tradePlanStore.GetPlan(planId);
            if (plan == null) continue;

            if (!planMap.ContainsKey(planId))
            {
                planMap[planId] = (plan, new List<(string, SignalStateEntry)>());
            }

            var entry = planMap[planId];
            entry.Signals.Add((sigType, state));
            planMap[planId] = entry;
        }

        // 生成总结文案
        foreach (var (plan, signals) in planMap.Values)
        {
            var lines = new List<string>();
            foreach (var (type, state) in signals)
            {
                if (type == "target")
                {
                    var reason = state.Reason;
                    if (reason == "reached")
                        lines.Add($"目标价 {plan.TargetPrice} 已到位");
                    else if (reason == "breakthrough")
                        lines.Add($"目标价 {plan.TargetPrice} 已突破");
                    else if (reason == "pullback")
                        lines.Add($"目标价 {plan.TargetPrice} 到过又回落");
                    else if (reason == "approaching")
                        lines.Add($"目标价 {plan.TargetPrice} 接近过");
                }
                else if (type == "stop")
                {
                    lines.Add($"止损价 {plan.StopLoss} 触及过");
                }
                else if (type == "limit_sealed")
                {
                    lines.Add($"{(state.State == "up" ? "涨停" : "跌停")}封板");
                }
            }

            if (lines.Count > 0)
            {
                summaries.Add($"  {plan.StockName}：{string.Join("，", lines)}");
            }
        }

        return summaries;
    }

    /// <summary>
    /// 因子权重优化 - 对应 planScheduler.js optimizeFactorWeights
    /// 策略：因子级 reward 精调 + 区分性特征分析
    /// </summary>
    public List<FactorChange> OptimizeFactorWeights()
    {
        try
        {
            var currentWeights = _multiFactorEngine.GetWeights();
            if (currentWeights.Count == 0)
            {
                currentWeights = new Dictionary<string, decimal>(DefaultFactorWeights);
            }

            var newWeights = new Dictionary<string, decimal>(currentWeights);
            var factorStats = _signalEventStore.GetFactorRewardStats();
            const int minSamples = 5;
            var factorAdjusted = false;
            var adjustments = new List<string>();

            foreach (var (fkey, fs) in factorStats)
            {
                if (fs.Total < minSamples) continue;
                if (!newWeights.ContainsKey(fkey)) continue;

                // 区分性特征分析
                var dp = fs.DiscriminativePower;
                if (Math.Abs(dp) > 10 && fs.HighQualityCount >= 3 && fs.LowQualityCount >= 3)
                {
                    if (dp > 15)
                    {
                        // 区分性特征强 → 增权
                        newWeights[fkey] = Math.Min(0.40m, currentWeights[fkey] * 1.08m);
                        factorAdjusted = true;
                        adjustments.Add($"{fkey}↑区分性(dp={dp:F0})");
                        continue;
                    }
                    if (dp < -10)
                    {
                        // 噪声特征 → 降权
                        newWeights[fkey] = Math.Max(0.05m, currentWeights[fkey] * 0.90m);
                        factorAdjusted = true;
                        adjustments.Add($"{fkey}↓噪声(dp={dp:F0})");
                        continue;
                    }
                }

                // 常规 reward 分支
                if (fs.AvgReward > 0.6)
                {
                    newWeights[fkey] = Math.Min(0.40m, currentWeights[fkey] * 1.12m);
                    factorAdjusted = true;
                    adjustments.Add($"{fkey}↑(reward={fs.AvgReward:F2},{fs.Total}次)");
                }
                else if (fs.AvgReward < 0.4)
                {
                    newWeights[fkey] = Math.Max(0.05m, currentWeights[fkey] * 0.88m);
                    factorAdjusted = true;
                    adjustments.Add($"{fkey}↓(reward={fs.AvgReward:F2},{fs.Total}次)");
                }
                else
                {
                    // 中间地带：结合 highRewardRate 和 optimalHitRate 温和调整
                    if (fs.HighRewardRate > 0.55 || fs.OptimalHitRate > 0.3)
                    {
                        newWeights[fkey] = Math.Min(0.40m, currentWeights[fkey] * 1.04m);
                        factorAdjusted = true;
                        adjustments.Add($"{fkey}轻微↑");
                    }
                    else if (fs.HighRewardRate < 0.4 && fs.OptimalHitRate < 0.15)
                    {
                        newWeights[fkey] = Math.Max(0.05m, currentWeights[fkey] * 0.94m);
                        factorAdjusted = true;
                        adjustments.Add($"{fkey}轻微↓");
                    }
                }
            }

            // 回退策略：因子级样本不足时用整体胜率粗调
            if (!factorAdjusted)
            {
                var stats = _signalEventStore.GetRecentStats();
                var sellSignalTypes = stats
                    .Where(kvp => kvp.Value.Total >= 5 && SellSignalTypes.Contains(kvp.Key))
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (sellSignalTypes.Count == 0) return new List<FactorChange>();

                var totalSuccess = sellSignalTypes.Sum(t => stats[t].Success);
                var totalFail = sellSignalTypes.Sum(t => stats[t].Fail);
                var totalTests = totalSuccess + totalFail;
                if (totalTests < 5) return new List<FactorChange>();

                var overallWinRate = (decimal)totalSuccess / totalTests;
                if (overallWinRate < 0.4m)
                {
                    newWeights["ma_pressure"] = Math.Min(0.35m, currentWeights.GetValueOrDefault("ma_pressure") * 1.15m);
                    newWeights["surge_angle"] = Math.Min(0.30m, currentWeights.GetValueOrDefault("surge_angle") * 1.12m);
                    newWeights["kline_pattern"] = Math.Max(0.05m, currentWeights.GetValueOrDefault("kline_pattern") * 0.85m);
                    newWeights["intraday_pattern"] = Math.Max(0.05m, currentWeights.GetValueOrDefault("intraday_pattern") * 0.9m);
                    Log.Information("[自进化-因子] 卖点整体胜率 {WinRate:F1}% < 40%，加强均线压力+拉升角度权重",
                        overallWinRate * 100);
                }
                else if (overallWinRate > 0.6m && totalTests >= 10)
                {
                    foreach (var key in newWeights.Keys.ToList())
                    {
                        var old = currentWeights.GetValueOrDefault(key);
                        var def = DefaultFactorWeights.GetValueOrDefault(key);
                        newWeights[key] = old * 0.8m + def * 0.2m;
                    }
                    Log.Information("[自进化-因子] 卖点整体胜率 {WinRate:F1}% > 60%，因子权重向默认值回归",
                        overallWinRate * 100);
                }
            }

            // 检测权重是否有实际变化
            var hasChange = false;
            var weightChanges = new List<FactorChange>();
            foreach (var key in newWeights.Keys)
            {
                var oldW = currentWeights.GetValueOrDefault(key);
                var newW = newWeights[key];
                if (Math.Abs(newW - oldW) > 0.005m)
                {
                    hasChange = true;
                    weightChanges.Add(new FactorChange
                    {
                        Factor = key,
                        OldWeight = JsMath.JsRound(oldW, 4),
                        NewWeight = JsMath.JsRound(newW, 4),
                        Direction = newW > oldW ? "up" : "down",
                        Strategy = "reward_based"
                    });
                }
            }

            if (hasChange)
            {
                _multiFactorEngine.UpdateWeights(newWeights);
                Log.Information("[自进化-因子] 因子权重已更新: {Adjustments}",
                    string.Join(", ", adjustments));
            }

            return weightChanges;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化-因子] 权重优化失败");
            return new List<FactorChange>();
        }
    }

    /// <summary>
    /// 信号权重自进化 - 对应 planScheduler.js optimizeSignalWeights
    /// 策略：基于各信号类型历史胜率调整权重乘子
    /// </summary>
    public List<SignalChange> OptimizeSignalWeights()
    {
        try
        {
            var stats = _signalEventStore.GetRecentStats();
            var currentMultipliers = _sellPointDetector.GetSignalMultipliers();
            var newMultipliers = new Dictionary<string, decimal>(currentMultipliers);
            var signalChanges = new List<SignalChange>();
            var adjustments = new List<string>();
            const int minSamples = 5;

            foreach (var (type, stat) in stats)
            {
                if (stat.Total < minSamples) continue;
                if (!SellSignalTypes.Contains(type)) continue;

                var winRate = stat.Total > 0 ? (decimal)stat.Success / stat.Total : 0.5m;
                var avgReward = stat.AvgReward;
                var optimalHitRate = stat.Total > 0
                    ? (decimal)(stat.NearDayHighCount + stat.BeforeMaxDrawdownCount) / stat.Total
                    : 0;
                var worstHitRate = stat.Total > 0 ? (decimal)stat.NearDayLowCount / stat.Total : 0;
                var failRate = stat.Total > 0 ? (decimal)stat.Fail / stat.Total : 0;
                var waveLowRate = stat.Total > 0 ? (decimal)stat.WaveLowCount / stat.Total : 0;
                var waveHighRate = stat.Total > 0 ? (decimal)stat.WaveHighCount / stat.Total : 0;

                // 静音需强证据
                var strongNoise = (winRate <= 0.35m && failRate > 0.5m)
                    || (waveLowRate >= 0.5m && waveHighRate <= 0.25m && stat.Total >= 10);
                var downFloor = strongNoise ? 0.15m : 0.35m;
                var oldM = currentMultipliers.GetValueOrDefault(type, 1.0m);
                var newM = oldM;
                var reason = "";

                if (winRate >= 0.6m && avgReward >= 0.55m)
                {
                    newM = oldM * 1.10m;
                    reason = "高胜率+高奖励";
                    adjustments.Add($"{stat.SignalLabel ?? type}↑(胜率{winRate * 100:F0}%)");
                }
                else if (winRate <= 0.35m || avgReward <= 0.4m || strongNoise)
                {
                    newM = oldM * (strongNoise ? 0.80m : 0.85m);
                    reason = strongNoise ? "回测质量强证据(波谷占比高)" : (avgReward <= 0.4m ? "低奖励(噪声)" : "低胜率");
                    adjustments.Add($"{stat.SignalLabel ?? type}↓(胜率{winRate * 100:F0}%)");
                }
                else if (optimalHitRate > 0.3m)
                {
                    newM = oldM * 1.06m;
                    reason = $"最优卖点命中率{optimalHitRate * 100:F0}%";
                    adjustments.Add($"{stat.SignalLabel ?? type}↑(最优卖点命中率{optimalHitRate * 100:F0}%)");
                }
                else if (worstHitRate > 0.3m)
                {
                    newM = oldM * 0.90m;
                    reason = $"最差卖点命中率{worstHitRate * 100:F0}%";
                    adjustments.Add($"{stat.SignalLabel ?? type}↓(最差卖点命中率{worstHitRate * 100:F0}%)");
                }
                else
                {
                    // 中间地带：向 1.0 回归
                    newM = oldM * 0.95m + 1.0m * 0.05m;
                    // 防静音闪烁：已静音信号不得跨回静音线上方
                    if (oldM <= MonitorConfig.SignalMuteThreshold)
                    {
                        newM = Math.Min(newM, MonitorConfig.SignalMuteThreshold);
                    }
                }

                // 降步应用静音保护下限
                if (newM < oldM) newM = Math.Max(downFloor, newM);
                newMultipliers[type] = newM;

                if (Math.Abs(newM - oldM) > 0.02m)
                {
                    signalChanges.Add(new SignalChange
                    {
                        SignalType = type,
                        SignalLabel = stat.SignalLabel ?? type,
                        OldMultiplier = JsMath.JsRound(oldM, 3),
                        NewMultiplier = JsMath.JsRound(newM, 3),
                        Direction = newM > oldM ? "up" : "down",
                        WinRate = JsMath.JsRound(winRate, 3),
                        AvgReward = JsMath.JsRound(avgReward, 3),
                        Total = stat.Total,
                        Reason = reason
                    });
                }
            }

            if (signalChanges.Count > 0)
            {
                _sellPointDetector.UpdateSignalMultipliers(newMultipliers);
                if (adjustments.Count > 0)
                {
                    Log.Information("[自进化-信号] 权重乘子调整: {Adjustments}",
                        string.Join(", ", adjustments));
                }
            }

            return signalChanges;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化-信号] 权重优化失败");
            return new List<SignalChange>();
        }
    }

    /// <summary>
    /// 进化搜索 - 对应 planScheduler.js runEvolutionSearch
    /// 回放驱动的闭环迭代参数搜索（重写修复）：
    ///   1. 以当前参数回放近 5 日事件（ReplayWithParams 模拟，不动引擎）
    ///   2. 从回放归因（blame/credit）推导候选调整步（定向压漏网者/升误杀者）
    ///   3. 候选参数重放 → 损失变小则接受，否则记负归因（连续2次同方向失败冻结）
    ///   4. 循环直到达标或轮次用尽；仅最终改进时才回填引擎
    /// 旧实现的致命缺陷：直接改活引擎参数，评分却用与参数无关的静态统计公式——
    /// newScore 恒等于 currentScore → 永远回滚；且回滚经 clamp 后不精确，每次运行
    /// 都漂移污染参数。此版与 Electron 同构：候选先模拟、改进才落地。
    /// </summary>
    public async Task<EvolutionSearchResult> RunEvolutionSearchAsync()
    {
        const int maxRounds = 8;
        const int minSearchEvents = 8;
        const decimal targetLowFilter = 0.8m;
        const decimal targetHighKeep = 0.9m;

        var result = new EvolutionSearchResult();

        // 损失函数：高质量误杀的惩罚权重(2.5)高于低质量漏网(1.0)，波次违规最重(10)——宁少杀不误杀
        decimal LossOf(ReplayResult r)
        {
            var lowGap = r.LowTotal > 0 && r.LowFilterRate.HasValue
                ? Math.Max(0m, targetLowFilter - (decimal)r.LowFilterRate.Value) : 0m;
            var highGap = r.HighTotal > 0 && r.HighKeepRate.HasValue
                ? Math.Max(0m, targetHighKeep - (decimal)r.HighKeepRate.Value) : 0m;
            return lowGap + 2.5m * highGap + 10m * r.WaveViolations.Count;
        }

        try
        {
            // 归因冻结日衰减：防止多日后全部参数被冻结导致搜索永久失效
            _signalEventStore.DecayAttributionFreezes();

            var baseM = _sellPointDetector.GetSignalMultipliers();
            var baseF = _multiFactorEngine.GetWeights();
            var res = _signalEventStore.ReplayWithParams(ToDoubleMap(baseM), ToDoubleMap(baseF));
            if (res.Replayable < minSearchEvents)
            {
                Log.Information("[自进化-搜索] 可回放事件 {Count} < {Min}，跳过搜索", res.Replayable, minSearchEvents);
                return await Task.FromResult(result);
            }

            result.OldScore = LossOf(res);

            var curM = new Dictionary<string, decimal>(baseM);
            var curF = new Dictionary<string, decimal>(baseF);
            var improved = false;

            for (var round = 0; round < maxRounds; round++)
            {
                if (LossOf(res) <= 0.001m) break; // 已达标
                var steps = DeriveSearchSteps(res);
                if (steps.Count == 0) break; // 无可用参数（全部冻结）

                var stepped = false;
                foreach (var step in steps.Take(3)) // 每轮最多尝试3个候选步，取第一个有改进的
                {
                    var cand = BuildCandidateParams(curM, curF, step);
                    if (cand == null) continue; // 该参数已到边界无空间
                    var r2 = _signalEventStore.ReplayWithParams(
                        ToDoubleMap(cand.Value.M), ToDoubleMap(cand.Value.F));
                    var better = LossOf(r2) < LossOf(res) - 0.001m
                        && r2.WaveViolations.Count <= res.WaveViolations.Count;
                    if (better)
                    {
                        _signalEventStore.UpdateAttribution(new List<AttributionRoundEntry>
                        {
                            new()
                            {
                                ParamKey = step.Key, Kind = step.Target == "factor_weight" ? "factor" : "signal",
                                Label = step.Key,
                                Delta = (double)(cand.Value.NewValue - cand.Value.OldValue),
                                LowFiltered = r2.LowFiltered - res.LowFiltered,
                                HighKilled = res.HighKept - r2.HighKept
                            }
                        });
                        result.AppliedSteps.Add(new SearchStep
                        {
                            Target = step.Target, Key = step.Key, Direction = step.Direction,
                            OldValue = cand.Value.OldValue, NewValue = cand.Value.NewValue
                        });
                        curM = cand.Value.M;
                        curF = cand.Value.F;
                        res = r2;
                        improved = true;
                        stepped = true;
                        break;
                    }
                    else
                    {
                        // 负归因：连续2次同方向失败会触发冻结
                        _signalEventStore.UpdateAttribution(new List<AttributionRoundEntry>
                        {
                            new()
                            {
                                ParamKey = step.Key, Kind = step.Target == "factor_weight" ? "factor" : "signal",
                                Label = step.Key,
                                Delta = (double)(cand.Value.NewValue - cand.Value.OldValue),
                                Failed = true
                            }
                        });
                    }
                }
                if (!stepped) break;
            }

            if (improved)
            {
                // 回填引擎（仅改进时；引擎内部可能归一化权重，最终以引擎实际状态为准）
                _sellPointDetector.UpdateSignalMultipliers(curM);
                _multiFactorEngine.UpdateWeights(curF);
                // 用引擎回填后的实际参数重放一次，报告与实盘完全一致
                try
                {
                    res = _signalEventStore.ReplayWithParams(
                        ToDoubleMap(_sellPointDetector.GetSignalMultipliers()),
                        ToDoubleMap(_multiFactorEngine.GetWeights()));
                }
                catch { /* 保留搜索结果 */ }
                result.Improved = true;
                result.NewScore = LossOf(res);
                Log.Information("[自进化-搜索] 闭环搜索改进：损失 {Old:F3}→{New:F3}，{Count} 步",
                    result.OldScore, result.NewScore, result.AppliedSteps.Count);
            }
            else
            {
                result.NewScore = result.OldScore;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化-搜索] 闭环搜索失败");
        }

        return await Task.FromResult(result);
    }

    /// <summary>
    /// 推导搜索步骤 - 对应 planScheduler.js _deriveSearchSteps(res)
    /// 从回放归因推导：blame（低质量漏网者）压低、credit（高质量误杀者）提升（仅当高质量保留率&lt;95%时优先）
    /// </summary>
    private List<SearchStep> DeriveSearchSteps(ReplayResult res)
    {
        var steps = new List<SearchStep>();
        try
        {
            var ledger = _signalEventStore.GetAttributionLedger();
            var entries = ledger?.Entries ?? new Dictionary<string, AttributionEntry>();

            foreach (var (paramKey, weight) in res.Blame)
            {
                if (weight <= 0) continue;
                if (entries.TryGetValue(paramKey, out var e) && e.Frozen) continue;
                var isFactor = paramKey.StartsWith("factor:");
                steps.Add(new SearchStep
                {
                    Target = isFactor ? "factor_weight" : "signal_multiplier",
                    Key = isFactor ? paramKey["factor:".Length..] : paramKey,
                    Direction = "down",
                    Weight = (decimal)weight
                });
            }

            var highNeedsHelp = res.HighTotal > 0 && res.HighKeepRate.HasValue && res.HighKeepRate < 0.95;
            if (highNeedsHelp)
            {
                foreach (var (paramKey, weight) in res.Credit)
                {
                    if (weight <= 0) continue;
                    if (entries.TryGetValue(paramKey, out var e) && e.Frozen) continue;
                    var isFactor = paramKey.StartsWith("factor:");
                    steps.Add(new SearchStep
                    {
                        Target = isFactor ? "factor_weight" : "signal_multiplier",
                        Key = isFactor ? paramKey["factor:".Length..] : paramKey,
                        Direction = "up",
                        Weight = (decimal)weight * 1.2m // 高质量误杀优先于低质量漏网
                    });
                }
            }

            steps = steps.OrderByDescending(s => s.Weight).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化-搜索] 推导调整步失败");
        }
        return steps;
    }

    /// <summary>
    /// 构造候选参数（纯函数，不改动引擎状态）- 对应 planScheduler.js _applySearchStep
    /// 信号乘子步长：降 ×0.80 / 升 ×1.15，clamp [0.35, 1.6]（下限与静音线对齐）；
    /// 已在 0.36 以下的类型无降步空间（防止 clamp 反向抬升）
    /// 因子权重步长：降 ×0.92 / 升 ×1.08，clamp [0.05, 0.40]
    /// </summary>
    private (Dictionary<string, decimal> M, Dictionary<string, decimal> F,
        decimal OldValue, decimal NewValue)? BuildCandidateParams(
        Dictionary<string, decimal> curM, Dictionary<string, decimal> curF, SearchStep step)
    {
        if (step.Target == "factor_weight")
        {
            var f = new Dictionary<string, decimal>(curF);
            if (!f.TryGetValue(step.Key, out var old) || old <= 0) return null;
            var mag = step.Direction == "down" ? 0.92m : 1.08m;
            var nv = Math.Max(0.05m, Math.Min(0.40m, old * mag));
            if (Math.Abs(nv - old) < 0.001m) return null;
            f[step.Key] = nv;
            return (curM, f, old, nv);
        }

        var m = new Dictionary<string, decimal>(curM);
        var oldVal = m.GetValueOrDefault(step.Key, 1.0m);
        // 降步不得低于静音保护线：已在 0.36 以下的类型无降步空间
        if (step.Direction == "down" && oldVal <= 0.36m) return null;
        var magS = step.Direction == "down" ? 0.80m : 1.15m;
        var floor = step.Direction == "down" ? 0.35m : 0.15m;
        var nvS = Math.Max(floor, Math.Min(1.6m, oldVal * magS));
        if (Math.Abs(nvS - oldVal) < 0.01m) return null;
        m[step.Key] = nvS;
        return (m, curF, oldVal, nvS);
    }


    private static Dictionary<string, double> ToDoubleMap(Dictionary<string, decimal> src)
        => src.ToDictionary(kv => kv.Key, kv => (double)kv.Value);

    /// <summary>
    /// 漏报复活 - 对应 planScheduler.js _resurrectMutedFromMissed
    /// 检查被静音的信号是否有漏报（应该触发但没触发），如果有则复活
    /// </summary>
    private List<string> ResurrectMutedFromMissed()
    {
        var resurrected = new List<string>();

        try
        {
            var multipliers = _sellPointDetector.GetSignalMultipliers();
            var stats = _signalEventStore.GetRecentStats();

            foreach (var (type, stat) in stats)
            {
                if (!multipliers.TryGetValue(type, out var currentMult)) continue;
                if (currentMult > MonitorConfig.SignalMuteThreshold) continue; // 未被静音

                // 检查是否漏报：胜率回升到 50% 以上且样本充足
                if (stat.Total >= 5)
                {
                    var winRate = (decimal)stat.Success / stat.Total;
                    if (winRate >= 0.5m)
                    {
                        // 复活：拉回 0.5
                        multipliers[type] = 0.5m;
                        resurrected.Add(type);
                        Log.Information("[自进化-复活] 信号 {Type} 漏报复活(胜率回升到 {WinRate:F0}%)",
                            type, winRate * 100);
                    }
                }
            }

            if (resurrected.Count > 0)
            {
                _sellPointDetector.UpdateSignalMultipliers(multipliers);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化-复活] 漏报复活失败");
        }

        return resurrected;
    }

    /// <summary>
    /// 显示自进化报告 - 对应 planScheduler.js _showSelfEvolutionReport
    /// </summary>
}
