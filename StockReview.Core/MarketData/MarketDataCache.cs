using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using StockReview.Core.Services;

namespace StockReview.Core.MarketData;

/// <summary>
/// 行情数据缓存组（A5 拆分，2026-09-02）。
/// 从 PlanSchedulerService 提取的 7 个行情缓存字典，集中持有以便独立测试。
/// key 均为 stockCode；TTL 语义与提取前完全一致（零行为变更）。
/// </summary>
public sealed class MarketDataCache
{
    /// <summary>快照缓存（10 秒 tick 主导追加，推送路径原地更新最新一条）</summary>
    public ConcurrentDictionary<string, List<PriceSnapshot>> SnapshotCache { get; } = new();

    /// <summary>秒级价格轨迹（时间升序，仅保留最近约16分钟，供时间窗口快速涨跌检测）</summary>
    public ConcurrentDictionary<string, List<LiveTrailPoint>> LiveTrail { get; } = new();

    /// <summary>快照内存缓冲（批量落地 SQLite 前的暂存）</summary>
    public ConcurrentDictionary<string, List<PriceSnapshot>> SnapshotBuffer { get; } = new();

    /// <summary>分时VWAP缓存（盘中 60s TTL 自动刷新）</summary>
    public ConcurrentDictionary<string, (List<IntradayPoint> Data, DateTime FetchedAt)> TrendsCache { get; } = new();

    /// <summary>日K线缓存（TTL: 5分钟；跨天清理见 <see cref="ResetForNewDay"/>）</summary>
    public ConcurrentDictionary<string, (List<KLineData> Data, DateTime ExpiresAt)> DailyKlineCache { get; } = new();

    /// <summary>资金流向缓存（TTL: 5分钟）</summary>
    public ConcurrentDictionary<string, (object? Data, DateTime ExpiresAt)> CapitalFlowCache { get; } = new();

    /// <summary>批量行情缓存（TTL: 由 Settings.RefreshIntervalMs 决定，3/5/10 秒三挡）</summary>
    public ConcurrentDictionary<string, (StockQuote Data, DateTime ExpiresAt)> BatchQuoteCache { get; } = new();

    /// <summary>
    /// 跨天重置：清空秒级轨迹与日K线缓存（对应原 OnDayChanged 中的
    /// _liveTrail.Clear() + _dailyKlineCache.Clear()，其余缓存靠 TTL 自然过期）。
    /// </summary>
    public void ResetForNewDay()
    {
        LiveTrail.Clear();
        DailyKlineCache.Clear();
    }

    /// <summary>
    /// 清理过期缓存（周期性调用，对应原 PlanSchedulerService.CleanupExpiredCaches）：
    /// 行情/资金流向按 ExpiresAt 过期；分时VWAP缓存清掉超过 10 分钟的陈旧条目。
    /// </summary>
    public void CleanupExpired(DateTime now)
    {
        CleanupExpired(BatchQuoteCache, now);
        CleanupExpired(CapitalFlowCache, now);
        foreach (var key in TrendsCache.Keys.Where(k =>
            TrendsCache.TryGetValue(k, out var v) && (now - v.FetchedAt).TotalMinutes > 10).ToList())
        {
            TrendsCache.TryRemove(key, out _);
        }
        // 日K线缓存跨天清理在 ResetForNewDay 中处理
    }

    private static void CleanupExpired<T>(ConcurrentDictionary<string, (T Data, DateTime ExpiresAt)> cache, DateTime now)
    {
        foreach (var key in cache.Keys.Where(k =>
            cache.TryGetValue(k, out var v) && v.ExpiresAt <= now).ToList())
        {
            cache.TryRemove(key, out _);
        }
    }
}
