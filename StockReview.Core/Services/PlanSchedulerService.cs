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

// ============================================================================
// Enums
// ============================================================================

/// <summary>时间状态 - 对应 planScheduler.js 的 TIME_STATUS</summary>
public enum TimeStatus
{
    PreMarket,      // 盘前 (8:00-9:30)
    Trading,        // 交易中 (9:30-11:30, 13:00-15:00)
    AfterMarket,    // 盘后 (15:00-20:00)
    NonWorking,     // 非工作时段 (20:00-次日8:00)
    NonTradingDay   // 非交易日（周末/节假日）
}

/// <summary>计划状态 - 对应 PLAN_STATUS</summary>
public enum PlanStatus
{
    Pending,
    Active,
    Completed,
    Cancelled
}

/// <summary>执行状态 - 对应 EXECUTION_STATUS</summary>
public enum ExecutionStatus
{
    NotExecuted,
    Executed,
    Partial,
    Cancelled
}

/// <summary>提醒级别</summary>
public enum ReminderLevel
{
    Info,
    Hint,
    Alert,
    Critical
}

/// <summary>宠物心情类型</summary>
public enum MoodType
{
    Neutral,
    Happy,
    Sad,
    Thinking,
    Sleeping,
    Resting,
    Celebrating,
    Excited
}

/// <summary>日内时段 - 对应 marketTime.getIntradayPhase</summary>
public enum IntradayPhase
{
    PreOpen,        // 9:00 前
    CallAuction,    // 9:15-9:25 集合竞价
    PreMatch,       // 9:25-9:30 集合竞价尾段
    Morning,        // 9:30-11:30 上午
    Lunch,          // 11:30-13:00 午休
    Afternoon,      // 13:00-14:57 下午
    CloseAuction,   // 14:57-15:00 收盘集合竞价
    Closed          // 15:00 后
}

// ============================================================================
// Configuration
// ============================================================================
public class MonitorConfig
{
    /// <summary>快速涨跌检测窗口</summary>
    public List<RapidWindow> RapidWindows { get; set; } = new()
    {
        // 对齐业务意图与 Electron v2 原设计（快照节奏 SnapshotIntervalSec=10s）：
        // 9/30/60/120 bars ≈ 1.5/5/10/20 分钟，对应 ≥1%/≥2%/≥3%/≥4% 触发 脉冲/中速/慢牛/持续推升。
        new() { Bars = 9,   Pct = 1.0m, Label = "脉冲",     CooldownMs = 5 * 60 * 1000 },
        new() { Bars = 30,  Pct = 2.0m, Label = "中速",     CooldownMs = 10 * 60 * 1000 },
        new() { Bars = 60,  Pct = 3.0m, Label = "慢牛",     CooldownMs = 20 * 60 * 1000 },
        new() { Bars = 120, Pct = 4.0m, Label = "持续推升", CooldownMs = 30 * 60 * 1000 }
    };

    /// <summary>进场价跌幅强制止损阈值（%）</summary>
    public decimal EntryDropThreshold { get; set; } = 5m;

    /// <summary>进场价跌幅强制止损冷却（毫秒）</summary>
    public int EntryDropCooldownMs { get; set; } = 30 * 60 * 1000;

    /// <summary>目标价接近阈值（%）</summary>
    public decimal PriceNearThreshold { get; set; } = 1.5m;

    /// <summary>目标价突破阈值（%）</summary>
    public decimal PriceBreakthroughThreshold { get; set; } = 0.5m;

    /// <summary>快照记录间隔（秒）</summary>
    public int SnapshotIntervalSec { get; set; } = 10;

    /// <summary>快照缓存最大数量（10秒节奏下覆盖全交易日，对齐 Electron 全天快照）</summary>
    public int SnapshotCacheSize { get; set; } = 500;

    /// <summary>快照批量落地间隔（秒）</summary>
    public int SnapshotFlushIntervalSec { get; set; } = 60;

    /// <summary>限频清理间隔（秒）</summary>
    public int RateLimitCleanIntervalSec { get; set; } = 120;

    /// <summary>收盘后 snooze 分钟数</summary>
    public int AfterMarketSnoozeMinutes { get; set; } = 5;

    /// <summary>信号静音阈值</summary>
    public const decimal SignalMuteThreshold = 0.35m;

    /// <summary>盘后提醒默认间隔（分钟）</summary>
    public int AfterMarketReminderIntervalMin { get; set; } = 3;
}

/// <summary>快速涨跌检测窗口定义</summary>
public class RapidWindow
{
    public int Bars { get; set; }
    public decimal Pct { get; set; }
    public string Label { get; set; } = "";
    public int CooldownMs { get; set; }
}

// ============================================================================
// Models
// ============================================================================

/// <summary>价格快照 - 对应 planScheduler.js 的 snapshot</summary>
public class PriceSnapshot
{
    public string StockCode { get; set; } = "";
    public decimal Price { get; set; }
    /// <summary>本采样区间增量成交量（对齐 Electron：检测器消费区间量）</summary>
    public long Volume { get; set; }
    /// <summary>当日累计成交量（行情接口原始值，用于计算区间增量）</summary>
    public long CumulativeVolume { get; set; }
    public decimal Amount { get; set; }
    public DateTime Timestamp { get; set; }
    /// <summary>分时均价（真实VWAP，分时数据自算）</summary>
    public decimal Vwap { get; set; }
    /// <summary>本采样区间最高价（富途秒级推送维护，仅内存）</summary>
    public decimal High { get; set; }
    /// <summary>本采样区间最低价（富途秒级推送维护，仅内存）</summary>
    public decimal Low { get; set; }
    /// <summary>量可靠性标记（富途实时 vs 东财延迟）</summary>
    public bool VolumeReliable { get; set; }
}

/// <summary>信号状态条目</summary>
public class SignalStateEntry
{
    public string State { get; set; } = "";
    public long At { get; set; }
    public string? Reason { get; set; }
    public decimal? Price { get; set; }
}

/// <summary>限频记录</summary>
public class RateLimitRecord
{
    public List<long> Timestamps { get; set; } = new();
}

/// <summary>波内限发状态</summary>
public class WaveGateState
{
    public decimal LastPrice { get; set; }
    public long LastDirection { get; set; } // 1=up, -1=down, 0=flat
    public long WaveStartAt { get; set; }
    public decimal WaveHigh { get; set; }
    public decimal WaveLow { get; set; }
    public string? LastSignalType { get; set; }
}

/// <summary>快速涨跌检测结果</summary>
public class RapidMatch
{
    public string Direction { get; set; } = ""; // up/down
    public decimal ChangePct { get; set; }
    public int WindowBars { get; set; }
    public string WindowLabel { get; set; } = "";
    public int CooldownMs { get; set; }
    /// <summary>实际窗口时间跨度（分钟，按快照时间戳计算）</summary>
    public double WindowMinutes { get; set; }
}

/// <summary>涨跌停封板检测结果</summary>
public class LimitMoveResult
{
    public bool Sealed { get; set; }
    public string Direction { get; set; } = ""; // up/down
    public decimal LimitPrice { get; set; }
}

/// <summary>收集到的信号（目标价/止损价）</summary>
public class CollectedSignal
{
    public string Type { get; set; } = ""; // target/stop/limit_sealed/rapid/entry_drop
    public string State { get; set; } = "";
    public string? Reason { get; set; }
    public decimal? Price { get; set; }
    public decimal? ChangePct { get; set; }
}

/// <summary>卖点信号信息</summary>
public class SellSignalInfo
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal Score { get; set; }
    public decimal? Similarity { get; set; }
    public string? PriorityName { get; set; }
    public decimal TotalScore { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>买点信号信息</summary>
public class BuySignalInfo
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal Score { get; set; }
}

/// <summary>评分提醒信息</summary>
public class ScoreAlertInfo
{
    public decimal TotalScore { get; set; }
    public string PriorityName { get; set; } = "";
    public List<SellSignalInfo> Signals { get; set; } = new();
}

/// <summary>信号事件记录</summary>
public class SignalEventRecord
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string SignalType { get; set; } = "";
    public string SignalLabel { get; set; } = "";
    public decimal Price { get; set; }
    public long Timestamp { get; set; }
    public int SnapshotIndex { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>提醒请求</summary>
public class ReminderRequest
{
    public string? Id { get; set; }
    public string Type { get; set; } = "";
    public ReminderLevel Level { get; set; } = ReminderLevel.Info;
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public int Importance { get; set; } = 3;
    public int DurationMs { get; set; } = 0;
    public bool Persistent { get; set; }
    public List<ReminderAction>? Actions { get; set; }
}

/// <summary>盘后提醒状态</summary>
public class AfterMarketNotifiedState
{
    public string Date { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>尾盘 MA5 检查状态</summary>
public class PreCloseMA5State
{
    public string Date { get; set; } = "";
    /// <summary>上次播报时间戳（ms）：控制真正的 5 分钟复查间隔（原 CheckCount 序号去重会被 1s tick 在 6 秒内耗尽）</summary>
    public long LastReminderAt { get; set; }
}

/// <summary>信号近期统计</summary>
public class SignalStat
{
    public int Total { get; set; }
    public int Success { get; set; }
    public int Fail { get; set; }
    public decimal AvgReward { get; set; }
    public string? SignalLabel { get; set; }
    public int NearDayHighCount { get; set; }
    public int NearDayLowCount { get; set; }
    public int BeforeMaxDrawdownCount { get; set; }
    public int WaveHighCount { get; set; }
    public int WaveLowCount { get; set; }
}

/// <summary>进化搜索结果</summary>
public class EvolutionSearchResult
{
    public bool Improved { get; set; }
    public decimal OldScore { get; set; }
    public decimal NewScore { get; set; }
    public List<SearchStep> AppliedSteps { get; set; } = new();
}

/// <summary>搜索步骤</summary>
public class SearchStep
{
    public string Target { get; set; } = ""; // signal_multiplier / factor_weight / threshold
    public string Key { get; set; } = "";
    public decimal Multiplier { get; set; }
    public string? Direction { get; set; }
    /// <summary>归因权重（blame/credit 分值，决定步骤优先级）</summary>
    public decimal Weight { get; set; }
    /// <summary>应用前的参数值（报告用）</summary>
    public decimal OldValue { get; set; }
    /// <summary>应用后的参数值（报告用）</summary>
    public decimal NewValue { get; set; }
}

/// <summary>因子权重变更</summary>
public class FactorChange
{
    public string Factor { get; set; } = "";
    public decimal OldWeight { get; set; }
    public decimal NewWeight { get; set; }
    public string Direction { get; set; } = "";
    public string Strategy { get; set; } = "";
}

/// <summary>信号乘子变更</summary>
public class SignalChange
{
    public string SignalType { get; set; } = "";
    public string SignalLabel { get; set; } = "";
    public decimal OldMultiplier { get; set; }
    public decimal NewMultiplier { get; set; }
    public string Direction { get; set; } = "";
    public decimal WinRate { get; set; }
    public decimal AvgReward { get; set; }
    public int Total { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>日内精选</summary>
public class DailyPick
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string PickDate { get; set; } = "";
    public string? Reason { get; set; }
}

// ============================================================================
// Interfaces for External Dependencies (Pinia stores → DI interfaces)
// ============================================================================

/// <summary>宠物服务接口 - 对应 usePetStore()</summary>
public interface IPetStore
{
    void AddReminder(ReminderRequest request);
    void HideBubble();
    void SetMood(MoodType mood);
    void ScheduleMoodRestore(int delayMs);
    void ScheduleUpgrade(ReminderRequest reminder, int delayMs, string level);
    void UpdateTimeStatus();
}

/// <summary>交易计划存储接口 - 对应 useTradePlanStore()</summary>
public interface ITradePlanStore
{
    List<TradePlan> Plans { get; }
    List<TradePlan> TodayPlans { get; }
    List<TradePlan> YesterdayPlans { get; }
    /// <summary>持仓过夜监控计划（所有早于今天的活跃计划，对应 Electron getMonitoringPlans）</summary>
    List<TradePlan> MonitoringPlans { get; }
    List<TradePlan> PendingTodayPlans { get; }
    TradePlan? GetPlan(string id);
    void UpdatePlan(string id, object updates);
    void RecordExecution(string id, object executionData);
}

/// <summary>设置存储接口 - 对应 usePetSettingsStore()</summary>
public interface IPetSettingsStore
{
    PetSettings Settings { get; }
}

/// <summary>宠物设置</summary>
public class PetSettings
{
    public bool SellPointDetection { get; set; } = true;
    public bool KeyLevelDetection { get; set; } = true;
    public decimal SurgePullbackThreshold { get; set; } = 1.5m;
    public decimal VolumeAmplifyMultiple { get; set; } = 2.0m;
    public decimal StagnantThreshold { get; set; } = 0.3m;
    public decimal SupportBreakdownTolerance { get; set; } = 0.2m;
    public decimal PriceNearThreshold { get; set; } = 1.5m;
    public int AfterMarketReminderInterval { get; set; } = 3;
    public bool CustomRemindersEnabled { get; set; } = true;
    /// <summary>尾盘 MA5 检查（14:30-15:00 未站上五日均线的监控股合并播报），默认开启</summary>
    public bool PreCloseMA5Check { get; set; } = true;
    /// <summary>轮询刷新间隔（毫秒），仅 HTTP 轮询模式生效；3/5/10 秒三挡</summary>
    public int RefreshIntervalMs { get; set; } = 5000;
}

/// <summary>自定义提醒存储接口</summary>
public interface ICustomRemindersStore
{
    List<CustomReminder> GetReminders();
    void AddReminder(CustomReminder reminder);
    void UpdateReminder(string id, CustomReminder reminder);
    void DeleteReminder(string id);
}

/// <summary>卖点检测器接口</summary>
public interface ISellPointDetector
{
    List<SellSignalInfo> Analyze(TradePlan plan, StockQuote data, List<PriceSnapshot> snapshots, List<KLineData> dailyKlines, object? capitalFlow);
    void UpdateConfig(object config);
    void UpdateSignalMultipliers(Dictionary<string, decimal> multipliers);
    Dictionary<string, decimal> GetSignalMultipliers();
    Dictionary<string, decimal> GetSignalMultipliersSnapshot();
}

/// <summary>买点检测器接口</summary>
public interface IBuyPointDetector
{
    List<BuySignalInfo> Analyze(TradePlan plan, StockQuote data, List<PriceSnapshot> snapshots, List<KLineData> dailyKlines);
}

/// <summary>信号事件存储接口</summary>
public interface ISignalEventStore
{
    void RecordEvent(SignalEventRecord record);
    Dictionary<string, SignalStat> GetRecentStats();
    Dictionary<string, FactorRewardStat> GetFactorRewardStats();
    void EvaluateTodaySignals(Dictionary<string, List<PriceSnapshot>> allSnapshots);
    List<SignalEventRecord> GetTodayEvents();

    /// <summary>回放：按候选参数重放近窗口事件（自进化搜索引擎的模拟评估核心）</summary>
    ReplayResult ReplayWithParams(Dictionary<string, double>? newMultipliers,
        Dictionary<string, double>? newFactorWeights, int days = 5);

    /// <summary>归因账本更新（正/负归因，连续失败触发冻结）</summary>
    void UpdateAttribution(List<AttributionRoundEntry> roundEntries);

    /// <summary>归因冻结衰减：新交易日解冻连续失败冻结、过期等效冻结</summary>
    void DecayAttributionFreezes();

    /// <summary>归因账本读取（搜索推导步骤时跳过冻结参数）</summary>
    AttributionLedger GetAttributionLedger();
}

/// <summary>多因子引擎接口</summary>
public interface IMultiFactorEngine
{
    Dictionary<string, decimal> GetWeights();
    void UpdateWeights(Dictionary<string, decimal> weights);
    decimal CalculateFusedScore(Dictionary<string, decimal> factorScores, Dictionary<string, decimal> weights);
}

/// <summary>市场时间服务接口 - 对应 marketTime 模块</summary>
public interface IMarketTimeService
{
    /// <summary>是否为交易日</summary>
    bool IsTradingDay(DateTime date);
    /// <summary>获取上一交易日</summary>
    DateTime GetPreviousTradingDay(DateTime date);
    /// <summary>获取下一交易日</summary>
    DateTime GetNextTradingDay(DateTime date);
    /// <summary>格式化日期为 yyyy-MM-dd</summary>
    string FormatDate(DateTime date);
    /// <summary>获取东八区小时（小数）</summary>
    decimal GetHours(DateTime date);
    /// <summary>获取东八区星期几（0=周日, 1=周一...6=周六）</summary>
    int GetDay(DateTime date);
    /// <summary>获取节假日名称</summary>
    string? GetHolidayName();
    /// <summary>获取日内时段</summary>
    (IntradayPhase Phase, string Label) GetIntradayPhase(DateTime now);
    /// <summary>获取当前东八区时间</summary>
    DateTime GetNow();
}

// ============================================================================
// PlanSchedulerService - 对应 planScheduler.js (~5014行)
// ============================================================================

/// <summary>
/// 交易计划调度器 - 对应 Electron 版 planScheduler.js
/// 
/// 核心职责：
/// 1. 交易时段检测与状态机切换（盘前/盘中/盘后/非交易日）
/// 2. 交易计划信号检查（目标价/止损价/快速涨跌/涨跌停封板）
/// 3. 分时卖点/买点检测路由
/// 4. 分时快照记录与缓存
/// 5. 自定义提醒调度
/// 6. 盘后总结与周末摘要
/// 7. 信号自进化（因子权重/信号乘子优化）
/// 8. 限频去重与波内限发
/// 9. 富途推送驱动检测
/// </summary>
public class PlanSchedulerService : IHostedService
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
    /// 处理收盘提醒操作 - 对应 planScheduler.js handleAfterMarketAction
    /// </summary>
    public void HandleAfterMarketAction(string actionType, List<string> planIds)
    {
        var todayStr = _marketTime.FormatDate(Now);

        switch (actionType)
        {
            case "after_market_continue":
            {
                // 继续执行：planDate 改为下一交易日
                var nextDateStr = _marketTime.FormatDate(_marketTime.GetNextTradingDay(Now));
                foreach (var id in planIds)
                {
                    _tradePlanStore.UpdatePlan(id, new
                    {
                        planDate = nextDateStr,
                        status = "pending",
                        executionStatus = "not_executed"
                    });
                }
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
                ClearAfterMarketSnooze();
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                _petStore.SetMood(MoodType.Happy);
                _petStore.ScheduleMoodRestore(3000);
                break;
            }

            case "after_market_complete":
            {
                foreach (var id in planIds)
                {
                    _tradePlanStore.RecordExecution(id, new
                    {
                        executionStatus = "executed",
                        note = "收盘自动完成"
                    });
                }
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
                ClearAfterMarketSnooze();
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                _petStore.SetMood(MoodType.Happy);
                _petStore.ScheduleMoodRestore(3000);
                break;
            }

            case "after_market_dismiss":
            {
                // 稍后提醒：清除 done + 设置 snooze
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = false });
                _afterMarketSnoozeUntil = NowMs + Config.AfterMarketSnoozeMinutes * 60 * 1000L;
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                break;
            }

            case "after_market_record":
            {
                // 打开交易记录面板：短 snooze
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = false });
                _afterMarketSnoozeUntil = NowMs + 2 * 60 * 1000L;
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                break;
            }
        }
    }

    // ============================================================================
    // 计划信号检查 - 对应 planScheduler.js checkPlanSignals / checkTodayPlan
    // ============================================================================

    /// <summary>
    /// 检查计划信号 - 对应 planScheduler.js checkPlanSignals
    /// 全量信号检查：快速涨跌 → 封板 → 进场价跌 → 目标价 → 止损价 → 卖点 → 买点
    /// </summary>
    public async Task CheckPlanSignals(TradePlan plan, StockQuote data)
    {
        // 行情请求期间计划可能已被执行
        if (!IsPlanMonitorable(plan)) return;
        if (data == null || !IsFinite(data.CurrentPrice) || data.CurrentPrice <= 0) return;

        // 午休时段不触发
        var (phase, _) = _marketTime.GetIntradayPhase(Now);
        if (phase == IntradayPhase.Lunch) return;

        var currentPrice = data.CurrentPrice;

        // 读取设置
        var sellPointEnabled = _settingsStore.Settings.SellPointDetection;

        // 同步卖点阈值
        if (sellPointEnabled)
        {
            var s = _settingsStore.Settings;
            _sellPointDetector.UpdateConfig(new
            {
                surgePullbackThreshold = s.SurgePullbackThreshold,
                volumeAmplifyMultiple = s.VolumeAmplifyMultiple,
                stagnantThreshold = s.StagnantThreshold,
                supportBreakdownTolerance = s.SupportBreakdownTolerance,
                priceNearThreshold = s.PriceNearThreshold
            });
        }

        // 优化参数同步
        SyncOptimizedParams();

        // 1. 快速涨跌检测（多时间窗口）
        var snaps = GetSnapshots(plan.StockCode);
        var rapidMatch = DetectMultiWindowRapid(snaps);
        if (rapidMatch != null)
        {
            var coolKey = $"{plan.Id}:rapid_window_{rapidMatch.Direction}";
            if (ShouldEmitSignal(coolKey, "triggered", rapidMatch.CooldownMs))
            {
                if (CheckRateLimit(plan.StockCode, "price_alert", 2, 60 * 1000))
                {
                    var direction = rapidMatch.Direction == "up" ? "拉升" : "下跌";
                    var changeTxt = (rapidMatch.ChangePct >= 0 ? "+" : "") +
                                    rapidMatch.ChangePct.ToString("F2", CultureInfo.InvariantCulture) + "%";
                    var minutes = rapidMatch.WindowMinutes.ToString("F1", CultureInfo.InvariantCulture);

                    // 数据收集计划：仅留痕不弹气泡
                    if (plan.PlanType != "watch")
                    {
                        var reminder = new ReminderRequest
                        {
                            Type = "price_alert",
                            Level = ReminderLevel.Alert,
                            Title = $"{plan.StockName} {rapidMatch.WindowLabel}{direction}",
                            Content = $"{plan.StockName}（{plan.StockCode}）{minutes}分钟内{rapidMatch.WindowLabel}{direction} {changeTxt}，现 {currentPrice} 元，建议查看分时决定是否{(rapidMatch.Direction == "up" ? "止盈/减仓" : "补仓/止损")}。",
                            StockCode = plan.StockCode,
                            StockName = plan.StockName,
                            Importance = 5,
                            DurationMs = 12000
                        };
                        _petStore.AddReminder(reminder);
                        _petStore.ScheduleUpgrade(reminder, 20000, "warning");
                    }

                    // 记录信号事件
                    _signalEventStore.RecordEvent(new SignalEventRecord
                    {
                        StockCode = plan.StockCode,
                        StockName = plan.StockName,
                        SignalType = $"rapid_{rapidMatch.Direction}_{rapidMatch.WindowLabel}",
                        SignalLabel = $"{rapidMatch.WindowLabel}{direction}",
                        Price = currentPrice,
                        Timestamp = NowMs,
                        SnapshotIndex = snaps.Count - 1,
                        Metadata = new Dictionary<string, object>
                        {
                            ["changePct"] = rapidMatch.ChangePct,
                            ["windowBars"] = rapidMatch.WindowBars,
                            ["windowLabel"] = rapidMatch.WindowLabel,
                            ["alerted"] = plan.PlanType != "watch",
                            ["collectOnly"] = plan.PlanType == "watch"
                        }
                    });
                }
            }
        }

        // 2. 涨跌停封板检测
        var limitMove = DetectLimitMove(plan.StockCode, plan.StockName, currentPrice, data);
        if (limitMove is { Sealed: true })
        {
            var key = $"{plan.Id}:limit_sealed";
            if (!_signalStates.ContainsKey(key))
            {
                _signalStates[key] = new SignalStateEntry
                {
                    State = limitMove.Direction,
                    At = NowMs
                };

                // 数据收集模式：仅标记状态不弹气泡
                if (plan.PlanType == "watch") return;

                var directionText = limitMove.Direction == "up" ? "涨停" : "跌停";
                var advice = limitMove.Direction == "up"
                    ? (plan.PlanType == "sell" ? "挂单排队中，可能无法成交" : "已封板，追涨风险大")
                    : (plan.PlanType == "buy" ? "抄底需谨慎" : "卖出可能无法成交，注意风险");

                if (!CheckRateLimit(plan.StockCode, "limit_move")) return;

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "limit_move",
                    Level = limitMove.Direction == "down" ? ReminderLevel.Critical : ReminderLevel.Alert,
                    Title = $"{plan.StockName} {directionText}封板",
                    Content = $"{plan.StockName}（{plan.StockCode}）当前价 {currentPrice}，{directionText}封板。\n{advice}。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 6,
                    DurationMs = 15000
                });
            }
            return; // 封板时不再检测其他信号
        }

        // 3. 进场价跌 5% 强制止损
        CheckEntryDropForceStop(plan, currentPrice);

        // 4. 目标价检测
        if ((plan.TargetPrice ?? 0) > 0)
        {
            await CheckTargetPriceAsync(plan, currentPrice, data);
        }

        // 5. 止损价检测
        if ((plan.StopLoss ?? 0) > 0)
        {
            await CheckStopLossAsync(plan, currentPrice, data);
        }

        // 6. 分时卖点识别
        await DetectAndRouteSellSignals(plan, data, sellPointEnabled);

        // 7. 分时买点识别
        await DetectAndRouteBuySignals(plan, data);
    }

    /// <summary>
    /// 检查今日计划 - 对应 planScheduler.js checkTodayPlan
    /// 与 checkPlanSignals 共用 N1 去重，负责盘中监控逻辑
    /// </summary>
    public async Task CheckTodayPlan(TradePlan plan, StockQuote data)
    {
        if (!IsPlanMonitorable(plan)) return;
        if (data == null || !IsFinite(data.CurrentPrice) || data.CurrentPrice <= 0) return;

        var (phase, _) = _marketTime.GetIntradayPhase(Now);

        // 午休时段跳过
        if (phase == IntradayPhase.Lunch) return;

        var currentPrice = data.CurrentPrice;

        // 读取设置
        var sellPointEnabled = _settingsStore.Settings.SellPointDetection;

        // 同步卖点阈值
        if (sellPointEnabled)
        {
            var s = _settingsStore.Settings;
            _sellPointDetector.UpdateConfig(new
            {
                surgePullbackThreshold = s.SurgePullbackThreshold,
                volumeAmplifyMultiple = s.VolumeAmplifyMultiple,
                stagnantThreshold = s.StagnantThreshold,
                supportBreakdownTolerance = s.SupportBreakdownTolerance,
                priceNearThreshold = s.PriceNearThreshold
            });
        }

        SyncOptimizedParams();

        // 1. 快速涨跌检测
        var snaps = GetSnapshots(plan.StockCode);
        var rapidMatch = DetectMultiWindowRapid(snaps);
        if (rapidMatch != null)
        {
            var coolKey = $"{plan.Id}:rapid_window_{rapidMatch.Direction}";
            if (ShouldEmitSignal(coolKey, "triggered", rapidMatch.CooldownMs))
            {
                if (CheckRateLimit(plan.StockCode, "price_alert", 2, 60 * 1000))
                {
                    var direction = rapidMatch.Direction == "up" ? "拉升" : "下跌";
                    var changeTxt = (rapidMatch.ChangePct >= 0 ? "+" : "") +
                                    rapidMatch.ChangePct.ToString("F2", CultureInfo.InvariantCulture) + "%";
                    var minutes = rapidMatch.WindowMinutes.ToString("F1", CultureInfo.InvariantCulture);

                    if (plan.PlanType != "watch")
                    {
                        var reminder = new ReminderRequest
                        {
                            Type = "price_alert",
                            Level = ReminderLevel.Alert,
                            Title = $"{plan.StockName} {rapidMatch.WindowLabel}{direction}",
                            Content = $"{plan.StockName}（{plan.StockCode}）{minutes}分钟内{rapidMatch.WindowLabel}{direction} {changeTxt}，现 {currentPrice} 元。",
                            StockCode = plan.StockCode,
                            StockName = plan.StockName,
                            Importance = 5,
                            DurationMs = 12000
                        };
                        _petStore.AddReminder(reminder);
                        _petStore.ScheduleUpgrade(reminder, 20000, "warning");
                    }
                }
            }
        }

        // 2. 涨跌停封板检测
        var limitMove = DetectLimitMove(plan.StockCode, plan.StockName, currentPrice, data);
        if (limitMove is { Sealed: true })
        {
            var key = $"{plan.Id}:limit_sealed";
            if (!_signalStates.ContainsKey(key))
            {
                _signalStates[key] = new SignalStateEntry { State = limitMove.Direction, At = NowMs };

                if (plan.PlanType == "watch") return;

                var directionText = limitMove.Direction == "up" ? "涨停" : "跌停";
                var advice = limitMove.Direction == "up"
                    ? (plan.PlanType == "sell" ? "挂单排队中，可能无法成交" : "已封板，追涨风险大")
                    : (plan.PlanType == "buy" ? "抄底需谨慎" : "卖出可能无法成交，注意风险");

                if (!CheckRateLimit(plan.StockCode, "limit_move")) return;

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "limit_move",
                    Level = limitMove.Direction == "down" ? ReminderLevel.Critical : ReminderLevel.Alert,
                    Title = $"{plan.StockName} {directionText}封板",
                    Content = $"{plan.StockName}（{plan.StockCode}）当前价 {currentPrice}，{directionText}封板。\n{advice}。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 6,
                    DurationMs = 15000
                });
            }
            return;
        }

        // 3. 进场价跌 5% 强制止损
        CheckEntryDropForceStop(plan, currentPrice);

        // 4. 目标价检测
        if ((plan.TargetPrice ?? 0) > 0)
        {
            await CheckTargetPriceAsync(plan, currentPrice, data);
        }

        // 5. 止损价检测
        if ((plan.StopLoss ?? 0) > 0)
        {
            await CheckStopLossAsync(plan, currentPrice, data);
        }

        // 6. 分时卖点识别
        await DetectAndRouteSellSignals(plan, data, sellPointEnabled);

        // 7. 分时买点识别
        await DetectAndRouteBuySignals(plan, data);
    }

    // ============================================================================
    // 目标价检测 - 对应 planScheduler.js collectTargetSignal / checkTargetPrice
    // ============================================================================

    /// <summary>
    /// 目标价检测 - 三状态机：approaching → reached → breakthrough / pullback
    /// </summary>
    private async Task CheckTargetPriceAsync(TradePlan plan, decimal currentPrice, StockQuote data)
    {
        var target = plan.TargetPrice ?? 0;
        if (target <= 0) return;

        var key = $"{plan.Id}:target";
        var diff = (currentPrice - target) / target * 100;
        var prevState = _signalStates.TryGetValue(key, out var entry) ? entry.State : "";
        var wasAboveTarget = prevState == "reached" || prevState == "breakthrough";

        // 对齐 Electron collectTargetSignal 状态判定（基于"当前价 vs 目标价"，防震荡重复触发）：
        // - reached：现价 ≥ 目标价 且 |diff| ≤ 阈值（刚到目标价）
        // - breakthrough：现价 ≥ 目标价 且超出阈值（大幅突破 / 从 reached 升级）
        // - pullback：之前在目标价上方，现回落到下方（最佳卖点窗口）
        // - approaching：现价 < 目标价 且距目标 ≤ 阈值（下方容差内接近）
        //   （旧实现 reached 在目标价下方阈值内即触发、approaching 无下界判定，语义偏差）
        string newState;
        string? reason = null;

        if (currentPrice >= target)
        {
            if (!wasAboveTarget)
            {
                newState = Math.Abs(diff) <= Config.PriceNearThreshold ? "reached" : "breakthrough";
                reason = newState;
            }
            else if (prevState == "reached" && Math.Abs(diff) > Config.PriceNearThreshold)
            {
                // 已到过目标价后继续大幅上行 → 升级为突破
                newState = "breakthrough";
                reason = "breakthrough";
            }
            else
            {
                // 停留在目标价上方小幅波动：不重复触发
                newState = "";
            }
        }
        else if (wasAboveTarget)
        {
            newState = "pullback";
            reason = "pullback";
        }
        else if (Math.Abs(diff) <= Config.PriceNearThreshold)
        {
            newState = "approaching";
            reason = "approaching";
        }
        else
        {
            newState = "normal";
        }

        if (string.IsNullOrEmpty(newState) || newState == "normal") return;

        // 同状态冷却（15分钟）+ 状态持久化（pullback/wasAboveTarget 判定依赖）
        if (!ShouldEmitSignal(key, newState, 15 * 60 * 1000)) return;

        // 级别去重
        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;
        MarkLevelHitNotified(plan.Id, newState);

        // 动作型提醒当日一次去重
        var actionKey = $"{plan.Id}:target_{newState}";
        if (_actionEmittedToday.ContainsKey(actionKey)) return;
        _actionEmittedToday[actionKey] = true;

        // 波内限发检查
        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch")
        {
            // 数据收集模式：仅记录不弹气泡
            return;
        }

        if (!CheckRateLimit(plan.StockCode, "target_price")) return;

        var (title, content, level) = newState switch
        {
            "breakthrough" => ($"{plan.StockName} 目标价突破",
                $"{plan.StockName}（{plan.StockCode}）已突破目标价 {target}，当前 {currentPrice} 元，涨幅 {diff:F2}%。", ReminderLevel.Alert),
            "reached" => ($"{plan.StockName} 目标价到位",
                $"{plan.StockName}（{plan.StockCode}）已到达目标价 {target}，当前 {currentPrice} 元。", ReminderLevel.Alert),
            "pullback" => ($"{plan.StockName} 目标价回落",
                $"{plan.StockName}（{plan.StockCode}）目标价 {target} 到过后回落，当前 {currentPrice} 元。", ReminderLevel.Hint),
            "approaching" => ($"{plan.StockName} 接近目标价",
                $"{plan.StockName}（{plan.StockCode}）接近目标价 {target}，当前 {currentPrice} 元，差距 {diff:F2}%。", ReminderLevel.Hint),
            _ => ("", "", ReminderLevel.Info)
        };

        if (string.IsNullOrEmpty(title)) return;

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "target_price",
            Level = level,
            Title = title,
            Content = content,
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = level == ReminderLevel.Alert ? 5 : 3,
            DurationMs = 10000
        });

        // 波内限发通过
        WaveGatePass(plan.StockCode, currentPrice, newState);

        // 记录信号事件
        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"target_{newState}",
            SignalLabel = reason ?? newState,
            Price = currentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["targetPrice"] = target,
                ["diff"] = diff,
                ["state"] = newState,
                ["alerted"] = plan.PlanType != "watch"
            }
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 止损价检测 - 对应 planScheduler.js collectStopLossSignal / checkStopLoss
    // ============================================================================

    /// <summary>
    /// 止损价检测 - 三状态机：approaching → touched / broken
    /// </summary>
    private async Task CheckStopLossAsync(TradePlan plan, decimal currentPrice, StockQuote data)
    {
        var stopLoss = plan.StopLoss ?? 0;
        if (stopLoss <= 0) return;

        var key = $"{plan.Id}:stop";
        var diff = (currentPrice - stopLoss) / stopLoss * 100;

        // 对齐 Electron collectStopLossSignal 状态判定：
        // - broken：现价低于止损价超过 0.1%（已跌破）
        // - touched：|diff| ≤ 0.1%（真正触及止损价，固定小容差）
        // - approaching：现价高于止损价且距止损 ≤ 用户设置阈值（PriceNearThreshold）
        //   （旧实现 approaching=阈值×2 导致超出设定仍触发"接近"、touched=±阈值
        //     导致未真正触及就报"触及止损价"，均与设置语义不符）
        const decimal HitTolerancePct = 0.1m;
        string newState;
        string? reason;

        if (diff < -HitTolerancePct)
        {
            newState = "broken";
            reason = "broken";
        }
        else if (Math.Abs(diff) <= HitTolerancePct)
        {
            newState = "touched";
            reason = "touched";
        }
        else if (diff <= Config.PriceNearThreshold)
        {
            newState = "approaching";
            reason = "approaching";
        }
        else
        {
            newState = "normal";
            reason = null;
        }

        if (!ShouldEmitSignal(key, newState, 10 * 60 * 1000)) return;
        if (newState == "normal") return;

        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;
        MarkLevelHitNotified(plan.Id, newState);

        var actionKey = $"{plan.Id}:stop_{newState}";
        if (_actionEmittedToday.ContainsKey(actionKey)) return;
        _actionEmittedToday[actionKey] = true;

        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch") return;

        // 止损使用 10 分钟窗口 3 次限频
        if (!CheckRateLimit(plan.StockCode, "stop_loss", 3, 10 * 60 * 1000)) return;

        var (title, content, level) = newState switch
        {
            "broken" => ($"{plan.StockName} 止损价跌破",
                $"{plan.StockName}（{plan.StockCode}）已跌破止损价 {stopLoss}，当前 {currentPrice} 元，跌幅 {-diff:F2}%。请立即评估是否止损。", ReminderLevel.Critical),
            "touched" => ($"{plan.StockName} 止损价触及",
                $"{plan.StockName}（{plan.StockCode}）已触及止损价 {stopLoss}，当前 {currentPrice} 元。请注意风险。", ReminderLevel.Alert),
            "approaching" => ($"{plan.StockName} 接近止损价",
                $"{plan.StockName}（{plan.StockCode}）接近止损价 {stopLoss}，当前 {currentPrice} 元，差距 {diff:F2}%。", ReminderLevel.Hint),
            _ => ("", "", ReminderLevel.Info)
        };

        if (string.IsNullOrEmpty(title)) return;

        var reminder = new ReminderRequest
        {
            Type = "stop_loss",
            Level = level,
            Title = title,
            Content = content,
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = level == ReminderLevel.Critical ? 7 : (level == ReminderLevel.Alert ? 6 : 3),
            DurationMs = level == ReminderLevel.Critical ? 20000 : 12000
        };
        _petStore.AddReminder(reminder);

        if (level == ReminderLevel.Critical)
        {
            _petStore.ScheduleUpgrade(reminder, 30000, "warning");
        }

        WaveGatePass(plan.StockCode, currentPrice, newState);

        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"stop_{newState}",
            SignalLabel = reason ?? newState,
            Price = currentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["stopLoss"] = stopLoss,
                ["diff"] = diff,
                ["state"] = newState,
                ["alerted"] = plan.PlanType != "watch"
            }
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 快速涨跌检测 - 对应 planScheduler.js detectMultiWindowRapid
    // ============================================================================

    /// <summary>
    /// 多时间窗口快速拉升/下跌检测（对齐 Electron detectMultiWindowRapid）
    /// 4个时间窗口（1.5min/5min/10min/20min）匹配不同拉升模式，任一窗口满足阈值即触发
    /// 方向判定：优先首尾涨跌幅，不够时用窗口波动率兜底（解决慢牛拉升不触发）
    /// </summary>
    public RapidMatch? DetectMultiWindowRapid(List<PriceSnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 9) return null;

        RapidMatch? bestMatch = null;

        foreach (var window in Config.RapidWindows)
        {
            if (snapshots.Count < window.Bars) continue;

            var recent = snapshots.TakeLast(window.Bars).ToList();
            var prices = recent.Select(s => s.Price).Where(p => p > 0).ToList();
            if (prices.Count < 2) continue;

            var firstPrice = prices[0];
            var lastPrice = prices[^1];
            var wLow = prices.Min();
            var wHigh = prices.Max();
            var changePct = (lastPrice - firstPrice) / firstPrice * 100;
            var volatilityPct = (wHigh - wLow) / Math.Min(wLow, firstPrice) * 100;

            // 方向判定：优先用首尾涨跌幅，不够时用波动率兜底
            var dir = changePct >= window.Pct ? "up"
                    : changePct <= -window.Pct ? "down"
                    : "normal";
            if (dir == "normal" && volatilityPct >= window.Pct)
            {
                dir = lastPrice < firstPrice ? "down" : lastPrice > firstPrice ? "up" : "normal";
            }

            if (dir == "normal") continue;

            // 选择满足条件的最长窗口（更可靠，避免短窗口噪音）；
            // 但如果短窗口幅度远超阈值（>2倍），优先选择短窗口（更及时）
            var ratio = Math.Abs(changePct) / window.Pct;
            if (bestMatch == null || window.Bars > bestMatch.WindowBars || ratio > 2)
            {
                bestMatch = new RapidMatch
                {
                    Direction = dir,
                    ChangePct = changePct,
                    WindowBars = window.Bars,
                    WindowLabel = window.Label,
                    CooldownMs = window.CooldownMs,
                    WindowMinutes = Math.Max(0.1, (recent[^1].Timestamp - recent[0].Timestamp).TotalMinutes)
                };
            }
        }

        return bestMatch;
    }

    // ============================================================================
    // 涨跌停封板检测 - 对应 planScheduler.js detectLimitMove
    // ============================================================================

    /// <summary>
    /// 涨跌停封板检测
    /// A 股规则：主板 ±10%，创业板/科创板 ±20%，ST ±5%
    /// 封板 = 当前价 == 涨停价/跌停价 且 卖一/买一 量极大
    /// </summary>
    public LimitMoveResult? DetectLimitMove(string stockCode, string stockName, decimal currentPrice, StockQuote data)
    {
        if (currentPrice <= 0 || data.PreClose <= 0) return null;

        // 判断涨跌幅限制
        var limitPct = GetLimitPct(stockCode);
        var limitUpPrice = Math.Round(data.PreClose * (1 + limitPct / 100), 2);
        var limitDownPrice = Math.Round(data.PreClose * (1 - limitPct / 100), 2);

        // 涨停封板：当前价 == 涨停价
        if (Math.Abs(currentPrice - limitUpPrice) < 0.01m)
        {
            return new LimitMoveResult
            {
                Sealed = true,
                Direction = "up",
                LimitPrice = limitUpPrice
            };
        }

        // 跌停封板：当前价 == 跌停价
        if (Math.Abs(currentPrice - limitDownPrice) < 0.01m)
        {
            return new LimitMoveResult
            {
                Sealed = true,
                Direction = "down",
                LimitPrice = limitDownPrice
            };
        }

        return new LimitMoveResult { Sealed = false };
    }

    /// <summary>
    /// 获取涨跌幅限制（%）
    /// </summary>
    private static decimal GetLimitPct(string stockCode)
    {
        // 创业板 30xxxx → 20%
        if (stockCode.StartsWith("30")) return 20m;
        // 科创板 68xxxx → 20%
        if (stockCode.StartsWith("68")) return 20m;
        // ST 股票 → 5%（简化判断：名称含 ST，实际应由调用方传入）
        // 北交所 8xxxxx/4xxxxx → 30%
        if (stockCode.StartsWith("8") || stockCode.StartsWith("4")) return 30m;
        // 主板默认 → 10%
        return 10m;
    }

    // ============================================================================
    // 进场价跌 5% 强制止损 - 对应 planScheduler.js _checkEntryDropForceStop
    // ============================================================================

    /// <summary>
    /// 进场价跌 5% 强制止损提示
    /// 即使未触及用户设置的止损价，只要相对进场价已跌 5% 就强制提醒
    /// </summary>
    private void CheckEntryDropForceStop(TradePlan plan, decimal currentPrice)
    {
        // 数据收集计划无真实持仓意图，跳过
        if (plan.PlanType == "watch") return;
        if ((plan.EntryPrice ?? 0) <= 0) return;

        var entryDropPct = ((double)(currentPrice - (plan.EntryPrice ?? 0m)) / (double)(plan.EntryPrice ?? 0m) * 100);
        if (entryDropPct > -(double)Config.EntryDropThreshold) return;

        var isCritical = entryDropPct <= -10;
        if (!ShouldEmitSignal($"{plan.Id}:entry_drop", "triggered", Config.EntryDropCooldownMs)) return;

        // 同股同类信息限频：10 分钟内最多 3 次
        if (!CheckRateLimit(plan.StockCode, "stop_loss", 3, 10 * 60 * 1000)) return;

        var dropAbs = Math.Abs(entryDropPct).ToString("F2", CultureInfo.InvariantCulture);

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "stop_loss",
            Level = isCritical ? ReminderLevel.Critical : ReminderLevel.Alert,
            Title = $"{plan.StockName} 强制止损提醒",
            Content = $"{plan.StockName}（{plan.StockCode}）从进场价 {plan.EntryPrice} 跌 {dropAbs}% 至 {currentPrice}。即使未到计划中的止损价 {plan.StopLoss}，已大幅亏损，请立即评估是否止损。",
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = isCritical ? 7 : 6,
            DurationMs = 20000
        });
    }

    // ============================================================================
    // 隔夜低开止损检测 - 对应 planScheduler.js checkOvernightSellSignals
    // ============================================================================

    /// <summary>
    /// 隔夜低开止损检测 + 单日亏损熔断
    /// 时间窗口：9:25-9:30 集合竞价时段
    /// </summary>
    public async Task CheckOvernightSellSignalsAsync(TradePlan plan, StockQuote data)
    {
        if (!IsPlanMonitorable(plan)) return;
        if (plan.PlanType == "watch") return;

        var now = Now;
        var hours = _marketTime.GetHours(now);

        // 时间窗口：9:25-9:30（9:30 后由 handleTradingTime 调用，但内部窗口已收窄）
        if (hours < 9 + 25m / 60m || hours >= 9.5m) return;

        if (data == null || data.CurrentPrice <= 0 || data.PreClose <= 0) return;

        var currentPrice = data.CurrentPrice;
        var preClose = data.PreClose;
        var openGapPct = (currentPrice - preClose) / preClose * 100;

        // 隔夜低开止损：低开超过 3%
        if (openGapPct < -3m)
        {
            var key = $"{plan.Id}:overnight_gap";
            if (!ShouldEmitSignal(key, "triggered", 30 * 60 * 1000)) return;

            if (!CheckRateLimit(plan.StockCode, "overnight_gap", 2, 30 * 60 * 1000)) return;

            _petStore.AddReminder(new ReminderRequest
            {
                Type = "overnight_gap",
                Level = openGapPct < -5 ? ReminderLevel.Critical : ReminderLevel.Alert,
                Title = $"{plan.StockName} 隔夜低开提醒",
                Content = $"{plan.StockName}（{plan.StockCode}）低开 {openGapPct:F2}%，当前 {currentPrice} 元，昨收 {preClose} 元。请评估是否止损或补仓。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = openGapPct < -5 ? 7 : 6,
                DurationMs = 20000
            });
        }

        // 单日亏损熔断：相对进场价亏损超过 8%
        if ((plan.EntryPrice ?? 0) > 0)
        {
            var dailyLossPct = ((double)(currentPrice - (plan.EntryPrice ?? 0m)) / (double)(plan.EntryPrice ?? 0m) * 100);
            if (dailyLossPct <= -8.0)
            {
                var key = $"{plan.Id}:daily_loss_breaker";
                if (!ShouldEmitSignal(key, "triggered", 30 * 60 * 1000)) return;

                if (!CheckRateLimit(plan.StockCode, "daily_loss_breaker", 2, 30 * 60 * 1000)) return;

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "daily_loss_breaker",
                    Level = ReminderLevel.Critical,
                    Title = $"{plan.StockName} 单日亏损熔断",
                    Content = $"{plan.StockName}（{plan.StockCode}）相对进场价已亏损 {Math.Abs(dailyLossPct):F2}%，触发单日亏损熔断。建议立即止损或减仓。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 7,
                    DurationMs = 30000
                });
            }
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 卖点/买点检测路由 - 对应 planScheduler.js _detectAndRouteSellSignals / _detectAndRouteBuySignals
    // ============================================================================

    private static readonly HashSet<string> KeyLevelTypes = new()
    { "break_ma5", "break_ma10", "break_ma30", "break_support" };

    /// <summary>
    /// 分时卖点检测 + 提醒路由
    /// 门控：全局 sellPointDetection 开关 + 计划级 monitorSellPoint
    /// 路由：2+ 信号共振 → emitScoreAlert；单信号 → emitSignalAlert；
    ///       形态相似度信号豁免（即使参与共振也额外单独提醒）
    /// </summary>
    private async Task DetectAndRouteSellSignals(TradePlan plan, StockQuote data, bool sellPointEnabled)
    {
        if (!sellPointEnabled || plan.MonitorSellPoint == 0) return;

        var snapshots = GetSnapshots(plan.StockCode);
        if (snapshots.Count < 5) return;

        // keyLevelDetection 关闭时过滤均线/支撑位跌破信号
        var keyLevelEnabled = _settingsStore.Settings.KeyLevelDetection;

        var dailyKlines = await FetchDailyKlinesWithCache(plan.StockCode);
        var capitalFlow = await FetchCapitalFlowWithCache(plan.StockCode);

        var signals = _sellPointDetector.Analyze(plan, data, snapshots, dailyKlines, capitalFlow);

        if (!keyLevelEnabled)
        {
            signals = signals.Where(s => !KeyLevelTypes.Contains(s.Type)).ToList();
        }

        if (signals.Count >= 2)
        {
            // 多信号共振 → 评分提醒
            await EmitScoreAlert(plan, signals);

            // 形态相似度信号豁免：即使参与共振也额外单独提醒
            foreach (var sig in signals)
            {
                if (PatternSimilarityTypes.Contains(sig.Type) && sig.Similarity != null)
                {
                    await EmitSignalAlert(plan, sig);
                }
            }
        }
        else if (signals.Count == 1)
        {
            await EmitSignalAlert(plan, signals[0]);
        }
    }

    /// <summary>
    /// 分时买点检测 + 提醒路由
    /// 门控：计划级 monitorBuyPoint=1
    /// </summary>
    private async Task DetectAndRouteBuySignals(TradePlan plan, StockQuote data)
    {
        if (plan.MonitorBuyPoint != 1) return;

        var snapshots = GetSnapshots(plan.StockCode);
        if (snapshots.Count < 5) return;

        var dailyKlines = await FetchDailyKlinesWithCache(plan.StockCode);
        var buySignals = _buyPointDetector.Analyze(plan, data, snapshots, dailyKlines);

        foreach (var signal in buySignals)
        {
            await EmitBuySignalAlert(plan, signal);
        }
    }

    // ============================================================================
    // 信号提醒发射 - 对应 planScheduler.js emitSignalAlert / emitBuySignalAlert / emitScoreAlert / emitCollectedSignal
    // ============================================================================

    /// <summary>
    /// 卖点信号提醒 - 对应 planScheduler.js emitSignalAlert
    /// 含静音门控、数据收集模式、形态相似度豁免
    /// </summary>
    private async Task EmitSignalAlert(TradePlan plan, SellSignalInfo signal)
    {
        // 静音门控：信号乘子 <= 静音阈值时跳过
        var multipliers = _sellPointDetector.GetSignalMultipliers();
        if (multipliers.TryGetValue(signal.Type, out var multiplier))
        {
            if (multiplier <= MonitorConfig.SignalMuteThreshold)
            {
                Log.Debug("[计划调度] 信号 {Type} 已静音(乘子={Multiplier:F3})，跳过", signal.Type, multiplier);
                return;
            }
        }

        // N1 去重
        var key = $"{plan.Id}:sell_{signal.Type}";
        if (!ShouldEmitSignal(key, "triggered", 15 * 60 * 1000)) return;

        // 波内限发
        if (!WaveGateAllows(plan.StockCode, data_currentPrice(signal), "sell")) return;

        // 同股同类限频
        if (!CheckRateLimit(plan.StockCode, "sell_signal", 2, 60 * 1000)) return;

        // 数据收集模式：仅记录不弹气泡
        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            _petStore.AddReminder(new ReminderRequest
            {
                Type = "sell_signal",
                Level = signal.Score >= 70 ? ReminderLevel.Alert : ReminderLevel.Hint,
                Title = $"{plan.StockName} {signal.Label}",
                Content = $"{plan.StockName}（{plan.StockCode}）触发卖点信号：{signal.Label}（评分 {signal.Score:F0}）。当前价 {signal.TotalScore:F2}。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = signal.Score >= 70 ? 6 : 4,
                DurationMs = 12000
            });
        }

        WaveGatePass(plan.StockCode, data_currentPrice(signal), "sell");

        // 记录信号事件
        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = signal.Type,
            SignalLabel = signal.Label,
            Price = signal.TotalScore,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = signal.Score,
                ["similarity"] = signal.Similarity ?? 0,
                ["alerted"] = !collectOnly,
                ["collectOnly"] = collectOnly
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// 买点信号提醒 - 对应 planScheduler.js emitBuySignalAlert
    /// </summary>
    private async Task EmitBuySignalAlert(TradePlan plan, BuySignalInfo signal)
    {
        var key = $"{plan.Id}:buy_{signal.Type}";
        if (!ShouldEmitSignal(key, "triggered", 15 * 60 * 1000)) return;

        if (!CheckRateLimit(plan.StockCode, "buy_signal", 2, 60 * 1000)) return;

        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            _petStore.AddReminder(new ReminderRequest
            {
                Type = "buy_signal",
                Level = signal.Score >= 70 ? ReminderLevel.Alert : ReminderLevel.Hint,
                Title = $"{plan.StockName} {signal.Label}",
                Content = $"{plan.StockName}（{plan.StockCode}）触发买点信号：{signal.Label}（评分 {signal.Score:F0}）。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = signal.Score >= 70 ? 6 : 4,
                DurationMs = 12000
            });
        }

        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"buy_{signal.Type}",
            SignalLabel = signal.Label,
            Price = signal.Score,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = signal.Score,
                ["alerted"] = !collectOnly,
                ["collectOnly"] = collectOnly
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// 多信号共振评分提醒 - 对应 planScheduler.js emitScoreAlert
    /// VIX 四档优先级：极高(>=80) / 高(60-79) / 中(40-59) / 低(<40)
    /// </summary>
    private async Task EmitScoreAlert(TradePlan plan, List<SellSignalInfo> signals)
    {
        // 计算综合评分
        var totalScore = signals.Sum(s => s.Score);
        var priorityName = totalScore switch
        {
            >= 80 => "极高",
            >= 60 => "高",
            >= 40 => "中",
            _ => "低"
        };

        // N1 去重
        var key = $"{plan.Id}:score_alert";
        if (!ShouldEmitSignal(key, priorityName, 10 * 60 * 1000)) return;

        // 波内限发
        var avgPrice = signals.Average(s => s.TotalScore);
        if (!WaveGateAllows(plan.StockCode, avgPrice, "score")) return;

        if (!CheckRateLimit(plan.StockCode, "score_alert", 2, 10 * 60 * 1000)) return;

        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            var signalLabels = string.Join("、", signals.Select(s => s.Label));
            _petStore.AddReminder(new ReminderRequest
            {
                Type = "score_alert",
                Level = totalScore >= 60 ? ReminderLevel.Alert : ReminderLevel.Hint,
                Title = $"{plan.StockName} {priorityName}优先级卖点提醒",
                Content = $"{plan.StockName}（{plan.StockCode}）触发 {signals.Count} 个卖点信号（{signalLabels}），综合评分 {totalScore:F0}（{priorityName}优先级）。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = totalScore >= 80 ? 7 : (totalScore >= 60 ? 6 : 4),
                DurationMs = 15000
            });
        }

        WaveGatePass(plan.StockCode, avgPrice, "score");

        // 记录信号事件
        foreach (var sig in signals)
        {
            _signalEventStore.RecordEvent(new SignalEventRecord
            {
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                SignalType = sig.Type,
                SignalLabel = sig.Label,
                Price = avgPrice,
                Timestamp = NowMs,
                Metadata = new Dictionary<string, object>
                {
                    ["score"] = sig.Score,
                    ["totalScore"] = totalScore,
                    ["priorityName"] = priorityName,
                    ["signalCount"] = signals.Count,
                    ["alerted"] = !collectOnly,
                    ["collectOnly"] = collectOnly
                }
            });
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 限频去重 - 对应 planScheduler.js shouldEmitSignal / checkRateLimit / cleanRateLimit
    // ============================================================================

    /// <summary>
    /// 信号去重检查 - 对应 planScheduler.js shouldEmitSignal
    /// 同一 key 同一状态在冷却时间内不重复触发
    /// </summary>
    public bool ShouldEmitSignal(string key, string state, int cooldownMs = 15 * 60 * 1000)
    {
        var now = NowMs;

        if (state == "normal")
        {
            // 不清除冷却记录：避免价格在阈值附近震荡时反复触发
            return false;
        }

        if (_signalStates.TryGetValue(key, out var previous))
        {
            if (previous.State == state && now - previous.At < cooldownMs)
            {
                return false;
            }
        }

        _signalStates[key] = new SignalStateEntry { State = state, At = now };
        return true;
    }

    /// <summary>
    /// 同股同类信息限频检查 - 对应 planScheduler.js checkRateLimit
    /// 滑动窗口：时间窗口内最多触发 maxCount 次
    /// </summary>
    public bool CheckRateLimit(string stockCode, string type, int maxCount = 2, int windowMs = 60 * 1000)
    {
        if (string.IsNullOrEmpty(stockCode) || string.IsNullOrEmpty(type)) return true;

        var key = $"{stockCode}:{type}";
        var now = NowMs;
        var windowStart = now - windowMs;

        var record = _rateLimiter.GetOrAdd(key, _ => new RateLimitRecord());
        lock (record)
        {
            record.Timestamps = record.Timestamps.Where(t => t >= windowStart).ToList();

            if (record.Timestamps.Count >= maxCount)
            {
                return false;
            }

            record.Timestamps.Add(now);
            return true;
        }
    }

    /// <summary>
    /// 清理过期的限频记录 - 对应 planScheduler.js cleanRateLimit
    /// 清理窗口 31 分钟，覆盖最大限频窗口（30 分钟 overnight_gap / daily_loss_breaker）
    /// </summary>
    public void CleanRateLimit()
    {
        var now = NowMs;
        const int windowMs = 31 * 60 * 1000;

        foreach (var kvp in _rateLimiter)
        {
            var record = kvp.Value;
            lock (record)
            {
                record.Timestamps = record.Timestamps.Where(t => now - t <= windowMs).ToList();
                if (record.Timestamps.Count == 0)
                {
                    _rateLimiter.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    // ============================================================================
    // 波内限发 - 对应 planScheduler.js _waveGateState / _waveGateAllows / _waveGatePass
    // ============================================================================

    /// <summary>
    /// 波内限发检查 - 同一价格波动波内只触发一次同类型信号
    /// 波的定义：价格单方向运动（上涨/下跌），直到出现方向反转
    /// </summary>
    private bool WaveGateAllows(string stockCode, decimal currentPrice, string signalType)
    {
        if (string.IsNullOrEmpty(stockCode)) return true;

        var state = _waveGateStates.GetOrAdd(stockCode, _ => new WaveGateState
        {
            LastPrice = currentPrice,
            WaveStartAt = NowMs,
            WaveHigh = currentPrice,
            WaveLow = currentPrice
        });

        lock (state)
        {
            // 判断方向是否反转
            var newDirection = currentPrice > state.LastPrice ? 1 : (currentPrice < state.LastPrice ? -1 : 0);

            if (newDirection != 0 && newDirection != state.LastDirection && state.LastDirection != 0)
            {
                // 方向反转 → 新波开始
                state.WaveStartAt = NowMs;
                state.WaveHigh = Math.Max(state.LastPrice, currentPrice);
                state.WaveLow = Math.Min(state.LastPrice, currentPrice);
                state.LastSignalType = null;
            }

            state.LastPrice = currentPrice;
            if (newDirection != 0) state.LastDirection = newDirection;
            state.WaveHigh = Math.Max(state.WaveHigh, currentPrice);
            state.WaveLow = Math.Min(state.WaveLow, currentPrice);

            // 检查是否已在当前波内触发过同类型信号
            if (state.LastSignalType == signalType)
            {
                return false; // 本波已触发过同类型信号，拒绝
            }

            return true;
        }
    }

    /// <summary>
    /// 波内限发通过 - 标记当前波已触发某类型信号
    /// </summary>
    private void WaveGatePass(string stockCode, decimal currentPrice, string signalType)
    {
        if (string.IsNullOrEmpty(stockCode)) return;

        if (_waveGateStates.TryGetValue(stockCode, out var state))
        {
            lock (state)
            {
                state.LastSignalType = signalType;
            }
        }
    }

    // ============================================================================
    // 级别去重 - 对应 planScheduler.js _isLevelHitNotifiedToday / _markLevelHitNotified
    // ============================================================================

    private bool IsLevelHitNotifiedToday(string planId, string level)
    {
        return _levelHitNotified.ContainsKey($"{planId}:{level}");
    }

    private void MarkLevelHitNotified(string planId, string level)
    {
        _levelHitNotified[$"{planId}:{level}"] = true;
    }

    // ============================================================================
    // 快照记录 - 对应 planScheduler.js recordSnapshots / saveSnapshot / getSnapshots / _flushSnapshots
    // ============================================================================

    /// <summary>
    /// 记录快照 - 对应 planScheduler.js recordSnapshots
    /// 10秒节奏 + 区间增量量 + 分时数据自算真实VWAP（对齐 Electron）
    /// </summary>
    private async Task RecordSnapshotsAsync(Dictionary<string, StockQuote> dataMap)
    {
        var now = Now;

        // 按配置间隔记录（默认10秒，对齐 Electron monitorIntervalMs=10s）
        if ((now - _lastSnapshotTime).TotalSeconds < Config.SnapshotIntervalSec)
        {
            return;
        }
        _lastSnapshotTime = now;

        // 并行获取分时数据，计算真实的分时均价（VWAP），60s缓存（分时数据每分钟更新一次）
        var vwapTasks = dataMap.Keys.Select(async code => (code, Vwap: await FetchTrendsVwapAsync(code)));
        var vwapResults = await Task.WhenAll(vwapTasks);
        var vwapMap = vwapResults.ToDictionary(r => r.code, r => r.Vwap);

        foreach (var (stockCode, quote) in dataMap)
        {
            if (quote == null || quote.CurrentPrice <= 0) continue;

            // 上一个快照（区间量与均价兜底的基准）
            var snapshots = GetSnapshots(stockCode);
            var previous = snapshots.Count > 0 ? snapshots[^1] : null;

            // 行情接口返回当日累计成交量，检测器需要每个采样区间的增量
            var cumulativeVolume = quote.Volume;
            var previousCumulative = previous?.CumulativeVolume ?? 0;
            // M8 修复：累计量比上一快照还低 = 数据源切换/接口重置，这个区间量不可信
            var volumeInvalid = previous != null && cumulativeVolume < previousCumulative;
            var intervalVolume = previous != null && cumulativeVolume >= previousCumulative
                ? cumulativeVolume - previousCumulative
                : 0;
            // M4 修复：降级数据源返回 volume=0，量比类信号静默失效 → 打不可靠标记跳过放量判断
            var volumeReliable = !volumeInvalid && cumulativeVolume > 0;

            var snapshot = new PriceSnapshot
            {
                StockCode = stockCode,
                Price = quote.CurrentPrice,
                Volume = intervalVolume,
                CumulativeVolume = cumulativeVolume,
                Amount = quote.Amount,
                Timestamp = now,
                // 分时均价：优先自算VWAP，其次上一快照的avgPrice（均价线缓慢变化），最后降级为当前价
                Vwap = vwapMap.TryGetValue(stockCode, out var realVwap) && realVwap > 0
                    ? realVwap
                    : (previous != null && previous.Vwap > 0 ? previous.Vwap : quote.CurrentPrice),
                VolumeReliable = volumeReliable
            };

            // 写入内存缓存
            var cache = _snapshotCache.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
            lock (cache)
            {
                cache.Add(snapshot);
                // 限制缓存大小
                if (cache.Count > Config.SnapshotCacheSize)
                {
                    cache.RemoveRange(0, cache.Count - Config.SnapshotCacheSize);
                }
            }

            // 写入批量落地缓冲
            var buffer = _snapshotBuffer.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
            lock (buffer)
            {
                buffer.Add(snapshot);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 分时数据缓存60秒（分时数据每分钟更新一次，快照10秒节奏拉全量分钟点过于频繁）
    /// </summary>
    private readonly ConcurrentDictionary<string, (List<IntradayPoint> Data, DateTime FetchedAt)> _trendsCache = new();
    private const int TrendsCacheTtlSec = 60;

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
    private void SaveSnapshot(PriceSnapshot snapshot)
    {
        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                INSERT INTO price_snapshots (stockCode, price, volume, amount, timestamp, vwap, volumeReliable, cumulativeVolume)
                VALUES (@StockCode, @Price, @Volume, @Amount, @Timestamp, @Vwap, @VolumeReliable, @CumulativeVolume)";
            conn.Execute(sql, new
            {
                snapshot.StockCode,
                snapshot.Price,
                snapshot.Volume,
                snapshot.Amount,
                Timestamp = snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                snapshot.Vwap,
                snapshot.VolumeReliable,
                snapshot.CumulativeVolume
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 保存快照失败: {Code}", snapshot.StockCode);
        }
    }

    /// <summary>
    /// 批量落地快照 - 对应 planScheduler.js _flushSnapshots
    /// </summary>
    private async Task FlushSnapshotsAsync()
    {
        var now = Now;
        if ((now - _lastSnapshotFlushTime).TotalSeconds < Config.SnapshotFlushIntervalSec)
        {
            return;
        }
        _lastSnapshotFlushTime = now;

        var allSnapshots = new List<PriceSnapshot>();

        foreach (var (stockCode, buffer) in _snapshotBuffer)
        {
            List<PriceSnapshot> toFlush;
            lock (buffer)
            {
                toFlush = buffer.ToList();
                buffer.Clear();
            }
            allSnapshots.AddRange(toFlush);
        }

        if (allSnapshots.Count == 0) return;

        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                INSERT INTO price_snapshots (stockCode, price, volume, amount, timestamp, vwap, volumeReliable, cumulativeVolume)
                VALUES (@StockCode, @Price, @Volume, @Amount, @Timestamp, @Vwap, @VolumeReliable, @CumulativeVolume)";

            conn.Execute(sql, allSnapshots.Select(s => new
            {
                s.StockCode,
                s.Price,
                s.Volume,
                s.Amount,
                Timestamp = s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Vwap,
                s.VolumeReliable,
                s.CumulativeVolume
            }));

            Log.Debug("[计划调度] 批量落地 {Count} 条快照", allSnapshots.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 批量落地快照失败");
        }

        await Task.CompletedTask;
    }

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
    /// 获取日K线（带缓存 TTL=当日）- 对应 planScheduler.js fetchDailyKlinesWithCache
    /// 空结果不缓存到当日（否则卖点检测整天降级），改用5分钟短TTL自动重试，并打日志使失败可见
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
            // 成功：缓存到当日结束
            var todayEnd = now.Date.AddDays(1);
            _dailyKlineCache[stockCode] = (klines, todayEnd);
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

    /// <summary>
    /// 清理过期缓存
    /// </summary>
    private void CleanupExpiredCaches()
    {
        var now = Now;
        CleanupCache(_batchQuoteCache, now);
        CleanupCache(_capitalFlowCache, now);
        // 分时VWAP缓存：清掉超过10分钟的陈旧条目（盘中会按60s TTL自动刷新）
        foreach (var key in _trendsCache.Keys.Where(k =>
            _trendsCache.TryGetValue(k, out var v) && (now - v.FetchedAt).TotalMinutes > 10).ToList())
        {
            _trendsCache.TryRemove(key, out _);
        }
        // 日K线缓存跨天清理在 OnDayChanged 中处理
    }

    private static void CleanupCache<T>(ConcurrentDictionary<string, (T Data, DateTime ExpiresAt)> cache, DateTime now)
    {
        foreach (var key in cache.Keys.Where(k =>
            cache.TryGetValue(k, out var v) && v.ExpiresAt <= now).ToList())
        {
            cache.TryRemove(key, out _);
        }
    }

    // ============================================================================
    // 自定义提醒检查 - 对应 planScheduler.js checkCustomReminders
    // ============================================================================

    /// <summary>
    /// 自定义提醒检查 - 跨窗口触发锁 + 二次校验
    /// </summary>
    private async Task CheckCustomRemindersAsync(DateTime now)
    {
        // 已停用：自定义提醒触发由 CustomReminderSchedulerService 专职负责（含当日去重/连弹/snooze/错过补发）。
        // 本旧路径按"分钟匹配 + _signalStates 去重"触发，不检查 Done 状态与 LastTriggeredAt，
        // 与专职调度器形成双路径重复触发：点完成后 ~30s 的下一次 30 秒轮询仍处同一分钟内会再次入队弹出。
        // 全局开关检查也随之移至 CustomReminderSchedulerService。
        await Task.CompletedTask;
    }

    // 停用路径的原始实现保留备查：
    private async Task CheckCustomRemindersAsync_Legacy(DateTime now)
    {
        if (!_settingsStore.Settings.CustomRemindersEnabled) return;

        // 限频：每 30 秒检查一次
        if ((now - _lastCustomReminderCheck).TotalSeconds < 30) return;
        _lastCustomReminderCheck = now;

        var reminders = _customRemindersStore.GetReminders();
        if (reminders.Count == 0) return;

        var nowTimeStr = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var todayStr = _marketTime.FormatDate(now);

        foreach (var reminder in reminders.Where(r => r.Enabled))
        {
            if (reminder.Time != nowTimeStr) continue;

            // 当日去重
            var dedupKey = $"custom_reminder_{reminder.Id}_{todayStr}";
            if (_signalStates.ContainsKey(dedupKey)) continue;

            _signalStates[dedupKey] = new SignalStateEntry { State = "triggered", At = NowMs };

            _petStore.AddReminder(new ReminderRequest
            {
                Id = $"custom_{reminder.Id}_{todayStr}",
                Type = "custom_reminder",
                Level = ReminderLevel.Hint,
                Title = reminder.Title ?? $"{reminder.StockName} 自定义提醒",
                Content = reminder.Content ?? $"{reminder.StockName}（{reminder.StockCode}）自定义提醒时间到。",
                StockCode = reminder.StockCode,
                StockName = reminder.StockName,
                Importance = 3,
                // 原版：气泡按钮来自用户在弹窗勾选的 actions（默认 ✅完成/⏰稍后提醒）
                // 每个动作注入原始提醒 ID（对齐 Electron 触发时 rawActions.map → reminderId）
                Actions = (reminder.Actions != null && reminder.Actions.Count > 0
                        ? reminder.Actions
                        : CustomRemindersService.DefaultActions)
                    .Select(a => new ReminderAction
                    {
                        Type = a.Type,
                        Label = a.Label,
                        PlanIds = a.PlanIds,
                        ReminderId = reminder.Id
                    })
                    .ToList()
            });
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 盘前 MA5 检查 - 对应 planScheduler.js checkPreCloseMA5
    // ============================================================================

    /// <summary>
    /// 尾盘 MA5 检查（14:30-15:00 每 5 分钟，对齐 Electron checkPreCloseMA5）
    /// 当前价低于 MA5（未站上五日均线）的监控股合并播报，提示可能触发卖出条件
    /// </summary>
    private async Task CheckPreCloseMA5Async()
    {
        // 设置开关（宠物设置-尾盘 MA5 检查，默认开启）
        if (!_settingsStore.Settings.PreCloseMA5Check) return;

        var now = Now;
        var hours = _marketTime.GetHours(now);

        // 仅在 14:30-15:00 执行
        if (hours < 14.5m || hours >= 15) return;

        var todayStr = _marketTime.FormatDate(now);
        if (_preCloseMA5State.Date != todayStr)
        {
            _preCloseMA5State = new PreCloseMA5State { Date = todayStr };
        }

        // 距上次播报不足 5 分钟不查（留 5 秒余量，避免 tick 间隔导致跳过）
        var nowMs = NowMs;
        if (_preCloseMA5State.LastReminderAt > 0 &&
            nowMs - _preCloseMA5State.LastReminderAt < 5 * 60 * 1000 - 5000)
            return;

        // 监控范围：今日计划 + 持仓过夜计划 + 前一交易日擒牛（对齐原版）
        var codeNameMap = new Dictionary<string, string>();
        foreach (var plan in _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans).Where(IsPlanMonitorable))
        {
            codeNameMap[plan.StockCode] = plan.StockName;
        }
        foreach (var (pickCode, pickName) in LoadLatestTradingDayPicks())
        {
            codeNameMap.TryAdd(pickCode, pickName);
        }

        if (codeNameMap.Count == 0)
        {
            Log.Information("[MA5检查] 当日无监控计划（昨日/今日/擒牛均为空），跳过");
            return;
        }

        var stockCodes = codeNameMap.Keys.ToList();
        var dataMap = await FetchBatchDataWithCache(stockCodes);

        var alerts = new List<string>();
        var quoteOk = 0;
        var ma5Ok = 0;
        foreach (var code in stockCodes)
        {
            if (!dataMap.TryGetValue(code, out var quote) || quote == null || quote.CurrentPrice <= 0)
                continue;
            quoteOk++;

            var dailyKlines = await FetchDailyKlinesWithCache(code);
            if (dailyKlines.Count < 5) continue;

            var ma5 = dailyKlines.TakeLast(5).Average(k => k.Close);
            if (ma5 <= 0) continue;
            ma5Ok++;

            // 当前价低于 5 日均线 → 未站上，收集待播报
            if (quote.CurrentPrice < ma5)
            {
                var name = quote.Name;
                if (string.IsNullOrEmpty(name) && !codeNameMap.TryGetValue(code, out name)) name = code;
                var deviation = (ma5 - quote.CurrentPrice) / ma5 * 100;
                alerts.Add($"• {name}({code}): 当前价 {quote.CurrentPrice} < MA5均价 {ma5:F2}，偏离 {deviation:F2}%");
            }
        }

        // 汇总日志：覆盖静默路径（行情部分失败 / MA5 部分失败 / 全部站上），零触发时可据此定位
        if (alerts.Count == 0)
        {
            Log.Information("[MA5检查] 监控{Total} 行情{QuoteOk}/{Total} MA5有效{Ma5Ok}/{Total} 低于MA5 0只（全部站上或数据不足）",
                stockCodes.Count, quoteOk, ma5Ok);
            return;
        }

        // 合并为一条提醒，展示 20 秒
        var content = $"注意注意，快收盘了以下股票还没站上五日均线，可能触发卖出条件哦：\n\n{string.Join("\n", alerts)}\n\n💡 5日均线(MA5)是短期趋势参考，收盘价未站上MA5意味着短期偏弱。";
        _petStore.AddReminder(new ReminderRequest
        {
            Type = "signal",
            Level = ReminderLevel.Alert,
            Title = $"⚠️ {alerts.Count}只股票未站上5日均线",
            Content = content,
            Importance = 5,
            DurationMs = 20000
        });

        _preCloseMA5State.LastReminderAt = NowMs;
        Log.Information("[MA5检查] {Count}只股票低于MA5，已合并播报（5分钟后复查）", alerts.Count);
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

    // ============================================================================
    // 空闲心得提醒 - 对应 planScheduler.js showIdleInsight
    // ============================================================================

    /// <summary>
    /// 空闲时显示随机心得提醒
    /// </summary>
    private async Task ShowIdleInsightAsync()
    {
        var now = Now;

        // 每 30 分钟最多一次
        if ((now - _lastIdleInsightTime).TotalMinutes < 30) return;
        _lastIdleInsightTime = now;

        var insights = new[]
        {
            "交易不在多，在于精。宁可不交易，也不要随意交易。",
            "止损是交易的第一课，学会止损才能在市场中生存。",
            "不要试图抓住每一个机会，只做属于自己的交易。",
            "趋势是你的朋友，不要逆势而为。",
            "仓位管理比选股更重要，控制风险永远是第一位。",
            "盘后复盘是进步最快的方式，坚持每日总结。",
            "不要因为一次亏损就否定自己的策略，也不要因为一次盈利就盲目自信。",
            "市场永远是对的，不要和市场争辩。",
            "耐心等待机会，机会是等出来的，不是追出来的。",
            "交易是一场马拉松，不是百米冲刺，保持节奏很重要。"
        };

        var idx = new Random().Next(insights.Length);
        _petStore.AddReminder(new ReminderRequest
        {
            Type = "idle_insight",
            Level = ReminderLevel.Info,
            Title = "交易心得",
            Content = insights[idx],
            Importance = 1,
            DurationMs = 8000
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 市场摘要播报 - 对应 planScheduler.js showMarketDigest
    // ============================================================================

    /// <summary>
    /// 市场摘要播报（周末/节假日每日一次）
    /// </summary>
    private async Task ShowMarketDigestAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        var digestKey = $"pet_market_digest_{todayStr}";
        if (_signalStates.ContainsKey(digestKey)) return;

        var prevTradingDay = _marketTime.FormatDate(_marketTime.GetPreviousTradingDay(Now));
        var prevPlans = _tradePlanStore.Plans.Where(p => p.PlanDate == prevTradingDay).ToList();
        var executed = prevPlans.Count(p => p.ExecutionStatus == "executed");

        var dailyPicks = await LoadLatestTradingDayPicksAsync();

        var sections = new List<string>
        {
            "市场休市摘要",
            $"上一交易日：{prevTradingDay}"
        };

        if (prevPlans.Count > 0)
        {
            sections.Add($"计划执行：{executed}/{prevPlans.Count} 已完成");
        }

        if (dailyPicks.Count > 0)
        {
            sections.Add($"擒牛 {dailyPicks.Count} 只：");
            var pickNames = string.Join("\n", dailyPicks.Take(5).Select(p => $"  {p.StockName}({p.StockCode})"));
            sections.Add(pickNames);
        }

        var nextTradingDay = _marketTime.FormatDate(_marketTime.GetNextTradingDay(Now));
        var nextPlans = _tradePlanStore.Plans.Where(p =>
            p.PlanDate == nextTradingDay &&
            p.ExecutionStatus != "executed" &&
            p.ExecutionStatus != "cancelled").ToList();

        if (nextPlans.Count > 0)
        {
            sections.Add($"下一交易日（{nextTradingDay}）");
            sections.Add($"待执行计划 {nextPlans.Count} 条");
        }
        else
        {
            sections.Add($"下一交易日（{nextTradingDay}）暂无计划");
        }

        sections.Add("休息日适合复盘和总结，有空可以回顾一下心得。");

        _petStore.AddReminder(new ReminderRequest
        {
            Id = $"market_digest_{todayStr}",
            Type = "market_digest",
            Level = ReminderLevel.Hint,
            Title = "休市摘要",
            Content = string.Join("\n", sections),
            Importance = 2
        });

        _signalStates[digestKey] = new SignalStateEntry { State = "triggered", At = NowMs };
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

    // ============================================================================
    // 周末总结 - 对应 planScheduler.js showWeekendSummary
    // ============================================================================

    /// <summary>
    /// 显示周末总结
    /// </summary>
    private void ShowWeekendSummary()
    {
        var weekStart = GetWeekStart();
        var weekEnd = GetWeekEnd();
        var weekPlans = _tradePlanStore.Plans
            .Where(p => string.Compare(p.PlanDate, weekStart, StringComparison.Ordinal) >= 0 &&
                        string.Compare(p.PlanDate, weekEnd, StringComparison.Ordinal) <= 0)
            .ToList();

        var executed = weekPlans.Count(p => p.ExecutionStatus == "executed");
        var partial = weekPlans.Count(p => p.ExecutionStatus == "partial");
        var notExecuted = weekPlans.Count(p => p.ExecutionStatus == "not_executed");
        var cancelled = weekPlans.Count(p => p.ExecutionStatus == "cancelled");

        var content = $"本周交易总结\n\n本周共制定 {weekPlans.Count} 条计划\n";
        content += $"已执行：{executed} 条\n";
        if (partial > 0) content += $"部分执行：{partial} 条\n";
        content += $"未执行：{notExecuted} 条\n";
        if (cancelled > 0) content += $"已取消：{cancelled} 条\n";
        content += executed > 0 ? "\n继续保持，下周加油！" : "\n下周可以多制定一些计划哦！";

        var todayStr = _marketTime.FormatDate(Now);
        _petStore.AddReminder(new ReminderRequest
        {
            Id = $"weekend_summary_{todayStr}",
            Type = "weekend_summary",
            Level = ReminderLevel.Hint,
            Title = "本周交易总结",
            Content = content,
            Importance = 3
        });
    }

    private string GetWeekStart()
    {
        var now = Now;
        var day = (int)now.DayOfWeek;
        if (day == 0) day = 7; // 周日 = 7
        var monday = now.AddDays(-(day - 1));
        return _marketTime.FormatDate(monday);
    }

    private string GetWeekEnd()
    {
        var now = Now;
        var day = (int)now.DayOfWeek;
        if (day == 0) day = 7;
        var sunday = now.AddDays(7 - day);
        return _marketTime.FormatDate(sunday);
    }

    // ============================================================================
    // 冷启动回放补全 - 对应 planScheduler.js backfillTodayEvents
    // ============================================================================

    /// <summary>
    /// 冷启动回放补全 - 补全当天遗漏的事件
    /// </summary>
    private async Task BackfillTodayEventsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastBackfillDate == todayStr) return;
        _lastBackfillDate = todayStr;

        try
        {
            // 从数据库加载今日已有的快照到内存缓存
            using var conn = _db.CreateConnection();
            const string sql = @"
                SELECT stockCode AS StockCode, price AS Price, volume AS Volume,
                       amount AS Amount, timestamp AS TimestampStr, vwap AS Vwap, volumeReliable AS VolumeReliable,
                       cumulativeVolume AS CumulativeVolumeRaw
                FROM price_snapshots
                WHERE date(timestamp) = @Today
                ORDER BY timestamp";

            var rows = conn.Query<dynamic>(sql, new { Today = todayStr }).ToList();

            // 旧版快照 volume 列存的是当日累计量；按相邻行差分换算成区间量（对齐新版语义）
            var lastCumulativeByCode = new Dictionary<string, long>();

            foreach (var row in rows)
            {
                var code = (string)row.StockCode;
                var rawVolume = (long)row.Volume;
                var cumulative = row.CumulativeVolumeRaw != null && row.CumulativeVolumeRaw != DBNull.Value
                    ? (long)row.CumulativeVolumeRaw
                    : rawVolume; // 旧行降级：volume 即累计量
                var interval = lastCumulativeByCode.TryGetValue(code, out var prevCum) && cumulative >= prevCum
                    ? cumulative - prevCum
                    : 0;
                lastCumulativeByCode[code] = cumulative;

                var snapshot = new PriceSnapshot
                {
                    StockCode = code,
                    Price = (decimal)row.Price,
                    Volume = interval,
                    CumulativeVolume = cumulative,
                    Amount = (decimal)row.Amount,
                    Timestamp = DateTime.Parse((string)row.TimestampStr, CultureInfo.InvariantCulture),
                    Vwap = (decimal)row.Vwap,
                    VolumeReliable = (bool)row.VolumeReliable
                };

                var cache = _snapshotCache.GetOrAdd(code, _ => new List<PriceSnapshot>());
                lock (cache)
                {
                    cache.Add(snapshot);
                }
            }

            if (rows.Count > 0)
            {
                Log.Information("[计划调度] 冷启动回放补全：加载 {Count} 条今日快照", rows.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 冷启动回放补全失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 今日信号评估 - 对应 planScheduler.js evaluateTodaySignals
    // ============================================================================

    /// <summary>
    /// 今日信号评估 - 盘后调用，评估今日所有信号的质量
    /// </summary>
    private async Task EvaluateTodaySignalsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastEvaluateDate == todayStr) return;
        _lastEvaluateDate = todayStr;

        try
        {
            // 收集所有有快照的股票
            // 注意：枚举 _snapshotCache 的 List 必须持有该 List 的锁——
            // 交易时段 10 秒 tick 会在锁内 cache.Add，无锁 Where 枚举会抛"集合已修改"
            var allSnapshots = new Dictionary<string, List<PriceSnapshot>>();
            foreach (var (code, snaps) in _snapshotCache)
            {
                List<PriceSnapshot> todaySnaps;
                lock (snaps)
                {
                    todaySnaps = snaps.Where(s => _marketTime.FormatDate(s.Timestamp) == todayStr).ToList();
                }
                if (todaySnaps.Count > 0)
                {
                    allSnapshots[code] = todaySnaps;
                }
            }

            // 委托给信号事件存储评估
            _signalEventStore.EvaluateTodaySignals(allSnapshots);

            Log.Information("[计划调度] 今日信号评估完成：{Count} 只股票", allSnapshots.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 今日信号评估失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 信号自进化 - 对应 planScheduler.js autoOptimizeParams / runEvolutionSearch 等
    // ============================================================================

    /// <summary>
    /// 自动优化参数 - 对应 planScheduler.js autoOptimizeParams
    /// 盘后自动执行因子权重 + 信号乘子优化
    /// </summary>
    public async Task AutoOptimizeParamsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastAutoOptimizeDate == todayStr) return;

        // 持久化去重（跨重启）：应用重启后内存变量清零会导致盘后每次启动都
        // 重新执行自进化并重复推送"自进化报告"提醒，故以 appConfig 记录当日已执行
        try
        {
            using var conn = _db.CreateConnection();
            var savedDate = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_auto_optimize_date'");
            if (savedDate == todayStr)
            {
                _lastAutoOptimizeDate = todayStr;
                return;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }

        // 仅在盘后执行
        var hours = _marketTime.GetHours(Now);
        if (hours < 15 || hours >= 20) return;

        _lastAutoOptimizeDate = todayStr;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_last_auto_optimize_date", todayStr);
        }
        catch { /* ignore */ }

        try
        {
            // 1. 先评估今日信号
            await EvaluateTodaySignalsAsync();

            // 2. 因子权重优化
            var factorChanges = OptimizeFactorWeights();

            // 3. 信号乘子优化
            var signalChanges = OptimizeSignalWeights();

            // 4. 进化搜索
            var searchResult = await RunEvolutionSearchAsync();

            // 5. 漏报复活
            var resurrected = ResurrectMutedFromMissed();

            // 6. 显示自进化报告
            if (factorChanges.Count > 0 || signalChanges.Count > 0 || searchResult.Improved || resurrected.Count > 0)
            {
                ShowSelfEvolutionReport(factorChanges, signalChanges, searchResult, resurrected);
            }

            // 7. 持久化参数
            SaveAutoOptimizedParams();

            Log.Information("[计划调度] 自进化完成：因子变更 {FactorCount}，信号变更 {SignalCount}，复活 {ResurrectCount}",
                factorChanges.Count, signalChanges.Count, resurrected.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 自进化失败");
        }
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
                    newWeights["maPressure"] = Math.Min(0.35m, currentWeights.GetValueOrDefault("maPressure") * 1.15m);
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
                        OldWeight = Math.Round(oldW, 4),
                        NewWeight = Math.Round(newW, 4),
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
                        OldMultiplier = Math.Round(oldM, 3),
                        NewMultiplier = Math.Round(newM, 3),
                        Direction = newM > oldM ? "up" : "down",
                        WinRate = Math.Round(winRate, 3),
                        AvgReward = Math.Round(avgReward, 3),
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
    private void ShowSelfEvolutionReport(
        List<FactorChange> factorChanges,
        List<SignalChange> signalChanges,
        EvolutionSearchResult searchResult,
        List<string> resurrected)
    {
        var sections = new List<string> { "自进化报告" };

        if (factorChanges.Count > 0)
        {
            sections.Add($"因子权重调整({factorChanges.Count}项):");
            sections.AddRange(factorChanges.Select(c =>
                $"  {c.Factor}: {c.OldWeight}→{c.NewWeight}({c.Direction})"));
        }

        if (signalChanges.Count > 0)
        {
            sections.Add($"信号乘子调整({signalChanges.Count}项):");
            sections.AddRange(signalChanges.Select(c =>
                $"  {c.SignalLabel}: {c.OldMultiplier}→{c.NewMultiplier}({c.Direction}, {c.Reason})"));
        }

        if (searchResult.Improved)
        {
            sections.Add($"进化搜索: 评分 {searchResult.OldScore:F1}→{searchResult.NewScore:F1}(+{searchResult.NewScore - searchResult.OldScore:F1})");
        }

        if (resurrected.Count > 0)
        {
            sections.Add($"漏报复活({resurrected.Count}项): {string.Join(", ", resurrected)}");
        }

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "self_evolution",
            Level = ReminderLevel.Info,
            Title = "自进化报告",
            Content = string.Join("\n", sections),
            Importance = 2,
            DurationMs = 10000
        });
    }

    // ============================================================================
    // 参数持久化 - 对应 planScheduler.js loadAutoOptimizedParams / loadOptimizedParams / _syncOptimizedParams
    // ============================================================================

    /// <summary>
    /// 加载自动优化持久化参数（启动时调用）
    /// </summary>
    private void LoadAutoOptimizedParams()
    {
        try
        {
            // 从数据库加载（对应 JS 的 localStorage）
            using var conn = _db.CreateConnection();

            // 快速拉升阈值已排除在自进化之外（对齐 Electron v2：阈值由 Config.RapidWindows 硬编码，
            // signalEvents 统计跳过 rapid_ 前缀，不参与自动调整）。
            // 旧版本曾把阈值调至 0.65%/0.98%（默认 1%/2%）并持久化，导致小波动触发提醒
            // 并开启长冷却，压制后续真正的大幅拉升信号。版本号不匹配时删除持久化值（对齐
            // Electron localStorage.removeItem 语义，旧实现只写版本号不清值，下次启动版本匹配
            // 又把毒值加载回来），确保代码内默认阈值永久生效。
            const int rapidThresholdVersion = 3;
            var savedRapidVersion = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_auto_optimized_rapid_version'");
            if (savedRapidVersion != rapidThresholdVersion.ToString())
            {
                conn.Execute("DELETE FROM appConfig WHERE key = 'pet_auto_optimized_rapid_windows'");
                SaveConfig(conn, "pet_auto_optimized_rapid_version", rapidThresholdVersion.ToString());
                Log.Information("[自进化] 快速拉升阈值版本升级至 v{Version}，已清除旧持久化值（阈值硬编码不参与自动调整）",
                    rapidThresholdVersion);
            }

            // 卖点配置
            var sellRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_auto_optimized_sell'");
            if (!string.IsNullOrEmpty(sellRaw))
            {
                var savedSell = JsonConvert.DeserializeObject<dynamic>(sellRaw);
                if (savedSell?.stagnantThreshold != null)
                {
                    // 更新卖点检测器配置
                    _sellPointDetector.UpdateConfig(new
                    {
                        stagnantThreshold = (decimal)savedSell.stagnantThreshold
                    });
                    Log.Information("[自进化] 已加载历史优化的放量滞涨阈值");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 加载历史参数失败");
        }
    }

    /// <summary>
    /// 加载优化参数
    /// </summary>
    private void LoadOptimizedParams()
    {
        try
        {
            using var conn = _db.CreateConnection();

            // 信号乘子
            var multRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_optimized_signal_multipliers'");
            if (!string.IsNullOrEmpty(multRaw))
            {
                var multipliers = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(multRaw);
                if (multipliers != null)
                {
                    _sellPointDetector.UpdateSignalMultipliers(multipliers);
                    Log.Information("[自进化] 已加载信号乘子参数");
                }
            }

            // 因子权重
            var weightRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_optimized_factor_weights'");
            if (!string.IsNullOrEmpty(weightRaw))
            {
                var weights = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(weightRaw);
                if (weights != null)
                {
                    _multiFactorEngine.UpdateWeights(weights);
                    Log.Information("[自进化] 已加载因子权重参数");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 加载优化参数失败");
        }
    }

    /// <summary>
    /// 保存自动优化参数到数据库
    /// </summary>
    private void SaveAutoOptimizedParams()
    {
        try
        {
            using var conn = _db.CreateConnection();

            // 快速拉升阈值不持久化（对齐 Electron v2：硬编码不参与自动调整，
            // 保存只会让历史污染值无限延续）
            // 保存信号乘子
            var multJson = JsonConvert.SerializeObject(_sellPointDetector.GetSignalMultipliers());
            SaveConfig(conn, "pet_optimized_signal_multipliers", multJson);

            // 保存因子权重
            var weightJson = JsonConvert.SerializeObject(_multiFactorEngine.GetWeights());
            SaveConfig(conn, "pet_optimized_factor_weights", weightJson);

            Log.Information("[自进化] 参数已持久化");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 保存参数失败");
        }
    }

    /// <summary>
    /// 同步优化参数 - 对应 planScheduler.js _syncOptimizedParams
    /// 优化参数优先级高于用户设置
    /// </summary>
    private void SyncOptimizedParams()
    {
        // 快速拉升窗口配置已在 Config 中，直接使用
        // 信号乘子和因子权重已通过 LoadOptimizedParams 加载到检测器中
    }

    /// <summary>
    /// 确保 price_snapshots 表存在
    /// </summary>
    private void EnsureSnapshotTable()
    {
        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                CREATE TABLE IF NOT EXISTS price_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stockCode TEXT NOT NULL,
                    price REAL NOT NULL,
                    volume INTEGER,
                    amount REAL,
                    timestamp TEXT NOT NULL,
                    vwap REAL,
                    volumeReliable INTEGER DEFAULT 1,
                    cumulativeVolume INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_snapshots_code_time ON price_snapshots(stockCode, timestamp);";
            conn.Execute(sql);

            // 旧库升级：补 cumulativeVolume 列（已存在时报 duplicate column，忽略）
            try
            {
                conn.Execute("ALTER TABLE price_snapshots ADD COLUMN cumulativeVolume INTEGER");
                Log.Information("[计划调度] price_snapshots 表已升级：新增 cumulativeVolume 列");
            }
            catch { /* 列已存在 */ }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 创建 price_snapshots 表失败");
        }
    }

    /// <summary>
    /// 保存配置项到 appConfig 表
    /// </summary>
    private static void SaveConfig(SqliteConnection conn, string key, string value)
    {
        const string sql = @"
            INSERT OR REPLACE INTO appConfig (key, value) VALUES (@Key, @Value)";
        conn.Execute(sql, new { Key = key, Value = value });
    }

    // ============================================================================
    // 富途推送 - 对应 planScheduler.js _bindFutuPush / _onFutuPush / _runPushDrivenDetect / _ensureFutuSubscription
    // ============================================================================

    /// <summary>
    /// 确保富途订阅 - 对应 planScheduler.js _ensureFutuSubscription
    /// 调用 FutuAdapter 连接 OpenD + 订阅计划内股票
    /// </summary>
    private async Task EnsureFutuSubscriptionAsync()
    {
        if (_futuAdapter == null) return;  // 未注入富途适配器，降级到 HTTP 轮询

        // 期望订阅集合（今日 + 持仓过夜，含备份导入的旧日期计划）
        var stockCodes = _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans)
            .Where(IsPlanMonitorable)
            .Select(p => p.StockCode)
            .Distinct()
            .ToList();

        // 已连接且全部覆盖：无需操作（盘中新添计划在此发现缺口 → 下个 tick 增量补订）
        if (_futuSubscribed && _futuAdapter.IsConnected)
        {
            var subscribed = _futuAdapter.GetSubscribedCodes();
            if (stockCodes.All(subscribed.Contains)) return;
        }

        var now = Now;
        var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _futuSubscribeRetryCount)));
        if (now - _lastFutuSubscribeAttempt < retryDelay) return;

        _lastFutuSubscribeAttempt = now;
        _futuSubscribeRetryCount++;

        try
        {
            // 1. 连接 OpenD
            if (!_futuAdapter.IsConnected)
            {
                var ok = _futuAdapter.Connect();
                if (!ok)
                {
                    Log.Warning("[计划调度] 富途 OpenD 连接失败(重试 {Count})", _futuSubscribeRetryCount);
                    return;
                }
            }

            // 2. 绑定推送/连接事件（只绑一次；重订时不重复挂处理器）
            if (!_futuHandlerBound)
            {
                _futuAdapter.OnQuotePush += OnFutuPush;
                _futuAdapter.OnConnectionChanged += OnFutuConnectionChanged;
                _futuHandlerBound = true;
                Log.Information("[计划调度] 富途推送回调已绑定");
            }

            // 3. 增量订阅（adapter 内部按已订阅集合去重，只发缺失代码）
            if (stockCodes.Count > 0)
            {
                if (_futuAdapter.Subscribe(stockCodes))
                {
                    _futuSubscribed = true;
                    _futuSubscribeRetryCount = 0;
                    Log.Information("[计划调度] 富途实时推送已订阅 {Count} 只股票", stockCodes.Count);
                }
                else
                {
                    // 发送失败（连接未就绪）：保留 _futuSubscribed=false，下个 tick 按
                    // 退避节奏重试（对齐 Electron _scheduleFutuRetry 自愈）
                    Log.Warning("[计划调度] 富途订阅发送失败(重试 {Count})", _futuSubscribeRetryCount);
                }
            }
            else
            {
                _futuSubscribed = true; // 无监控股票视为订阅完成，避免每 tick 重复尝试
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 富途订阅失败(重试 {Count})", _futuSubscribeRetryCount);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 富途连接/订阅状态变更回调：断开或订阅失败时复位标记，
    /// 由交易时段每个 tick 的 EnsureFutuSubscriptionAsync 自动重连重订（对齐 Electron closed/error 自愈）
    /// </summary>
    private void OnFutuConnectionChanged(bool connected)
    {
        if (connected) return;
        if (_futuSubscribed)
        {
            _futuSubscribed = false;
            Log.Warning("[计划调度] 富途连接/订阅中断，等待交易tick自动重连重订");
        }
    }

    /// <summary>
    /// 绑定富途推送 - 已在 EnsureFutuSubscriptionAsync 中内联实现
    /// </summary>
    private void BindFutuPush()
    {
        // 推送回调绑定逻辑已移入 EnsureFutuSubscriptionAsync
    }

    /// <summary>
    /// 富途推送回调 - 对应 planScheduler.js _onFutuPush
    /// 直接受秒级推送价格触发信号检测，不走 HTTP 重新拉取
    /// </summary>
    public void OnFutuPush(string stockCode, decimal price, long volume, decimal amount)
    {
        var now = Now;
        var cacheTtl = TimeSpan.FromMilliseconds(Math.Max(3000, _settingsStore.Settings.RefreshIntervalMs));

        // 更新/创建缓存中的行情（推送数据直接写入，不回头走 HTTP）
        StockQuote quote;
        if (_batchQuoteCache.TryGetValue(stockCode, out var cached))
        {
            quote = cached.Data;
            quote.CurrentPrice = price;
            quote.Volume = volume;
            quote.Amount = amount;
            quote.DateTime = now;
        }
        else
        {
            quote = new StockQuote
            {
                Code = stockCode,
                CurrentPrice = price,
                Volume = volume,
                Amount = amount,
                DateTime = now
            };
        }
        _batchQuoteCache[stockCode] = (quote, now.Add(cacheTtl));

        // 原地更新该股最新快照的价格（不追加新快照，对齐 Electron _onFutuPush：
        // 保持快照节奏由 10 秒 tick 主导，避免推送把多快照脉冲窗口压缩成几秒钟）
        var snapCache = _snapshotCache.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
        lock (snapCache)
        {
            if (snapCache.Count > 0)
            {
                var last = snapCache[^1];
                last.Price = price;
                // 秒级轨迹：维护本采样区间内的高低点。若只存区间末价，秒级冲高会被
                // 随后的回落覆盖，双顶提前预警看到的"反弹高点"失真（对齐 Electron）
                if (last.High == 0 || price > last.High) last.High = price;
                if (last.Low == 0 || price < last.Low) last.Low = price;
                // 富途推送的 amount/volume 即当日累计额/量 → 真实 VWAP；失败保留原均价
                if (volume > 0 && amount > 0)
                {
                    last.Vwap = amount / volume;
                }
            }
        }

        // 直接触发推送驱动检测（对齐 Electron：空闲立即检测；检测中标记 trailing 补跑）
        if (_pushDetectRunning.ContainsKey(stockCode))
        {
            _pushDetectQueued[stockCode] = 1;
        }
        else
        {
            _ = RunPushDrivenDetectAsync(stockCode, quote);
        }
    }

    /// <summary>
    /// 推送驱动检测 - 对应 planScheduler.js _runPushDrivenDetect
    /// 按股票防重入（检测为纯计算毫秒级，无节流）；trailing 补跑保证
    /// 检测执行期间到达的新价格不丢（检测完成后立即用最新价再跑一轮）
    /// </summary>
    private async Task RunPushDrivenDetectAsync(string stockCode, StockQuote pushQuote)
    {
        if (!_pushDetectRunning.TryAdd(stockCode, 1)) return; // 已在检测中：仅排队

        try
        {
            await DetectForStockAsync(stockCode, pushQuote);

            // trailing 补跑：检测期间有新推送到达 → 用缓存中的最新价立即再跑
            while (_pushDetectQueued.TryRemove(stockCode, out _))
            {
                var latest = pushQuote;
                if (_batchQuoteCache.TryGetValue(stockCode, out var cached) && cached.Data.CurrentPrice > 0)
                {
                    latest = cached.Data; // 检测期间到达的推送持续覆盖缓存
                }
                await DetectForStockAsync(stockCode, latest);
            }
        }
        finally
        {
            _pushDetectRunning.TryRemove(stockCode, out _);
        }
    }

    /// <summary>单股票增量检测（推送价直供，绕过 HTTP 轮询/缓存）</summary>
    private async Task DetectForStockAsync(string stockCode, StockQuote quote)
    {
        // 查找该股票的计划（今日 + 持仓过夜，含备份导入的旧日期计划）
        var plans = _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans)
            .Where(p => p.StockCode == stockCode && IsPlanMonitorable(p))
            .ToList();

        if (plans.Count == 0) return;
        if (quote == null || quote.CurrentPrice <= 0) return;

        foreach (var plan in plans)
        {
            // 检测期间计划可能已被执行/取消
            if (!IsPlanMonitorable(plan)) continue;
            await CheckPlanSignals(plan, quote);
        }
    }

    /// <summary>
    /// 清理富途订阅 - 对应 planScheduler.js _cleanupFutuSubscriptionAfterClose
    /// </summary>
    private void CleanupFutuSubscriptionAfterClose()
    {
        if (!_futuSubscribed) return;

        // 解绑推送/连接回调
        if (_futuAdapter != null)
        {
            _futuAdapter.OnQuotePush -= OnFutuPush;
            _futuAdapter.OnConnectionChanged -= OnFutuConnectionChanged;
        }

        _futuSubscribed = false;
        _futuHandlerBound = false;
        _pushDetectRunning.Clear();
        _pushDetectQueued.Clear();
        Log.Information("[计划调度] 富途订阅已清理");
    }

    // ============================================================================
    // 辅助方法
    // ============================================================================

    /// <summary>
    /// 计划是否可监控 - 未执行/未取消且在监控日期范围内
    /// </summary>
    private bool IsPlanMonitorable(TradePlan plan)
    {
        if (plan.ExecutionStatus == "executed") return false;
        if (plan.ExecutionStatus == "cancelled") return false;
        if (plan.Status == "cancelled") return false;
        return true;
    }

    /// <summary>
    /// 是否显示盘前提醒（当日只提醒一次）
    /// </summary>
    private bool ShouldShowPreMarketReminder()
    {
        var today = _marketTime.FormatDate(Now);
        if (_lastPreMarketReminderDate == today) return false;
        try
        {
            using var conn = _db.CreateConnection();
            var saved = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_pre_market_reminder_date'");
            if (saved == today)
            {
                _lastPreMarketReminderDate = today;
                return false;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }
        return true;
    }

    /// <summary>
    /// 是否显示非交易日提醒
    /// </summary>
    private bool ShouldShowNonTradingDayReminder()
    {
        var today = _marketTime.FormatDate(Now);
        if (_lastNonTradingDayReminderDate == today) return false;
        try
        {
            using var conn = _db.CreateConnection();
            var saved = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_non_trading_day_reminder_date'");
            if (saved == today)
            {
                _lastNonTradingDayReminderDate = today;
                return false;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }
        return true;
    }

    /// <summary>
    /// 计划类型文本
    /// </summary>
    private static string PlanTypeText(string planType)
    {
        return planType switch
        {
            "buy" => "买入",
            "sell" => "卖出",
            "watch" => "数据收集",
            _ => planType
        };
    }

    /// <summary>
    /// 数值是否有效
    /// </summary>
    private static bool IsFinite(decimal value) => !double.IsInfinity((double)value) && !double.IsNaN((double)value);

    /// <summary>
    /// 从信号信息中提取当前价（辅助方法）
    /// </summary>
    private static decimal data_currentPrice(SellSignalInfo signal) => signal.TotalScore;

    // ============================================================================
    // 盘后状态持久化
    // ============================================================================

    private void SaveAfterMarketNotified(AfterMarketNotifiedState state)
    {
        _afterMarketNotified = state;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_after_market_notified", JsonConvert.SerializeObject(state));
        }
        catch { /* ignore */ }
    }

    private AfterMarketNotifiedState LoadAfterMarketNotified()
    {
        try
        {
            using var conn = _db.CreateConnection();
            var raw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_after_market_notified'");
            if (!string.IsNullOrEmpty(raw))
            {
                return JsonConvert.DeserializeObject<AfterMarketNotifiedState>(raw) ?? new AfterMarketNotifiedState();
            }
        }
        catch { /* ignore */ }
        return _afterMarketNotified;
    }

    private void ClearAfterMarketSnooze()
    {
        _afterMarketSnoozeUntil = 0;
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute("DELETE FROM appConfig WHERE key = 'pet_after_market_snooze_until'");
        }
        catch { /* ignore */ }
    }

    private void SaveAfterMarketLastReminder(long timestamp)
    {
        _afterMarketLastReminder = timestamp;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_after_market_last_reminder", timestamp.ToString());
        }
        catch { /* ignore */ }
    }
}
