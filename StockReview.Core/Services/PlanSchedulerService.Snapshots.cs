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

public partial class PlanSchedulerService
{

    // ============================================================================
    // 限频去重 - 对应 planScheduler.js shouldEmitSignal / checkRateLimit / cleanRateLimit
    // ============================================================================

    /// <summary>
    /// 信号去重检查（只读）- 对应 planScheduler.js shouldEmitSignal
    /// 同一 key 同一状态在冷却时间内不重复触发。
    /// 不在此处写入状态：调用方须在所有门控（波闸/限频等）通过后调用 CommitSignalState，
    /// 否则下游门控失败时会白白消耗一次冷却窗口
    /// </summary>
    public bool CanEmitSignal(string key, string state, int cooldownMs = 15 * 60 * 1000)
    {
        if (state == "normal")
        {
            // 不清除冷却记录：避免价格在阈值附近震荡时反复触发
            return false;
        }

        if (_signalStates.TryGetValue(key, out var previous) &&
            previous.State == state && NowMs - previous.At < cooldownMs)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 提交信号状态（与 CanEmitSignal 配对）：记录状态并开始冷却
    /// </summary>
    public void CommitSignalState(string key, string state)
    {
        _signalStates[key] = new SignalStateEntry { State = state, At = NowMs };
    }

    /// <summary>
    /// 同股同类信息限频检查 - 对应 planScheduler.js checkRateLimit
    /// 滑动窗口：时间窗口内最多触发 maxCount 次
    /// </summary>
    public bool CheckRateLimit(string stockCode, string type, int maxCount = 2, int windowMs = 60 * 1000)
    {
        if (string.IsNullOrEmpty(stockCode) || string.IsNullOrEmpty(type)) return true;

        var key = $"{stockCode}:{type}";
        var now = NowMs;
        var windowStart = now - windowMs;

        var record = _rateLimiter.GetOrAdd(key, _ => new RateLimitRecord());
        lock (record)
        {
            record.Timestamps = record.Timestamps.Where(t => t >= windowStart).ToList();

            if (record.Timestamps.Count >= maxCount)
            {
                return false;
            }

            record.Timestamps.Add(now);
            return true;
        }
    }

    /// <summary>
    /// 清理过期的限频记录 - 对应 planScheduler.js cleanRateLimit
    /// 清理窗口 31 分钟，覆盖最大限频窗口（30 分钟 overnight_gap / daily_loss_breaker）
    /// </summary>
    public void CleanRateLimit()
    {
        var now = NowMs;
        const int windowMs = 31 * 60 * 1000;

        foreach (var kvp in _rateLimiter)
        {
            var record = kvp.Value;
            lock (record)
            {
                record.Timestamps = record.Timestamps.Where(t => now - t <= windowMs).ToList();
                if (record.Timestamps.Count == 0)
                {
                    _rateLimiter.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    // ============================================================================
    // 波内限发 - 对应 planScheduler.js _waveGateState / _waveGateAllows / _waveGatePass
    // ============================================================================

    /// <summary>
    /// 波内限发检查 - 同一价格波动波内只触发一次同类型信号
    /// 波的定义：价格单方向运动（上涨/下跌），直到出现方向反转
    /// </summary>

    // ============================================================================
    // 波内限发 - 对应 planScheduler.js _waveGateState / _waveGateAllows / _waveGatePass
    // ============================================================================

    /// <summary>
    /// 波内限发检查 - 同一价格波动波内只触发一次同类型信号
    /// 波的定义：价格单方向运动（上涨/下跌），直到出现方向反转
    /// </summary>
    private bool WaveGateAllows(string stockCode, decimal currentPrice, string signalType)
    {
        if (string.IsNullOrEmpty(stockCode)) return true;

        var state = _waveGateStates.GetOrAdd(stockCode, _ => new WaveGateState
        {
            LastPrice = currentPrice,
            WaveStartAt = NowMs,
            WaveHigh = currentPrice,
            WaveLow = currentPrice
        });

        lock (state)
        {
            // 判断方向是否反转
            var newDirection = currentPrice > state.LastPrice ? 1 : (currentPrice < state.LastPrice ? -1 : 0);

            if (newDirection != 0 && newDirection != state.LastDirection && state.LastDirection != 0)
            {
                // 方向反转 → 新波开始
                state.WaveStartAt = NowMs;
                state.WaveHigh = Math.Max(state.LastPrice, currentPrice);
                state.WaveLow = Math.Min(state.LastPrice, currentPrice);
                state.LastSignalType = null;
            }

            state.LastPrice = currentPrice;
            if (newDirection != 0) state.LastDirection = newDirection;
            state.WaveHigh = Math.Max(state.WaveHigh, currentPrice);
            state.WaveLow = Math.Min(state.WaveLow, currentPrice);

            // 检查是否已在当前波内触发过同类型信号
            if (state.LastSignalType == signalType)
            {
                return false; // 本波已触发过同类型信号，拒绝
            }

            return true;
        }
    }

    /// <summary>
    /// 波内限发通过 - 标记当前波已触发某类型信号
    /// </summary>
    private void WaveGatePass(string stockCode, decimal currentPrice, string signalType)
    {
        if (string.IsNullOrEmpty(stockCode)) return;

        if (_waveGateStates.TryGetValue(stockCode, out var state))
        {
            lock (state)
            {
                state.LastSignalType = signalType;
            }
        }
    }

    // ============================================================================
    // 级别去重 - 对应 planScheduler.js _isLevelHitNotifiedToday / _markLevelHitNotified
    // ============================================================================
    // 级别去重 - 对应 planScheduler.js _isLevelHitNotifiedToday / _markLevelHitNotified
    // ============================================================================

    private bool IsLevelHitNotifiedToday(string planId, string level)
    {
        return _levelHitNotified.ContainsKey($"{planId}:{level}");
    }


    private void MarkLevelHitNotified(string planId, string level)
    {
        _levelHitNotified[$"{planId}:{level}"] = true;
    }

    // ============================================================================
    // 快照记录 - 对应 planScheduler.js recordSnapshots / saveSnapshot / getSnapshots / _flushSnapshots
    // ============================================================================

    /// <summary>
    /// 记录快照 - 对应 planScheduler.js recordSnapshots
    /// 10秒节奏 + 区间增量量 + 分时数据自算真实VWAP（对齐 Electron）
    /// </summary>

    // ============================================================================
    // 快照记录 - 对应 planScheduler.js recordSnapshots / saveSnapshot / getSnapshots / _flushSnapshots
    // ============================================================================

    /// <summary>
    /// 记录快照 - 对应 planScheduler.js recordSnapshots
    /// 10秒节奏 + 区间增量量 + 分时数据自算真实VWAP（对齐 Electron）
    /// </summary>
    private async Task RecordSnapshotsAsync(Dictionary<string, StockQuote> dataMap)
    {
        var now = Now;

        // 按配置间隔记录（默认10秒，对齐 Electron monitorIntervalMs=10s）
        if ((now - _lastSnapshotTime).TotalSeconds < Config.SnapshotIntervalSec)
        {
            return;
        }
        _lastSnapshotTime = now;

        // 并行获取分时数据，计算真实的分时均价（VWAP），60s缓存（分时数据每分钟更新一次）
        var vwapTasks = dataMap.Keys.Select(async code => (code, Vwap: await FetchTrendsVwapAsync(code)));
        var vwapResults = await Task.WhenAll(vwapTasks);
        var vwapMap = vwapResults.ToDictionary(r => r.code, r => r.Vwap);

        foreach (var (stockCode, quote) in dataMap)
        {
            if (quote == null || quote.CurrentPrice <= 0) continue;

            // 上一个快照（区间量与均价兜底的基准）
            var snapshots = GetSnapshots(stockCode);
            var previous = snapshots.Count > 0 ? snapshots[^1] : null;

            // 行情接口返回当日累计成交量，检测器需要每个采样区间的增量
            var cumulativeVolume = quote.Volume;
            var previousCumulative = previous?.CumulativeVolume ?? 0;
            // M8 修复：累计量比上一快照还低 = 数据源切换/接口重置，这个区间量不可信
            var volumeInvalid = previous != null && cumulativeVolume < previousCumulative;
            var intervalVolume = previous != null && cumulativeVolume >= previousCumulative
                ? cumulativeVolume - previousCumulative
                : 0;
            // M4 修复：降级数据源返回 volume=0，量比类信号静默失效 → 打不可靠标记跳过放量判断
            var volumeReliable = !volumeInvalid && cumulativeVolume > 0;

            var snapshot = new PriceSnapshot
            {
                StockCode = stockCode,
                Price = quote.CurrentPrice,
                Volume = intervalVolume,
                CumulativeVolume = cumulativeVolume,
                Amount = quote.Amount,
                Timestamp = now,
                // 分时均价：优先自算VWAP，其次上一快照的avgPrice（均价线缓慢变化），最后降级为当前价
                Vwap = vwapMap.TryGetValue(stockCode, out var realVwap) && realVwap > 0
                    ? realVwap
                    : (previous != null && previous.Vwap > 0 ? previous.Vwap : quote.CurrentPrice),
                VolumeReliable = volumeReliable
            };

            // 写入内存缓存
            var cache = _snapshotCache.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
            lock (cache)
            {
                cache.Add(snapshot);
                // 限制缓存大小
                if (cache.Count > Config.SnapshotCacheSize)
                {
                    cache.RemoveRange(0, cache.Count - Config.SnapshotCacheSize);
                }
            }

            // 写入批量落地缓冲
            var buffer = _snapshotBuffer.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
            lock (buffer)
            {
                buffer.Add(snapshot);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 分时数据缓存60秒（分时数据每分钟更新一次，快照10秒节奏拉全量分钟点过于频繁）
    /// </summary>
    private readonly ConcurrentDictionary<string, (List<IntradayPoint> Data, DateTime FetchedAt)> _trendsCache = new();
    private const int TrendsCacheTtlSec = 60;

    /// <summary>
    /// 保存快照到数据库 - 对应 planScheduler.js saveSnapshot
    /// </summary>
    private void SaveSnapshot(PriceSnapshot snapshot)
    {
        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                INSERT INTO price_snapshots (stockCode, price, volume, amount, timestamp, vwap, volumeReliable, cumulativeVolume)
                VALUES (@StockCode, @Price, @Volume, @Amount, @Timestamp, @Vwap, @VolumeReliable, @CumulativeVolume)";
            conn.Execute(sql, new
            {
                snapshot.StockCode,
                snapshot.Price,
                snapshot.Volume,
                snapshot.Amount,
                Timestamp = snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                snapshot.Vwap,
                snapshot.VolumeReliable,
                snapshot.CumulativeVolume
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 保存快照失败: {Code}", snapshot.StockCode);
        }
    }

    /// <summary>
    /// 批量落地快照 - 对应 planScheduler.js _flushSnapshots
    /// </summary>
    private async Task FlushSnapshotsAsync()
    {
        var now = Now;
        if ((now - _lastSnapshotFlushTime).TotalSeconds < Config.SnapshotFlushIntervalSec)
        {
            return;
        }
        _lastSnapshotFlushTime = now;

        var allSnapshots = new List<PriceSnapshot>();

        foreach (var (stockCode, buffer) in _snapshotBuffer)
        {
            List<PriceSnapshot> toFlush;
            lock (buffer)
            {
                toFlush = buffer.ToList();
                buffer.Clear();
            }
            allSnapshots.AddRange(toFlush);
        }

        if (allSnapshots.Count == 0) return;

        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                INSERT INTO price_snapshots (stockCode, price, volume, amount, timestamp, vwap, volumeReliable, cumulativeVolume)
                VALUES (@StockCode, @Price, @Volume, @Amount, @Timestamp, @Vwap, @VolumeReliable, @CumulativeVolume)";

            conn.Execute(sql, allSnapshots.Select(s => new
            {
                s.StockCode,
                s.Price,
                s.Volume,
                s.Amount,
                Timestamp = s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Vwap,
                s.VolumeReliable,
                s.CumulativeVolume
            }));

            Log.Debug("[计划调度] 批量落地 {Count} 条快照", allSnapshots.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 批量落地快照失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 数据获取缓存 - 对应 planScheduler.js fetchBatchDataWithCache / fetchDailyKlinesWithCache 等
    // ============================================================================

    /// <summary>
    /// 清理过期缓存
    /// </summary>
    private void CleanupExpiredCaches()
    {
        var now = Now;
        CleanupCache(_batchQuoteCache, now);
        CleanupCache(_capitalFlowCache, now);
        // 分时VWAP缓存：清掉超过10分钟的陈旧条目（盘中会按60s TTL自动刷新）
        foreach (var key in _trendsCache.Keys.Where(k =>
            _trendsCache.TryGetValue(k, out var v) && (now - v.FetchedAt).TotalMinutes > 10).ToList())
        {
            _trendsCache.TryRemove(key, out _);
        }
        // 日K线缓存跨天清理在 OnDayChanged 中处理
    }


    private static void CleanupCache<T>(ConcurrentDictionary<string, (T Data, DateTime ExpiresAt)> cache, DateTime now)
    {
        foreach (var key in cache.Keys.Where(k =>
            cache.TryGetValue(k, out var v) && v.ExpiresAt <= now).ToList())
        {
            cache.TryRemove(key, out _);
        }
    }

    // ============================================================================
    // 自定义提醒检查 - 对应 planScheduler.js checkCustomReminders
    // ============================================================================

    /// <summary>
    /// 自定义提醒检查 - 跨窗口触发锁 + 二次校验
    /// </summary>
}
