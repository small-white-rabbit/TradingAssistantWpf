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
        // 快照节奏 SnapshotIntervalSec=10s，按用户要求：3/10/15 分钟窗口对应 ≥1%/≥2%/≥3% 触发。
        // 18/60/90 bars ≈ 3/10/15 分钟，涨幅或跌幅满足即触发上涨/下跌提醒。
        new() { Bars = 18, Pct = 1.0m, Label = "脉冲", CooldownMs = 5 * 60 * 1000 },
        new() { Bars = 60, Pct = 2.0m, Label = "中速", CooldownMs = 10 * 60 * 1000 },
        new() { Bars = 90, Pct = 3.0m, Label = "慢牛", CooldownMs = 15 * 60 * 1000 }
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
