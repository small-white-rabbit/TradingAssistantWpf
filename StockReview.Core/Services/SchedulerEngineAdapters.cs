using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;

namespace StockReview.Core.Services;

// ============================================================================
// SchedulerEngineAdapters.cs
// Stage 5：为 PlanSchedulerService 的检测/引擎接口接真实引擎服务。
// 核心差异（double vs decimal、int 评分 vs 复合结果）在此做一次适配转换。
// ============================================================================

/// <summary>
/// ISellPointDetector 适配器 - 桥接调度器与 SellPointDetectorService
/// </summary>
public class SchedulerSellPointDetector : ISellPointDetector
{
    private readonly SellPointDetectorService _engine;

    public SchedulerSellPointDetector(SellPointDetectorService engine)
    {
        _engine = engine;
    }

    public List<SellSignalInfo> Analyze(TradePlan plan, StockQuote data, List<PriceSnapshot> snapshots, List<KLineData> dailyKlines, object? capitalFlow)
    {
        var result = _engine.Analyze(
            new TradingPlanInfo
            {
                Id = plan.Id,
                EntryPrice = (double)(plan.EntryPrice ?? 0m),
                CreatedAt = SchedulerEngineMap.ParseDate(plan.CreatedAt)
            },
            data,
            SchedulerEngineMap.MapSnapshots(snapshots),
            dailyKlines,
            capitalFlow);

        return result.Signals.Select(s => new SellSignalInfo
        {
            Type = s.Type,
            Label = string.IsNullOrEmpty(s.LevelName) ? s.Type : s.LevelName,
            Score = (int)Math.Round(s.Weight * 10 + result.TotalScore * 0.3),  // 单信号加权分 + 整体评分加成
            Similarity = s.Details.TryGetValue("similarity", out var sim) && sim is double sd && sd > 0 ? (decimal?)sd : null,
            PriorityName = result.PriorityName,
            TotalScore = (decimal)s.CurrentPrice  // 当前价格，供提醒文本和波闸使用
        }).ToList();
    }

    public void UpdateConfig(object config)
    {
        // 调度器只传部分参数字段（匿名对象），与引擎现行配置合并，避免重置其余字段
        var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(config)) ?? new();

        var merged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(_engine.GetConfig()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var (k, v) in incoming)
        {
            var match = merged.Keys.FirstOrDefault(kk =>
                string.Equals(kk, k, StringComparison.OrdinalIgnoreCase));
            if (match != null) merged[match] = v;
        }

        var cfg = JsonSerializer.Deserialize<SellPointDetectorConfig>(
            JsonSerializer.Serialize(merged),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        _engine.UpdateConfig(cfg);
    }

    public void UpdateSignalMultipliers(Dictionary<string, decimal> multipliers)
    {
        _engine.UpdateSignalMultipliers(multipliers.ToDictionary(kv => kv.Key, kv => (double)kv.Value));
    }

    public Dictionary<string, decimal> GetSignalMultipliers()
        => ToDecimalDict(_engine.GetSignalMultipliers());

    public Dictionary<string, decimal> GetSignalMultipliersSnapshot()
        => ToDecimalDict(_engine.GetSignalMultipliers());

    private static Dictionary<string, decimal> ToDecimalDict(Dictionary<string, double> src)
        => src.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);
}

/// <summary>
/// IBuyPointDetector 适配器 - 桥接调度器与 BuyPointDetectorService
/// </summary>
public class SchedulerBuyPointDetector : IBuyPointDetector
{
    private readonly BuyPointDetectorService _engine;

    public SchedulerBuyPointDetector(BuyPointDetectorService engine)
    {
        _engine = engine;
    }

    public List<BuySignalInfo> Analyze(TradePlan plan, StockQuote? data, List<PriceSnapshot> snapshots, List<KLineData> dailyKlines)
    {
        var results = _engine.Analyze(
            plan.Id,
            (double)(data?.CurrentPrice ?? plan.EntryPrice ?? 0m),
            (double)(data?.PreClose ?? 0m),
            SchedulerEngineMap.MapSnapshots(snapshots),
            dailyKlines == null ? null : SchedulerEngineMap.MapKlines(dailyKlines));

        return results.Select(r => new BuySignalInfo
        {
            Type = r.Type,
            Label = r.Label,
            Score = r.Score
        }).ToList();
    }
}

/// <summary>
/// IMultiFactorEngine 适配器 - 桥接调度器与 MultiFactorEngineService
/// </summary>
public class SchedulerMultiFactorEngine : IMultiFactorEngine
{
    private readonly MultiFactorEngineService _engine;

    public SchedulerMultiFactorEngine(MultiFactorEngineService engine)
    {
        _engine = engine;
    }

    public Dictionary<string, decimal> GetWeights()
        => _engine.GetWeights().ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);

    public void UpdateWeights(Dictionary<string, decimal> weights)
        => _engine.UpdateWeights(weights.ToDictionary(kv => kv.Key, kv => (double)kv.Value));

    public decimal CalculateFusedScore(Dictionary<string, decimal> factorScores, Dictionary<string, decimal> weights)
    {
        decimal total = 0m;
        foreach (var (k, score) in factorScores)
            if (weights.TryGetValue(k, out var w)) total += score * w;
        return total;
    }
}

// ============ 内部转换工具 ============

internal static class SchedulerEngineMap
{
    /// <summary>PriceSnapshot → 引擎 IntradaySnapshot（Volume 已是区间量，对齐 Electron 语义）</summary>
    public static List<IntradaySnapshot> MapSnapshots(List<PriceSnapshot> snapshots)
    {
        double cumFallback = 0;
        return snapshots.Select(s =>
        {
            var interval = (double)s.Volume;
            cumFallback += interval; // 旧数据无 CumulativeVolume 时的累计量兜底
            return new IntradaySnapshot
            {
                SnapshotAt = s.Timestamp,
                Price = (double)s.Price,
                High = (double)(s.High > 0 ? s.High : s.Price),
                Low = (double)(s.Low > 0 ? s.Low : s.Price),
                Volume = interval,
                IntervalVolume = interval, // 显式标记区间量，NormalizeVolumes 不再重算
                CumulativeVolume = s.CumulativeVolume > 0 ? s.CumulativeVolume : cumFallback,
                AvgPrice = (double)s.Vwap,
                VolumeReliable = s.VolumeReliable
            };
        }).ToList();
    }

    /// <summary>KLineData → 引擎 DailyKline</summary>
    public static List<DailyKline> MapKlines(List<KLineData> klines)
        => klines.Select(k => new DailyKline
        {
            Date = k.Date,
            Open = (double)k.Open,
            High = (double)k.High,
            Low = (double)k.Low,
            Close = (double)k.Close,
            Volume = k.Volume
        }).ToList();

    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt) ? dt.ToUniversalTime() : null;
    }
}