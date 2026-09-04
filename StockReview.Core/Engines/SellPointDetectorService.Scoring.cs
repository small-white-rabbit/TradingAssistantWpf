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

public partial class SellPointDetectorService
{

    // ==================== 评分系统 ====================

    /// <summary>
    /// 获取信号基础权重（静态表）
    /// </summary>
    public int GetBaseWeight(string type) => type switch
    {
        SignalTypes.VolumeDivergence => 25,
        SignalTypes.VolumeStagnant => 25,
        SignalTypes.VwapBreakdown => 20,
        SignalTypes.MaSuppress => 15,
        SignalTypes.DoubleTop => 20,
        SignalTypes.FishingLine => 25,
        SignalTypes.TripleTop => 20,
        SignalTypes.PlatformBreakdown => 20,
        SignalTypes.HighDeviationPullback => 15,
        SignalTypes.VwapRejection => 18,
        SignalTypes.VwapSlopeDown => 18,
        SignalTypes.LateSessionExit => 15,
        SignalTypes.SurgePullback => 15,
        SignalTypes.TopDivergence => 15,
        SignalTypes.BreakMa5 => 12,
        SignalTypes.BreakMa10 => 12,
        SignalTypes.BreakMa30 => 12,
        SignalTypes.BreakSupport => 12,
        SignalTypes.WeakReboundFailure => 28,
        SignalTypes.DeepDropRebound => 28,
        SignalTypes.SpikeVolumeTop => 28,
        SignalTypes.AtrStopLoss => 30,
        SignalTypes.AtrTrailingStop => 22,
        SignalTypes.AtrTakeProfit => 15,
        SignalTypes.TimeStop => 18,
        _ => 10
    };

    /// <summary>
    /// 硬规则类型：不参与自进化
    /// </summary>
    private bool IsNoEvolveType(string type) =>
        type == SignalTypes.AtrTakeProfit || type == SignalTypes.TimeStop;

    /// <summary>
    /// 获取信号权重（含自进化乘子）
    /// </summary>
    public int GetSignalWeight(string type)
    {
        var baseWeight = GetBaseWeight(type);
        if (IsNoEvolveType(type)) return baseWeight;
        var multiplier = GetMultiplier(type);
        return (int)JsMath.JsRound(baseWeight * multiplier);
    }

    /// <summary>
    /// 计算信号时间集中度得分
    /// </summary>
    public double CalculateTimeDensity(List<SellPointSignal> signals, long windowMs = 300000)
    {
        if (signals == null || signals.Count < 2) return 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var recent = signals.Where(s => now - (s.Timestamp > 0 ? s.Timestamp : now) < windowMs).ToList();
        if (recent.Count < 2) return 0;
        var timestamps = recent.Select(s => s.Timestamp > 0 ? s.Timestamp : now).ToList();
        var minTs = timestamps.Min();
        var maxTs = timestamps.Max();
        var spanMin = Math.Max(1, (maxTs - minTs) / 60000.0);
        return recent.Count / spanMin;
    }

    /// <summary>
    /// 多信号共振评分系统（四维加权）
    /// </summary>
    public ScoreResult EvaluateSignals(
        List<SellPointSignal> signals,
        List<IntradaySnapshot> snapshots,
        double currentPrice,
        List<KLineData>? dailyKlines,
        MarketContext? marketCtx,
        double multiFactorScore = 0)
    {
        if (signals == null || signals.Count == 0)
        {
            return new ScoreResult
            {
                TotalScore = 0, Priority = 3, PriorityName = "预警关注",
                VwapSlope = 0, Multiplier = 1, Bonus = 0, FilteredSignals = new()
            };
        }

        var vwapSlope = CalculateVWAPSlope(snapshots);
        var vwapRising = vwapSlope > 0.02;

        var uniqueSignals = DeduplicateSignals(signals);
        if (uniqueSignals.Count == 1 && uniqueSignals[0].Type == SignalTypes.VwapSlopeDown)
        {
            return new ScoreResult
            {
                TotalScore = 0, Priority = 3, PriorityName = "预警关注",
                VwapSlope = vwapSlope, Multiplier = 1, Bonus = 0,
                HasStopLossSignal = false, FilteredSignals = new()
            };
        }

        // 四象限
        var avgNow = snapshots.Count > 0 ? snapshots[^1].AvgPrice : 0;
        var gapPct = avgNow > 0 ? (avgNow - currentPrice) / avgNow * 100 : 0;
        var absGap = Math.Abs(gapPct);
        var quadrant = absGap <= 0.5 ? "tight" : absGap <= 1.0 ? "near" : gapPct < -1.0 ? "far_high" : "far_low";

        uniqueSignals = uniqueSignals.Where(s =>
        {
            if (MaBreakTypes.Contains(s.Type))
            {
                if (absGap > 1.0) return false;
                if (!CheckTouchedLineBefore(snapshots, s, 10, dailyKlines)) return false;
            }
            if (quadrant == "far_high")
            {
                if (MaBreakTypes.Contains(s.Type) || StoplossOnlyTypes.Contains(s.Type)) return false;
            }
            if (quadrant == "far_low")
            {
                if (MaBreakTypes.Contains(s.Type)) return false;
                if (ProfitOnlyTypes.Contains(s.Type) && !StoplossOnlyTypes.Contains(s.Type)) return false;
            }
            return true;
        }).ToList();

        // 静音类型剔除
        uniqueSignals = uniqueSignals.Where(s =>
        {
            var m = GetMultiplier(s.Type);
            return !(double.IsFinite(m) && m <= SellPointConstants.SignalMuteThreshold);
        }).ToList();

        // 四象限 UI 名重写
        foreach (var s in uniqueSignals)
        {
            if (s.Type == SignalTypes.VwapBreakdown)
                s.LevelName = quadrant == "tight" ? "跌破均价线" : (quadrant == "near" ? "近距跌破均价线" : "跌破均价线");
            else if (s.Type == SignalTypes.BreakMa5)
                s.LevelName = quadrant == "tight" ? "跌破5日均线" : (quadrant == "near" ? "近距跌破MA5" : "跌破MA5");
            else if (s.Type == SignalTypes.BreakMa10)
                s.LevelName = quadrant == "tight" ? "跌破10日均线" : (quadrant == "near" ? "近距跌破MA10" : "跌破MA10");
            else if (s.Type == SignalTypes.BreakMa30)
                s.LevelName = quadrant == "tight" ? "跌破30日均线" : (quadrant == "near" ? "近距跌破MA30" : "跌破MA30");
            else if (s.Type == SignalTypes.PlatformBreakdown)
                s.LevelName = quadrant == "far_low" ? "破位止损" : (quadrant == "near" ? "近距平台破位" : "跌破平台");
            else if (s.Type == SignalTypes.BreakSupport)
                s.LevelName = quadrant == "far_low" ? "支撑失守（止损）" : (quadrant == "near" ? "近距跌破支撑位" : "跌破支撑位");
            else if (s.Type == SignalTypes.WeakReboundFailure)
                s.LevelName = "缩量反弹失败（止损）";
        }

        if (uniqueSignals.Count == 0)
        {
            return new ScoreResult
            {
                TotalScore = 0, Priority = 3, PriorityName = "预警关注",
                VwapSlope = vwapSlope, Multiplier = 1, Bonus = 0,
                HasStopLossSignal = false, Quadrant = quadrant, GapPct = gapPct,
                FilteredSignals = uniqueSignals
            };
        }

        var types = uniqueSignals.Select(s => s.Type).ToList();
        var hasStopLossSignal = uniqueSignals.Any(s => s.IsStopLoss);
        var hasVWAPWeakSignal = types.Any(t => BreakdownTypes.Contains(t));

        // 基础分
        var baseScore = 0.0;
        var composition = new List<CompositionEntry>();
        foreach (var s in uniqueSignals)
        {
            var baseW = GetBaseWeight(s.Type);
            var mult = IsNoEvolveType(s.Type) ? 1.0 : GetMultiplier(s.Type);
            var w = JsMath.JsRound(baseW * mult);
            var halved = false;
            if (vwapRising)
            {
                if (s.Type == SignalTypes.VwapBreakdown || s.Type == SignalTypes.VwapRejection
                    || s.Type == SignalTypes.MaSuppress || s.Type == SignalTypes.WeakReboundFailure)
                {
                    w *= 0.5;
                    halved = true;
                }
            }
            baseScore += w;
            composition.Add(new CompositionEntry { Type = s.Type, Base = baseW, Multiplier = mult, Halved = halved });
        }

        // 时间集中度加成
        var density = CalculateTimeDensity(uniqueSignals);
        var densityMultiplier = Math.Min(1.5, 1.0 + Math.Min(1.0, density * 0.15));

        // 个股位置系数
        var posFactor = GetPositionFactor(dailyKlines, currentPrice);
        var positionMultiplier = 1.0;
        if (posFactor > 0.8) positionMultiplier = 1.3;
        else if (posFactor < 0.3)
        {
            if (hasStopLossSignal || hasVWAPWeakSignal) positionMultiplier = 1.2;
            else positionMultiplier = 0.6;
        }

        // 市场环境调整
        var envMultiplier = 1.0;
        if (marketCtx != null)
        {
            if (marketCtx.IsMorningOpen || marketCtx.IsAfternoonOpen) envMultiplier = 0.7;
            if (marketCtx.IsLateSession) envMultiplier = 1.2;
        }

        // 核心量价加成
        var bonus = 0.0;
        if (types.Contains(SignalTypes.VolumeDivergence) || types.Contains(SignalTypes.VolumeStagnant))
            bonus = 10;
        if (hasStopLossSignal && types.Contains(SignalTypes.VwapBreakdown))
            bonus += 8;

        var totalScore = (int)Math.Min(100, JsMath.JsRound(baseScore * densityMultiplier * positionMultiplier * envMultiplier + bonus));
        var signalScore = totalScore;
        var scoreMods = densityMultiplier * positionMultiplier * envMultiplier;

        // HOLD 过滤
        string? holdFilter = null;
        var hasHardStopLoss = types.Contains(SignalTypes.AtrStopLoss) || types.Contains(SignalTypes.AtrTrailingStop);
        if (hasHardStopLoss)
        {
            holdFilter = null;
        }
        else if (!types.Contains(SignalTypes.VolumeDivergence) && !types.Contains(SignalTypes.VolumeStagnant))
        {
            holdFilter = "HOLD(Vol)";
        }
        else if (!CheckMomentumConfirm(snapshots))
        {
            holdFilter = holdFilter != null ? "HOLD(Vol+Mom)" : "HOLD(Mom)";
        }

        // 融合多因子评分
        var fusedScore = Math.Max(totalScore, totalScore * 0.6 + multiFactorScore * 0.4);

        int priority;
        string priorityName;
        if (fusedScore >= 70 && holdFilter == null)
        {
            priority = 0; priorityName = "强制清仓";
        }
        else if (fusedScore >= 50 && (holdFilter == null || holdFilter == "HOLD(Mom)"))
        {
            priority = 1; priorityName = "立即卖出";
        }
        else if (fusedScore >= 35)
        {
            priority = 2; priorityName = "减仓观察";
        }
        else if (fusedScore >= 20 && holdFilter != null)
        {
            priority = 3; priorityName = $"预警关注·{holdFilter}";
        }
        else if (fusedScore >= 20)
        {
            priority = 3; priorityName = "预警关注";
        }
        else
        {
            return new ScoreResult
            {
                TotalScore = 0, Priority = 4, PriorityName = "",
                VwapSlope = vwapSlope, Multiplier = densityMultiplier, Bonus = 0,
                Density = density, PosFactor = posFactor,
                HasStopLossSignal = false, HoldFilter = holdFilter,
                FusedScore = 0, SignalScore = signalScore, ScoreMods = scoreMods,
                Composition = composition, Quadrant = quadrant, GapPct = gapPct,
                FilteredSignals = uniqueSignals
            };
        }

        return new ScoreResult
        {
            TotalScore = (int)JsMath.JsRound(fusedScore), Priority = priority, PriorityName = priorityName,
            VwapSlope = vwapSlope, Multiplier = densityMultiplier, Bonus = bonus,
            Density = density, PosFactor = posFactor,
            HasStopLossSignal = hasStopLossSignal, HoldFilter = holdFilter,
            FusedScore = (int)JsMath.JsRound(fusedScore), SignalScore = signalScore,
            ScoreMods = scoreMods, Composition = composition,
            Quadrant = quadrant, GapPct = gapPct,
            FilteredSignals = uniqueSignals
        };
    }

    /// <summary>
    /// 动量确认检查
    /// </summary>
    private bool CheckMomentumConfirm(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 10) return true;
        var recent5 = snapshots.GetRange(snapshots.Count - 5, 5);
        var prev5 = snapshots.GetRange(snapshots.Count - 10, 5);
        var recent0 = recent5[0].Price;
        var prev0 = prev5[0].Price;
        if (recent0 <= 0 || prev0 <= 0) return true;
        var recent5Change = (recent5[^1].Price - recent0) / recent0 * 100;
        // 2026-09-01 修正：原式
        // `recent5Change < 0 || (prev5Change > 0.2 && recent5Change < 0)` 的右支被左支
        // 完全包含，恒等于 `recent5Change < 0`，prev5Change 死计算已移除；
        // prev0<=0 除零防御检查保留（与 JS 原版一致）。
        return recent5Change < 0;
    }

    /// <summary>
    /// 破均线信号前置条件检查
    /// </summary>
    private bool CheckTouchedLineBefore(List<IntradaySnapshot> snapshots, SellPointSignal signal, int recentBars = 10, List<KLineData>? dailyKlines = null)
    {
        if (snapshots == null || snapshots.Count < 3) return true;
        var type = signal.Type;
        var lookStart = Math.Max(0, snapshots.Count - recentBars);
        var lookEnd = snapshots.Count - 2;
        var touchCount = 0;

        var maPeriodByType = new Dictionary<string, int>
        {
            [SignalTypes.BreakMa5] = 5,
            [SignalTypes.BreakMa10] = 10,
            [SignalTypes.BreakMa30] = 30
        };

        double? dailyMA = null;
        if (maPeriodByType.TryGetValue(type, out var maPeriod) && dailyKlines != null && dailyKlines.Count >= maPeriod)
        {
            dailyMA = CalculateDailyMA(dailyKlines, maPeriod);
        }

        var lastAvg = snapshots.Count > 0 ? snapshots[^1].AvgPrice : 0;
        for (var i = lookStart; i <= lookEnd; i++)
        {
            if (i >= snapshots.Count) continue;
            var s = snapshots[i];
            var price = s.Price;
            var refLine = (dailyMA.HasValue && double.IsFinite(dailyMA.Value) && dailyMA.Value > 0)
                ? dailyMA.Value
                : (s.AvgPrice > 0 ? s.AvgPrice : (lastAvg > 0 ? lastAvg : price));
            if (price <= 0 || refLine <= 0) continue;
            var diffPct = (price - refLine) / refLine * 100;
            if (diffPct >= -0.35) touchCount++;
        }
        return touchCount >= 2;
    }

    // ==================== 辅助方法 ====================

    private double GetMinNeckDepth(double price) =>
        price < _config.DoubleTopNeckDepthPriceLow ? _config.DoubleTopNeckDepthLow
        : price < _config.DoubleTopNeckDepthPriceMid ? _config.DoubleTopNeckDepthMid
        : _config.DoubleTopNeckDepthHigh;

}
