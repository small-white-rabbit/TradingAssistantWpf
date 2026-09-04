using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.MarketData;

namespace StockReview.Core.Engines;

// ==================== Constants ====================

/// <summary>
/// 信号类型常量（对应 JS SIGNAL_TYPES）
/// </summary>
public static class SignalTypes
{
    public const string SurgePullback = "surge_pullback";
    public const string VolumeStagnant = "volume_stagnant";
    public const string MaSuppress = "ma_suppress";
    public const string TopDivergence = "top_divergence";
    public const string VolumeDivergence = "volume_divergence";
    public const string DoubleTop = "double_top";
    public const string FishingLine = "fishing_line";
    public const string TripleTop = "triple_top";
    public const string PlatformBreakdown = "platform_breakdown";
    public const string HighDeviationPullback = "high_deviation_pullback";
    public const string VwapBreakdown = "vwap_breakdown";
    public const string VwapRejection = "vwap_rejection";
    public const string VwapSlopeDown = "vwap_slope_down";
    public const string LateSessionExit = "late_session_exit";
    public const string BreakMa5 = "break_ma5";
    public const string BreakMa10 = "break_ma10";
    public const string BreakMa30 = "break_ma30";
    public const string BreakSupport = "break_support";
    public const string WeakReboundFailure = "weak_rebound_failure";
    public const string DeepDropRebound = "deep_drop_rebound";
    public const string SpikeVolumeTop = "spike_volume_top";
    public const string AtrStopLoss = "atr_stop_loss";
    public const string AtrTakeProfit = "atr_take_profit";
    public const string AtrTrailingStop = "atr_trailing_stop";
    public const string TimeStop = "time_stop";
    public const string MultifactorResonance = "multifactor_resonance";
}

/// <summary>
/// 信号静音阈值：乘子 ≤ 此值的类型被历史验证为噪声，评分与提醒全链路剔除
/// </summary>
public static class SellPointConstants
{
    public const double SignalMuteThreshold = 0.35;
}

// ==================== Config ====================

/// <summary>
/// 卖点检测器默认配置（实战调优版，对应 JS DEFAULT_CONFIG）
/// </summary>
public class SellPointDetectorConfig
{
    // 冲高回落
    public double SurgePullbackThreshold { get; set; } = 1.8;
    public int SurgeFastSpan { get; set; } = 3;
    public double SurgeFastMinRisePct { get; set; } = 1.2;
    public double PullbackRatio { get; set; } = 0.35;
    public double VolumeAmplifyMultiple { get; set; } = 1.6;
    public double StagnantThreshold { get; set; } = 0.5;
    public int MaSuppressCandles { get; set; } = 2;
    public double VolumeDivergenceShrinkRatio { get; set; } = 0.4;

    // 支撑位
    public double SupportBreakdownTolerance { get; set; } = 1;
    public double SupportMinDistancePct { get; set; } = 0.5;
    public double SupportBreakdownMinPct { get; set; } = 0.15;
    public double PriceNearThreshold { get; set; } = 1;

    // 双顶
    public double DoubleTopTolerance { get; set; } = 0.8;
    public double DoubleTopRightMaxExceedPct { get; set; } = 0.05;
    public double DoubleTopPreVTrendMaxDrop { get; set; } = 1.0;
    public int DoubleTopPreWindow { get; set; } = 30;
    public double DoubleTopRightDropMin { get; set; } = 0.5;
    public double DoubleTopNeckDepthLow { get; set; } = 1.5;
    public double DoubleTopNeckDepthMid { get; set; } = 1.2;
    public double DoubleTopNeckDepthHigh { get; set; } = 0.8;
    public double DoubleTopNeckDepthPriceLow { get; set; } = 5;
    public double DoubleTopNeckDepthPriceMid { get; set; } = 10;
    public double DoubleTopMinProminence { get; set; } = 0.8;
    public double DoubleTopLeftDropAfterMin { get; set; } = 0.5;
    public int DoubleTopLeftDropAfterBars { get; set; } = 5;
    /// <summary>
    /// 颈线深度的日内波幅比例下限（2026-09-04 一博科技误报修复）：
    /// 颈线深度还须 ≥ 日内波幅×该比例（与价格档位下限取较大者）。
    /// 高波动股（如日内4%波幅）的 0.8% 档位下限只是整理平台噪音，
    /// 不构成有效颈线；要求颈线深度达到波幅1/3以上才算"真回调"。
    /// 低波幅横盘股仍由价格档位下限主导，行为不变。
    /// </summary>
    public double DoubleTopNeckDepthVolRatio { get; set; } = 0.35;
    /// <summary>
    /// 两顶最小真实时间间隔（分钟，2026-09-04 一博科技误报修复）：
    /// 根数下限（5根）与快照推送频率耦合，10秒粒度下仅25秒，无约束力。
    /// 早盘急拉后的第一个整理平台（两顶间隔4分钟级别）是上升中继而非双头。
    /// 经典日内双顶两顶间隔至少15-30分钟，5分钟为绝对下限。
    /// </summary>
    public double DoubleTopMinPeakGapMinutes { get; set; } = 5.0;

    // 双顶提前预警
    public bool EnableDoubleTopEarly { get; set; } = true;
    public double DoubleTopEarlyApproachPct { get; set; } = 0.7;
    public double DoubleTopEarlyRejectPct { get; set; } = 0.3;
    public double DoubleTopEarlyVolRatioMax { get; set; } = 0.6;
    public int DoubleTopEarlyMaxAgeBars { get; set; } = 10;
    public double DoubleTopEarlySimilarityMin { get; set; } = 0.45;

    // 钓鱼线
    public double FishingLineSurgePct { get; set; } = 2.5;
    public double FishingLineSurgeSlope { get; set; } = 0.5;
    public int FishingLineSpan { get; set; } = 5;
    public double FishingLinePullbackRatio { get; set; } = 0.4;
    public double FishingLineDownVolShrink { get; set; } = 0.8;

    // 均价线距离
    public double AvgPriceDistancePct { get; set; } = 1.0;

    // 三重顶
    public double TripleTopTolerance { get; set; } = 0.5;
    public double TripleTopPullback { get; set; } = 0.25;

    // 平台
    public double PlatformAmplitude { get; set; } = 1.5;
    // 平台窗口根数（10 秒/根，180 ≈ 30 分钟）：对齐平台典型持续 5 分钟-1 小时的经验区间，
    // 窗口越长，仅存几分钟的高位台阶越无法填满窗口冒充"平台"；
    // 开盘阶段本无平台，快照数预热门槛（窗口+5 ≈ 31 分钟）无需担心
    public int PlatformCandles { get; set; } = 180;
    public double PlatformBreakdownPct { get; set; } = 0.25;
    // 平台下轨分位（%）：下轨取平台窗口价格的该分位数而非最低价，去极值防上下影毛刺拉偏边界
    public double PlatformLowerPercentile { get; set; } = 15;
    // 跌破时间确认：最近 N 个快照（10 秒/个，18 ≈ 3 分钟）持续低于下轨才确认跌破
    public int PlatformConfirmSnaps { get; set; } = 18;

    // 高乖离
    public double HighDeviationPct { get; set; } = 2.0;
    public double HighDeviationPullback { get; set; } = 0.8;

    // 顶部形态通用过滤
    public double TopPatternMinPosition { get; set; } = 0.3;
    public double TopPatternMaxVwapSlope { get; set; } = 0.03;
    public double TopPatternMinPreRisePct { get; set; } = 2.0;

    // 顶背离
    public double TopDivergenceMinRelHeight { get; set; } = 0.6;
    public double TopDivergenceDevShrinkRatio { get; set; } = 0.85;
    public double TopDivergenceNewHighPct { get; set; } = 0.5;
    public double TopDivergenceVolShrinkRatio { get; set; } = 0.7;

    // VWAP 跌破
    public int VwapBreakdownConfirm { get; set; } = 3;
    public int VwapBreakdownMaxElapsed { get; set; } = 30;
    public int VwapBreakdownRallyLookback { get; set; } = 60;
    public double VwapBreakdownRallyMinAbove { get; set; } = 0.5;
    public int VwapBreakdownOscLookback { get; set; } = 40;
    public double VwapBreakdownOscRange { get; set; } = 1.2;
    public int VwapBreakdownOscAboveMin { get; set; } = 3;
    public int VwapBreakdownOscCrossMin { get; set; } = 1;
    public double VwapBreakdownOscSlopeMax { get; set; } = 0.01;
    public double VwapBreakdownMinDecline { get; set; } = 0.5;
    public int VwapBreakdownDeclineLookback { get; set; } = 60;

    // VWAP 挡道/拐头
    public double VwapRejectionGap { get; set; } = 0.6;
    public int VwapRejectionConfirm { get; set; } = 2;
    public double VwapSlopeDownThreshold { get; set; } = -0.015;
    public int VwapSlopeDownCandles { get; set; } = 12;

    // 尾盘
    public string LateSessionStart { get; set; } = "14:30";
    public double LateSessionVolumeMultiple { get; set; } = 2;
    public double LateSessionBreakdownPct { get; set; } = 0.5;

    // 缩量反弹失败
    public int WeakReboundMaxScan { get; set; } = 25;
    public int WeakReboundBelowConfirm { get; set; } = 3;
    public double WeakReboundGapMin { get; set; } = -0.3;
    public double WeakReboundGapMax { get; set; } = 0.2;
    public double WeakReboundPullbackPct { get; set; } = 0.5;
    public double WeakReboundVolShrink { get; set; } = 0.65;
    public double WeakReboundVwapSlopeMax { get; set; } = 0.008;

    // 大跌反抽
    public double DeepDropMinPct { get; set; } = 5;
    public double DeepDropReboundMinPct { get; set; } = 2;
    public double DeepDropPullbackPct { get; set; } = 0.5;
    public int DeepDropMaxElapsed { get; set; } = 60;
    public int DeepDropMinSnapshots { get; set; } = 20;
    public double DeepDropAboveVwapTol { get; set; } = 0.2;
    public int DeepDropPlatformMinBars { get; set; } = 12;
    public double DeepDropPlatformAmplitude { get; set; } = 1.5;
    public double DeepDropTouchTolerance { get; set; } = 0.6;
    public double DeepDropVolShrink { get; set; } = 0.85;

    // 单根巨量做顶
    public double SpikeVolumeMultiple { get; set; } = 2.5;
    public double SpikeVolumeMinPosition { get; set; } = 0.55;
    public int SpikeVolumeSurgeLookback { get; set; } = 20;
    public double SpikeVolumeSurgeMinRise { get; set; } = 1.2;
    public double SpikeVolumePrevCvMax { get; set; } = 0.45;
    public int SpikeVolumeCooldownBars { get; set; } = 8;

    // 形态相似度
    public bool EnablePatternSimilarity { get; set; } = true;
    public double PatternSimilarityThreshold { get; set; } = 0.50;
    public double DoubleTopSimilarityMin { get; set; } = 0.50;
    public double FishingLineSimilarityMin { get; set; } = 0.50;
    public double SurgePullbackSimilarityMin { get; set; } = 0.45;
    public double TopDivergenceSimilarityMin { get; set; } = 0.45;
    public double HeadShoulderSimilarityMin { get; set; } = 0.50;
    public double PlatformBreakSimilarityMin { get; set; } = 0.50;
    public double TripleTopSimilarityMin { get; set; } = 0.50;
    public double HighDeviationPullbackSimilarityMin { get; set; } = 0.45;
}

// ==================== Data Models ====================
// IntradaySnapshot, MarketSnapshot, PlanState 已移至 EngineModels.cs 统一定义

/// <summary>
/// 交易计划信息（对应 JS plan 对象，仅含检测所需字段）
/// </summary>
public class TradingPlanInfo
{
    public string Id { get; set; } = "default";
    public double EntryPrice { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// 卖点信号（对应 JS analyze 返回数组中的信号对象）
/// </summary>
public class SellPointSignal
{
    public string Type { get; set; } = "";
    public string LevelName { get; set; } = "";
    public double LevelPrice { get; set; }
    public double CurrentPrice { get; set; }
    public double Weight { get; set; }
    public bool IsResonance { get; set; }
    public long Timestamp { get; set; }
    public bool IsStopLoss { get; set; }
    public bool IsVolumeAmplified { get; set; }
    /// <summary>
    /// 类型专属详情字段（动态属性，对应 JS 对象的额外字段）
    /// </summary>
    public Dictionary<string, object?> Details { get; set; } = new();

    public SellPointSignal Set(string key, object? value)
    {
        Details[key] = value;
        return this;
    }

    public T? Get<T>(string key)
    {
        return Details.TryGetValue(key, out var v) && v is T t ? t : default;
    }

    public double GetDouble(string key, double defaultValue = 0)
    {
        return Details.TryGetValue(key, out var v) && v is double d ? d : defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        return Details.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;
    }
}

// PlanState 已移至 EngineModels.cs 统一定义

/// <summary>
/// 分析上下文（对应 JS snapshots._ctx 缓存）
/// </summary>
public class AnalyzeContext
{
    public List<double> Prices { get; set; } = new();
    public double DayLow { get; set; }
    public double DayHigh { get; set; }
    public double VwapSlope { get; set; }
}

/// <summary>
/// 市场环境上下文
/// </summary>
public class MarketContext
{
    public bool IsMorningOpen { get; set; }
    public bool IsAfternoonOpen { get; set; }
    public bool IsLateSession { get; set; }
    public bool IsUpLimit { get; set; }
    public bool IsDownLimit { get; set; }
    public double ChangePct { get; set; }
}

/// <summary>
/// 鲁棒峰值检测结果
/// </summary>
public class PeakInfo
{
    public int Index { get; set; }
    public double Price { get; set; }
    public double Prominence { get; set; }
    public double RelHeight { get; set; }
}

/// <summary>
/// 评分结果（对应 JS evaluateSignals 返回值）
/// </summary>
public class ScoreResult
{
    public int TotalScore { get; set; }
    public int Priority { get; set; }
    public string PriorityName { get; set; } = "";
    public double VwapSlope { get; set; }
    public double Multiplier { get; set; } = 1;
    public double Bonus { get; set; }
    public double Density { get; set; }
    public double PosFactor { get; set; }
    public bool HasStopLossSignal { get; set; }
    public string? HoldFilter { get; set; }
    public double FusedScore { get; set; }
    public int SignalScore { get; set; }
    public double ScoreMods { get; set; }
    public string? Quadrant { get; set; }
    public double GapPct { get; set; }
    public List<CompositionEntry>? Composition { get; set; }
    public List<SellPointSignal> FilteredSignals { get; set; } = new();
}

/// <summary>
/// 评分构成条目
/// </summary>
public class CompositionEntry
{
    public string Type { get; set; } = "";
    public int Base { get; set; }
    public double Multiplier { get; set; }
    public bool Halved { get; set; }
}

/// <summary>
/// 分析结果（对应 JS analyze 返回的带属性数组）
/// </summary>
public class AnalyzeResult
{
    public List<SellPointSignal> Signals { get; set; } = new();
    public int TotalScore { get; set; }
    public int Priority { get; set; } = 4;
    public string PriorityName { get; set; } = "";
    public bool HasStopLossSignal { get; set; }
    public string? HoldFilter { get; set; }
    public double MultiFactorScore { get; set; }
    public string? MultiFactorDetail { get; set; }
    public int SignalScore { get; set; }
    public List<CompositionEntry>? Composition { get; set; }
    public double ScoreMods { get; set; }
    public double ScoreBonus { get; set; }
}

/// <summary>
/// 超买共振检测结果
/// </summary>
public class OverboughtResonance
{
    public bool IsOverbought { get; set; }
    public int ResonanceCount { get; set; }
    public double Rsi { get; set; }
    public double Wr { get; set; }
    public double Mfi { get; set; }
}

/// <summary>
/// 平台信息（用于大跌反抽检测）
/// </summary>
public class PlatformInfo
{
    public int Start { get; set; }
    public int End { get; set; }
    public double Top { get; set; }
    public double Bottom { get; set; }
}

// ==================== Interfaces ====================

/// <summary>
/// 形态相似度计算接口（对应 JS patternSimilarity）
/// </summary>
public interface IPatternSimilarityCalculator
{
    (double similarity, object? details) CalculateSimilarity(
        List<double> prices, string patternType,
        Dictionary<string, int> keyPoints, List<double> volumes);
}

/// <summary>
/// 多因子引擎接口（对应 JS multiFactorEngine）
/// </summary>
public interface IMultiFactorEvaluator
{
    MultiFactorResult Evaluate(
        List<IntradaySnapshot> snapshots, double currentPrice,
        List<KLineData>? dailyKlines, List<SellPointSignal> signals,
        object? capitalFlow);
}

// ==================== Main Service ====================

/// <summary>
/// 分时卖点识别器
/// 识别常见卖点模式：冲高回落、放量滞涨、均线压制、顶背离、量价背离、
/// 双顶、钓鱼线、三重顶、平台跌破、高乖离、VWAP跌破/挡道/拐头、
/// 尾盘出逃、缩量反弹失败、大跌反抽、单根巨量做顶、ATR止损止盈等。
/// 含多信号共振评分系统（四维加权：去重 + 时间密度 + 位置 + 环境）。
/// </summary>
public class PrevDayData
{
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
}

/// <summary>
/// 卖点信号扩展方法
/// </summary>
public static class SellPointSignalExtensions
{
    public static SellPointSignal WithType(this SellPointSignal signal, string type)
    {
        signal.Type = type;
        return signal;
    }
}

// ==================== 向后兼容类型 ====================

/// <summary>
/// 卖出信号摘要（向后兼容）
/// </summary>
public class SellSignal
{
    public string StockCode { get; set; } = "";
    public DateTime Date { get; set; }
    public decimal Score { get; set; }
    public List<string> Reasons { get; set; } = new();
    public SignalLevel Level { get; set; }
}

/// <summary>
/// 信号级别
/// </summary>
public enum SignalLevel { None, Weak, Medium, Strong, Extreme }
