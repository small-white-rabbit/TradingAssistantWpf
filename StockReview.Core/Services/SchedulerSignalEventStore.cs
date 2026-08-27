using System;
using System.Collections.Generic;
using System.Linq;

namespace StockReview.Core.Services;

// ============================================================================
// SchedulerSignalEventStore.cs
// Stage 6：为 ISignalEventStore 提供真实实现，桥接调度器与 SignalEventService，
// 并借 IMarketTimeService 计算「今日」以驱动信号评估。
// ============================================================================

/// <summary>
/// ISignalEventStore 适配器 - 桥接调度器与 SignalEventService
/// </summary>
public class SchedulerSignalEventStore : ISignalEventStore
{
    private readonly SignalEventService _signalEvents;
    private readonly IMarketTimeService _marketTime;

    public SchedulerSignalEventStore(SignalEventService signalEvents, IMarketTimeService marketTime)
    {
        _signalEvents = signalEvents;
        _marketTime = marketTime;
    }

    public void RecordEvent(SignalEventRecord record)
    {
        if (record == null) return;
        _signalEvents.RecordEvent(new SignalEventInput
        {
            StockCode = record.StockCode,
            StockName = record.StockName,
            SignalType = record.SignalType,
            SignalLabel = record.SignalLabel,
            Price = record.Price,
            Timestamp = record.Timestamp,
            SnapshotIndex = record.SnapshotIndex,
            Metadata = record.Metadata
        });
    }

    public Dictionary<string, SignalStat> GetRecentStats()
    {
        return _signalEvents.GetRecentStats().ToDictionary(
            kv => kv.Key,
            kv => new SignalStat
            {
                Total = kv.Value.Total,
                Success = kv.Value.Success,
                Fail = kv.Value.Fail,
                AvgReward = (decimal)kv.Value.AvgReward,
                SignalLabel = kv.Value.SignalLabel,
                NearDayHighCount = kv.Value.NearDayHighCount,
                NearDayLowCount = kv.Value.NearDayLowCount,
                BeforeMaxDrawdownCount = kv.Value.BeforeMaxDrawdownCount,
                WaveHighCount = kv.Value.WaveHighCount,
                WaveLowCount = kv.Value.WaveLowCount
            });
    }

    // 与保障接口同名同 namespace 的 FactorRewardStat 直接透传（SignalEventService 已定义）
    public Dictionary<string, FactorRewardStat> GetFactorRewardStats() => _signalEvents.GetFactorRewardStats();

    public void EvaluateTodaySignals(Dictionary<string, List<PriceSnapshot>> allSnapshots)
    {
        if (allSnapshots == null || allSnapshots.Count == 0) return;
        var date = _marketTime.FormatDate(_marketTime.GetNow());
        var map = allSnapshots.ToDictionary(kv => kv.Key, kv => kv.Value.Select(ToSnapshot).ToList());
        _signalEvents.EvaluateDay(date, map);

        // 漏报复盘：评估后紧接着分析"该出现卖点而未出现"的显著回落波
        // （对齐 planScheduler.js evaluateDay → analyzeMissedSellPoints 串联，共用 snapshotsMap）。
        // 产出写入 pet_missed_sell_analysis 供 ResurrectMutedFromMissed 漏报复活使用——
        // 修复前该方法零调用，漏报侧闭环（回放只看已触发事件）从未运行
        try
        {
            var missed = _signalEvents.AnalyzeMissedSellPoints(date, map);
            if (missed != null && missed.MissedCount > 0)
            {
                Serilog.Log.Information(
                    "[漏报复盘] 今日显著回落波 {Total} 个，其中 {Missed} 个未获卖点覆盖",
                    missed.SignificantWaves, missed.MissedCount);
            }
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "[漏报复盘] 分析失败");
        }
    }

    public List<SignalEventRecord> GetTodayEvents()
    {
        return _signalEvents.GetTodayEvents().Select(e => new SignalEventRecord
        {
            StockCode = e.StockCode,
            StockName = e.StockName,
            SignalType = e.SignalType,
            SignalLabel = e.SignalLabel,
            Price = e.Price,
            Timestamp = e.Timestamp,
            SnapshotIndex = e.SnapshotIndex,
            Metadata = e.Metadata ?? new Dictionary<string, object>()
        }).ToList();
    }

    // 自进化搜索引擎所需的回放/归因能力（透传 SignalEventService 真实实现）
    public ReplayResult ReplayWithParams(Dictionary<string, double>? newMultipliers,
        Dictionary<string, double>? newFactorWeights, int days = 5)
        => _signalEvents.ReplayWithParams(newMultipliers, newFactorWeights, days);

    public void UpdateAttribution(List<AttributionRoundEntry> roundEntries)
        => _signalEvents.UpdateAttribution(roundEntries ?? new List<AttributionRoundEntry>());

    public void DecayAttributionFreezes() => _signalEvents.DecayAttributionFreezes();

    public AttributionLedger GetAttributionLedger() => _signalEvents.GetAttributionLedger();

    private static Snapshot ToSnapshot(PriceSnapshot s) => new()
    {
        SnapshotAt = s.Timestamp,
        Price = s.Price,
        AvgPrice = s.Vwap,
        Volume = s.Volume,
        VolumeReliable = s.VolumeReliable
    };
}