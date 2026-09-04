using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using StockReview.Core.MarketData;

namespace StockReview.Core.Engines;

/// <summary>
/// IMultiFactorEvaluator 适配器（外部资源分析报告建议1/4 接线层）
/// 把卖点检测引擎的领域类型（IntradaySnapshot/KLineData/SellPointSignal）
/// 转换为 MultiFactorEngineService 的输入类型后委托评分，
/// 使 SellPointDetectorService._multiFactorEngine 不再为 null，
/// 资金流因子（富途 GetCapitalFlow）真正参与多因子评分。
/// </summary>
public class MultiFactorEngineAdapter : IMultiFactorEvaluator
{
    private readonly MultiFactorEngineService _engine;

    public MultiFactorEngineAdapter(MultiFactorEngineService engine)
    {
        _engine = engine;
    }

    public MultiFactorResult Evaluate(
        List<IntradaySnapshot> snapshots, double currentPrice,
        List<KLineData>? dailyKlines, List<SellPointSignal> signals,
        object? capitalFlow)
    {
        try
        {
            var marketSnapshots = snapshots?.Select(ToMarketSnapshot).ToList() ?? new List<MarketSnapshot>();
            var klines = dailyKlines?.Select(ToDailyKline).ToList();
            var detected = signals?.Select(ToDetectedSignal).ToList() ?? new List<DetectedSignal>();
            var flow = capitalFlow as CapitalFlowData;

            return _engine.Evaluate(marketSnapshots, currentPrice, klines, detected, flow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[多因子适配] 类型转换/评估失败，返回中性结果降级为纯信号评分");
            return new MultiFactorResult { TotalScore = 0, Direction = "neutral", Confidence = 0, Detail = "适配器降级" };
        }
    }

    /// <summary>
    /// IntradaySnapshot → MarketSnapshot（子类逐字段复制，泛型不变性无法直接传引用）
    /// </summary>
    private static MarketSnapshot ToMarketSnapshot(IntradaySnapshot s) => new()
    {
        SnapshotAt = s.SnapshotAt,
        Price = s.Price,
        Open = s.Open,
        High = s.High,
        Low = s.Low,
        AvgPrice = s.AvgPrice,
        Volume = s.Volume,
        CumulativeVolume = s.CumulativeVolume,
        IntervalVolume = s.IntervalVolume,
        PreClose = s.PreClose,
        VolumeReliable = s.VolumeReliable
    };

    /// <summary>
    /// KLineData(decimal) → DailyKline(double)，均线压力因子按 double 计算
    /// </summary>
    private static DailyKline ToDailyKline(KLineData k) => new()
    {
        Open = (double)k.Open,
        High = (double)k.High,
        Low = (double)k.Low,
        Close = (double)k.Close,
        Volume = k.Volume,
        Date = k.Date
    };

    /// <summary>
    /// SellPointSignal → DetectedSignal（分时形态因子只消费 Type 集合，
    /// Score 语义不同：信号权重(1-5)不是因子分(0-100)，做 clamp 防溢出）
    /// </summary>
    private static DetectedSignal ToDetectedSignal(SellPointSignal s) => new()
    {
        Type = s.Type,
        Label = s.LevelName,
        Score = (int)Math.Min(100, Math.Max(0, s.Weight))
    };
}
