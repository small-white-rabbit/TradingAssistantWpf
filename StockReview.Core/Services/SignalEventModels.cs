using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StockReview.Core.Data;

public class SignalEvent
{
    public string Id { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string SignalType { get; set; } = "";
    public string SignalLabel { get; set; } = "";
    public decimal Price { get; set; }
    public long Timestamp { get; set; }
    public string TimeStr { get; set; } = "";
    public int SnapshotIndex { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string DataMode { get; set; } = "snapshot";
    public SignalEvaluation? Evaluation { get; set; }
    public bool Evaluated { get; set; }
    public bool IsOptimized { get; set; }
    public int? OptimizationVersion { get; set; }
    public long? OptimizedAt { get; set; }
}

public class SignalEventInput
{
    public string StockCode { get; set; } = "";
    public string? StockName { get; set; }
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public decimal Price { get; set; }
    public long Timestamp { get; set; }
    public int SnapshotIndex { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? DataMode { get; set; }
}

public class SignalEvaluation
{
    public string Result { get; set; } = "neutral";
    public string? Reason { get; set; }
    public double MaxChangePct { get; set; }
    public int EvalWindowMin { get; set; }
    public double TriggerPrice { get; set; }
    public double? Reward { get; set; }
    public double Quality { get; set; }
    public double Capture { get; set; }
    public double TimeEfficiency { get; set; }
    public double CapturePct { get; set; }
    public bool NearDayHigh { get; set; }
    public bool BeforeMaxDrawdown { get; set; }
    public bool NearDayLow { get; set; }
    public int? WaveIdx { get; set; }
    public double? WaveCapture { get; set; }
    public bool NearWaveTop { get; set; }
    public double? WaveDepthPct { get; set; }
    public bool WaveHigh { get; set; }
    public bool WaveLow { get; set; }
    public double? RankScore { get; set; }
    public string? Detail { get; set; }
}

public class Snapshot
{
    public DateTime SnapshotAt { get; set; }
    public decimal Price { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Open { get; set; }
    public decimal? AvgPrice { get; set; }
    public decimal? Volume { get; set; }
    public decimal? CumulativeVolume { get; set; }
    public decimal? IntervalVolume { get; set; }
    public bool? VolumeReliable { get; set; }
    public decimal? PreClose { get; set; }
}

public class EvalConfig
{
    public int[] BuySignalEvalWindows { get; set; } = Array.Empty<int>();
    public double BuySuccessThreshold { get; set; }
    public int[] SellSignalEvalWindows { get; set; } = Array.Empty<int>();
    public double SellSuccessThreshold { get; set; }
    public int[] RapidRiseEvalWindows { get; set; } = Array.Empty<int>();
    public double RapidRiseSuccessThreshold { get; set; }
    public int[] RapidFallEvalWindows { get; set; } = Array.Empty<int>();
    public double RapidFallSuccessThreshold { get; set; }
}

public class RewardInfo
{
    public double Reward { get; set; }
    public double Quality { get; set; }
    public double Capture { get; set; }
    public double TimeEfficiency { get; set; }
    public double CapturePct { get; set; }
}

public class OptimalExitPoints
{
    public int DayHighIdx { get; set; }
    public long DayHighTime { get; set; }
    public double DayHighPrice { get; set; }
    public int MaxDrawdownPeakIdx { get; set; }
    public long MaxDrawdownPeakTime { get; set; }
    public double MaxDrawdownPeakPrice { get; set; }
    public int MaxDrawdownEndIdx { get; set; }
    public double MaxDrawdownEndPrice { get; set; }
    public double MaxDrawdownPct { get; set; }
    public int DayLowIdx { get; set; }
    public long? DayLowTime { get; set; }
    public double DayLowPrice { get; set; }
}

public class Pivot
{
    public int Idx { get; set; }
    public char Type { get; set; }
    public double Price { get; set; }
}

public class Wave
{
    public int WaveIdx { get; set; }
    public int TroughIdx { get; set; }
    public long TroughTime { get; set; }
    public double TroughPrice { get; set; }
    public int PeakIdx { get; set; }
    public long PeakTime { get; set; }
    public double PeakPrice { get; set; }
    public double RisePct { get; set; }
    public int EndIdx { get; set; }
    public int BottomIdx { get; set; }
    public long BottomTime { get; set; }
    public double BottomPrice { get; set; }
    public double DepthPct { get; set; }
}

public class SignalTypeStat
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Fail { get; set; }
    public int Neutral { get; set; }
    public double AvgChangePct { get; set; }
    public double? AvgReward { get; set; }
    public List<StatHistoryRecord>? History { get; set; }
    public int NearDayHighCount { get; set; }
    public int BeforeMaxDrawdownCount { get; set; }
    public int NearDayLowCount { get; set; }
}

public class StatHistoryRecord
{
    public string Date { get; set; } = "";
    public string Result { get; set; } = "";
    public double ChangePct { get; set; }
    public double Reward { get; set; }
    public bool NearDayHigh { get; set; }
    public bool BeforeMaxDrawdown { get; set; }
    public bool NearDayLow { get; set; }
    public string? StockCode { get; set; }
}

public class RecentSignalStat
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Fail { get; set; }
    public int Neutral { get; set; }
    public double AvgChangePct { get; set; }
    public double AvgReward { get; set; }
    public int NearDayHighCount { get; set; }
    public int BeforeMaxDrawdownCount { get; set; }
    public int NearDayLowCount { get; set; }
    public int WaveHighCount { get; set; }
    public int WaveLowCount { get; set; }
}

public class StockQualityStat
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int TodayTotal { get; set; }
    public int Total { get; set; }
    public int High { get; set; }
    public int Mid { get; set; }
    public int Low { get; set; }
}

public class ReplayResult
{
    public int Replayable { get; set; }
    public int LowTotal { get; set; }
    public int LowFiltered { get; set; }
    public double? LowFilterRate { get; set; }
    public int HighTotal { get; set; }
    public int HighKept { get; set; }
    public double? HighKeepRate { get; set; }
    public int Stage1LowFiltered { get; set; }
    public int Stage1HighKept { get; set; }
    public List<WaveReplayInfo> Waves { get; set; } = new();
    public List<string> WaveViolations { get; set; } = new();
    public Dictionary<string, double> Blame { get; set; } = new();
    public Dictionary<string, double> Credit { get; set; } = new();
}

public class ReplayEventInfo
{
    public SignalEvent Event { get; set; } = new();
    public string Quality { get; set; } = "";
    public bool Stage1Pass { get; set; }
    public bool Stage2Pass { get; set; }
    public double Strength { get; set; }
    public string DateKey { get; set; } = "";
    public Dictionary<string, double>? Contributions { get; set; }
}

public class WaveReplayInfo
{
    public string WaveKey { get; set; } = "";
    public string DateKey { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int WaveIdx { get; set; }
    public double? DepthPct { get; set; }
    public int HighTotal { get; set; }
    public int HighKept { get; set; }
    public double Top1Rank { get; set; }
    public bool Top1Alive { get; set; }
}

public class FactorRewardStat
{
    public int Total { get; set; }
    public double RewardSum { get; set; }
    public double ScoreSum { get; set; }
    public int HighRewardCount { get; set; }
    public int OptimalHitCount { get; set; }
    public int HighQualityCount { get; set; }
    public double HighQualityScoreSum { get; set; }
    public int LowQualityCount { get; set; }
    public double LowQualityScoreSum { get; set; }
    public double AvgReward { get; set; }
    public double AvgScore { get; set; }
    public double HighRewardRate { get; set; }
    public double OptimalHitRate { get; set; }
    public double HighQualityAvgScore { get; set; }
    public double LowQualityAvgScore { get; set; }
    public double DiscriminativePower { get; set; }
}

public class OptimizationSuggestion
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public double WinRate { get; set; }
    public int Total { get; set; }
    public double AvgChangePct { get; set; }
}

// ============ 归因账本模型 ============

public class AttributionLedger
{
    public Dictionary<string, AttributionEntry> Entries { get; set; } = new();
    public long UpdatedAt { get; set; }
    public string? DayKey { get; set; }
}

public class AttributionEntry
{
    public string Kind { get; set; } = "signal";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "normal";
    public bool Frozen { get; set; }
    public string FreezeReason { get; set; } = "";
    public long? FrozenAt { get; set; }
    public int DirectionStreak { get; set; }
    public int TotalLowFiltered { get; set; }
    public int TotalHighKilled { get; set; }
    public int? FailedSteps { get; set; }
    public List<AttributionHistoryRecord>? History { get; set; }
}

public class AttributionHistoryRecord
{
    public long Ts { get; set; }
    public double Delta { get; set; }
    public double Net { get; set; }
    public int LowFiltered { get; set; }
    public int HighKilled { get; set; }
    public string? Note { get; set; }
}

public class AttributionRoundEntry
{
    public string? ParamKey { get; set; }
    public string? Kind { get; set; }
    public string? Label { get; set; }
    public double Delta { get; set; }
    public int LowFiltered { get; set; }
    public int HighKilled { get; set; }
    public bool Failed { get; set; }
}

// ============ 漏报分析模型 ============

public class MissedAnalysisSummary
{
    public string DateKey { get; set; } = "";
    public int SignificantWaves { get; set; }
    public int MissedCount { get; set; }
    public List<MissedWaveInfo>? Missed { get; set; }
    public FeatureCompareResult? FeatureCompare { get; set; }
    public string? MutedHint { get; set; }
    public long UpdatedAt { get; set; }
    public int RecentSignificant { get; set; }
    public int RecentMissed { get; set; }
}

public class MissedWaveInfo
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int WaveIdx { get; set; }
    public long PeakTime { get; set; }
    public string? PeakTimeStr { get; set; }
    public double RisePct { get; set; }
    public double DepthPct { get; set; }
    public string Coverage { get; set; } = "";
    public List<string>? MutedTypes { get; set; }
    public Dictionary<string, string>? MutedLabels { get; set; }
    public WaveFeatures? Features { get; set; }
}

public class WaveFeatures
{
    public double? VwapDevPct { get; set; }
    public double? SurgeSpeed5m { get; set; }
    public double? VolumeExp { get; set; }
}

public class FeatureCompareResult
{
    public double? MissedVwapDev { get; set; }
    public double? CoveredVwapDev { get; set; }
    public double? MissedVolExp { get; set; }
    public double? CoveredVolExp { get; set; }
    public double? MissedSpeed { get; set; }
    public double? CoveredSpeed { get; set; }
}
