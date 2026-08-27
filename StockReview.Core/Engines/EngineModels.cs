using System;
using System.Collections.Generic;

namespace StockReview.Core.Engines;

/// <summary>
/// 分时快照数据（对应 JS snapshot 对象）
/// 合并 BuyPointDetector 和 SellPointDetector 中的定义
/// MarketSnapshot 是 IntradaySnapshot 的别名，供 MultiFactorEngine 使用
/// </summary>
public class IntradaySnapshot
{
    public DateTime SnapshotAt { get; set; }
    public double Price { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double AvgPrice { get; set; }
    public double Volume { get; set; }
    public double CumulativeVolume { get; set; }
    public double? IntervalVolume { get; set; }
    public double PreClose { get; set; }
    public bool VolumeReliable { get; set; } = true;
}

/// <summary>
/// MarketSnapshot 别名（MultiFactorEngine 使用）
/// </summary>
public class MarketSnapshot : IntradaySnapshot { }

/// <summary>
/// 计划状态机（对应 JS planState 对象）
/// 合并 BuyPointDetector 和 SellPointDetector 中的定义
/// </summary>
public class PlanState
{
    // SellPoint 字段
    public bool HighReached { get; set; }
    public double PeakPrice { get; set; }
    public DateTime? VwapBreakdownAt { get; set; }
    public int VwapBreakdownSnapshotIndex { get; set; } = -1;
    public double VwapBreakdownPrice { get; set; }
    public bool VwapBreakdownSignaled { get; set; }
    public int LastSnapshotLength { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>
    /// 单根巨量做顶冷却索引（按 planId 隔离）
    /// </summary>
    public int SpikeVolCooldownIdx { get; set; } = -999;

    // BuyPoint 字段
    public DateTime? LastBuySignalTime { get; set; }
}
