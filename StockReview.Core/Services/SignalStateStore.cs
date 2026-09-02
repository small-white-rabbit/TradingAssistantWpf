using System.Collections.Concurrent;

namespace StockReview.Core.Services;

/// <summary>
/// 信号检测状态组（A5 拆分，2026-09-02）。
/// 从 PlanSchedulerService 提取的 5 个信号去重/限频状态字典，集中持有以便独立测试。
/// 语义与提取前完全一致（零行为变更）。
/// </summary>
public sealed class SignalStateStore
{
    /// <summary>信号状态缓存（去重）- key: "planId:sigType"</summary>
    public ConcurrentDictionary<string, SignalStateEntry> SignalStates { get; } = new();

    /// <summary>限频器 - key: "stockCode:type"</summary>
    public ConcurrentDictionary<string, RateLimitRecord> RateLimiter { get; } = new();

    /// <summary>波内限发状态 - key: stockCode</summary>
    public ConcurrentDictionary<string, WaveGateState> WaveGateStates { get; } = new();

    /// <summary>已提醒的目标价级别 - key: "planId:level" (当日去重)</summary>
    public ConcurrentDictionary<string, bool> LevelHitNotified { get; } = new();

    /// <summary>当日已触发动作型提醒 - key: "planId:actionType" (当日一次)</summary>
    public ConcurrentDictionary<string, bool> ActionEmittedToday { get; } = new();

    /// <summary>
    /// 跨天重置：清空全部当日去重/限频状态（对应原 OnDayChanged 中的 5 个 Clear()）。
    /// </summary>
    public void ResetForNewDay()
    {
        SignalStates.Clear();
        RateLimiter.Clear();
        WaveGateStates.Clear();
        LevelHitNotified.Clear();
        ActionEmittedToday.Clear();
    }
}
