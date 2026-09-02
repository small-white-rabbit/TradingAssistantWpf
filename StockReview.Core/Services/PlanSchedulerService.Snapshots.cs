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
    // 限频去重 shouldEmitSignal / checkRateLimit / cleanRateLimit
    // ============================================================================

    /// <summary>
    /// 信号去重检查（只读）- 对应原版 shouldEmitSignal
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

        if (_signalStore.SignalStates.TryGetValue(key, out var previous) &&
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
        _signalStore.SignalStates[key] = new SignalStateEntry { State = state, At = NowMs };
    }

    /// <summary>
    /// 同股同类信息限频检查 checkRateLimit
    /// 滑动窗口：时间窗口内最多触发 maxCount 次
    /// </summary>
    public bool CheckRateLimit(string stockCode, string type, int maxCount = 2, int windowMs = 60 * 1000)
    {
        if (string.IsNullOrEmpty(stockCode) || string.IsNullOrEmpty(type)) return true;

        var key = $"{stockCode}:{type}";
        var now = NowMs;
        var windowStart = now - windowMs;

        var record = _signalStore.RateLimiter.GetOrAdd(key, _ => new RateLimitRecord());
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
    /// 清理过期的限频记录 cleanRateLimit
    /// 清理窗口 31 分钟，覆盖最大限频窗口（30 分钟 overnight_gap / daily_loss_breaker）
    /// </summary>
    public void CleanRateLimit()
    {
        var now = NowMs;
        const int windowMs = 31 * 60 * 1000;

        foreach (var kvp in _signalStore.RateLimiter)
        {
            var record = kvp.Value;
            lock (record)
            {
                record.Timestamps = record.Timestamps.Where(t => now - t <= windowMs).ToList();
                if (record.Timestamps.Count == 0)
                {
                    _signalStore.RateLimiter.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    // ============================================================================
    // 波内限发 _waveGateState / _waveGateAllows / _waveGatePass
    // ============================================================================

    /// <summary>
    /// 波内限发检查 - 同一价格波动波内只触发一次同类型信号
    /// 波的定义：价格单方向运动（上涨/下跌），直到出现方向反转
    /// </summary>

    // ============================================================================
    // 波内限发 _waveGateState / _waveGateAllows / _waveGatePass
    // ============================================================================

    /// <summary>
    /// 波内限发检查 - 同一价格波动波内只触发一次同类型信号
    /// 波的定义：价格单方向运动（上涨/下跌），直到出现方向反转
    /// </summary>
    private bool WaveGateAllows(string stockCode, decimal currentPrice, string signalType)
    {
        if (string.IsNullOrEmpty(stockCode)) return true;

        var state = _signalStore.WaveGateStates.GetOrAdd(stockCode, _ => new WaveGateState
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

        if (_signalStore.WaveGateStates.TryGetValue(stockCode, out var state))
        {
            lock (state)
            {
                state.LastSignalType = signalType;
            }
        }
    }

    // ============================================================================
    // 级别去重 _isLevelHitNotifiedToday / _markLevelHitNotified
    // ============================================================================
    // 级别去重 _isLevelHitNotifiedToday / _markLevelHitNotified
    // ============================================================================

    private bool IsLevelHitNotifiedToday(string planId, string level)
    {
        return _signalStore.LevelHitNotified.ContainsKey($"{planId}:{level}");
    }


    private void MarkLevelHitNotified(string planId, string level)
    {
        _signalStore.LevelHitNotified[$"{planId}:{level}"] = true;
    }

    // ============================================================================
    // 快照记录 recordSnapshots / saveSnapshot / getSnapshots / _flushSnapshots
    // ============================================================================

    /// <summary>
    /// 记录快照 recordSnapshots
    /// 10秒节奏 + 区间增量量 + 分时数据自算真实VWAP（对齐原版）
    /// </summary>

    // ============================================================================
    // 快照记录 recordSnapshots / saveSnapshot / getSnapshots / _flushSnapshots
    // ============================================================================

    /// <summary>
    /// 记录快照 recordSnapshots
    /// 10秒节奏 + 区间增量量 + 分时数据自算真实VWAP（对齐原版）
    /// </summary>
    private async Task RecordSnapshotsAsync(Dictionary<string, StockQuote> dataMap)
    {
        var now = Now;

        // 按配置间隔记录（默认10秒，对齐原版 monitorIntervalMs=10s）
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
            var cache = _marketCache.SnapshotCache.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
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
            var buffer = _marketCache.SnapshotBuffer.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
            lock (buffer)
            {
                buffer.Add(snapshot);
            }

            // 同步写入秒级轨迹（HTTP 轮询降级模式下轨迹仍有 10 秒粒度，保证时间窗口检测可用）
            RecordLiveTrail(stockCode, quote.CurrentPrice, now);
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 秒级价格轨迹 - 时间窗口快速涨跌检测（替代快照 bars 计数窗口）
    // ============================================================================

    /// <summary>秒级轨迹保留时长（数据收集类股票的滚动裁剪窗口，覆盖最大检测窗口 15 分钟 + 余量）</summary>
    private static readonly TimeSpan LiveTrailRetention = TimeSpan.FromMinutes(16);

    /// <summary>轨迹数量兜底上限（重点股全天秒级理论上限 ~15000 点，留余量防异常膨胀）</summary>
    private const int LiveTrailMaxPoints = 20000;

    /// <summary>
    /// 记录秒级价格轨迹点（富途推送每秒多次调用 + 快照 tick 10秒兜底）。
    /// 价格不变且距上点不足 5 秒时不追加（控制内存，无增量信息）。
    /// 保留策略分级：买/卖类重点监控股全量保留当日轨迹（供形态匹配等后续分析）；
    /// 数据收集类（watch/无计划）降级为 16 分钟滚动裁剪。
    /// </summary>
    public void RecordLiveTrail(string stockCode, decimal price, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(stockCode) || price <= 0) return;

        var trail = _marketCache.LiveTrail.GetOrAdd(stockCode, _ => new List<LiveTrailPoint>());
        lock (trail)
        {
            if (trail.Count > 0)
            {
                var lastPoint = trail[^1];
                // 时间回退（时钟异常/乱序推送）：忽略该点
                if (timestamp < lastPoint.Timestamp) return;
                if (lastPoint.Price == price && timestamp - lastPoint.Timestamp < TimeSpan.FromSeconds(5)) return;
            }

            trail.Add(new LiveTrailPoint { Price = price, Timestamp = timestamp });

            // 重点监控股：全量保留当日轨迹（跨天由 OnDayChanged 清理），仅做数量上限兜底
            if (!IsPriorityMonitoredStock(stockCode))
            {
                // 数据收集类：16 分钟滚动裁剪（按交易连续时间，与检测窗口时间轴一致，
                // 跨午休时上午尾点不会被真实时间裁掉而破坏连贯性）
                var cutoff = ToSessionTime(timestamp) - LiveTrailRetention;
                var removeCount = 0;
                while (removeCount < trail.Count - 1 && ToSessionTime(trail[removeCount].Timestamp) < cutoff)
                {
                    removeCount++;
                }
                if (removeCount > 0) trail.RemoveRange(0, removeCount);
            }

            // 数量兜底
            if (trail.Count > LiveTrailMaxPoints)
            {
                trail.RemoveRange(0, trail.Count - LiveTrailMaxPoints);
            }
        }
    }

    /// <summary>
    /// 是否重点监控股（存在买入/卖出类可监控计划）：
    /// 此类股票通常仅几只，秒级轨迹全量保留供形态匹配等深度分析。
    /// </summary>
    private bool IsPriorityMonitoredStock(string stockCode)
    {
        var store = _tradePlanStore;
        if (store == null) return false;
        return store.TodayPlans
            .Concat(store.MonitoringPlans)
            .Any(p => p.StockCode == stockCode && p.PlanType != "watch" && IsPlanMonitorable(p));
    }

    /// <summary>
    /// 交易连续时间映射：剥离午休（11:30-13:00）空白时段，使上午/下午价格轨迹在时间轴上连贯。
    /// 13:00 后的时间戳前移 90 分钟（紧接 11:30），午休中的点贴到 11:30。
    /// 行情数据本身是连贯的——上午收盘最后一笔 11:29:59 与下午首笔 13:00:00 是相邻数据点，
    /// 剥离空白后两者仅隔 1 秒，跨午休的涨跌幅即可正常参与时间窗口判定。
    /// </summary>
    private static DateTime ToSessionTime(DateTime ts)
    {
        if (ts.Hour >= 13)
            return ts.AddMinutes(-90); // 下午时间前移，紧接 11:30 上午尾
        if ((ts.Hour == 11 && ts.Minute >= 30) || ts.Hour == 12)
            return new DateTime(ts.Year, ts.Month, ts.Day, 11, 30, 0); // 午休中（11:30-13:00）的点贴到 11:30
        return ts;
    }

    /// <summary>
    /// 基于秒级轨迹的时间窗口快速涨跌检测（推送即时触发，替代快照计数窗口）。
    /// 滑动窗口任意子区间语义：并非"满 3 分钟才触发"，而是窗口内任意时间段达到阈值即触发——
    /// 30 秒跌 1% 触发，3 分钟跌 1% 也触发。
    /// 实现：一遍扫描窗口内轨迹，计算最大回撤（任意高点→其后低点）与最大反弹（任意低点→其后高点），
    /// 任一达到窗口阈值即命中，天然覆盖"先涨后跌首尾抵消"的场景（旧首尾比较会漏检）。
    /// 与 DetectMultiWindowRapid 的区别：
    /// - 窗口按真实时间戳划定（3/10/15 分钟），不再依赖 18/60/90 根快照预热；
    /// - 午休连贯：窗口基于交易连续时间（剥离午休空白），跨午休涨跌幅正常判定；
    /// - 开盘盲区消除：9:25-9:30 竞价匹配价直接计入轨迹，开盘即有基准数据。
    /// </summary>
    public RapidMatch? DetectRapidByTimeTrail(string stockCode)
    {
        if (string.IsNullOrEmpty(stockCode)) return null;
        if (!_marketCache.LiveTrail.TryGetValue(stockCode, out var trail)) return null;

        lock (trail)
        {
            if (trail.Count < 2) return null;
            var last = trail[^1];
            var lastSessionTs = ToSessionTime(last.Timestamp);

            RapidMatch? bestMatch = null;
            DateTime bestToTs = DateTime.MinValue;

            foreach (var window in Config.RapidWindows)
            {
                // Bars × SnapshotIntervalSec 折算真实窗口分钟数（18 bars × 10s = 3 分钟）
                var windowMinutes = Math.Max(0.1, window.Bars * (double)Config.SnapshotIntervalSec / 60.0);
                var windowStartSession = lastSessionTs.AddMinutes(-windowMinutes);

                // 定位窗口内首个轨迹点（基于交易连续时间，trail 按时间升序）
                var startIdx = trail.Count - 1;
                for (var i = 0; i < trail.Count; i++)
                {
                    if (ToSessionTime(trail[i].Timestamp) >= windowStartSession) { startIdx = i; break; }
                }
                if (startIdx >= trail.Count - 1) continue; // 窗口内不足 2 个点

                // 一遍扫描：最大反弹（低点→其后高点）与最大回撤（高点→其后低点）
                var runMin = trail[startIdx].Price;
                var runMax = trail[startIdx].Price;
                var runMinTs = ToSessionTime(trail[startIdx].Timestamp);
                var runMaxTs = runMinTs;
                var maxUp = 0m; var maxUpFromTs = runMinTs; var maxUpToTs = lastSessionTs;
                var maxDown = 0m; var maxDownFromTs = runMinTs; var maxDownToTs = lastSessionTs;

                for (var i = startIdx; i < trail.Count; i++)
                {
                    var p = trail[i].Price;
                    var ts = ToSessionTime(trail[i].Timestamp);

                    var up = (p - runMin) / runMin * 100;
                    if (up > maxUp) { maxUp = up; maxUpFromTs = runMinTs; maxUpToTs = ts; }

                    var down = (runMax - p) / runMax * 100;
                    if (down > maxDown) { maxDown = down; maxDownFromTs = runMaxTs; maxDownToTs = ts; }

                    if (p < runMin) { runMin = p; runMinTs = ts; }
                    if (p > runMax) { runMax = p; runMaxTs = ts; }
                }

                // 方向判定：回撤/反弹达到阈值即命中；
                // 两者同时达到（先涨后跌或反之）时，以后发生的方向为准（提醒时效关注"刚发生的运动"），
                // 时间相同再比幅度
                string dir;
                decimal changePct;
                DateTime fromTs;
                if (maxDown >= window.Pct && maxUp >= window.Pct)
                {
                    var downLater = maxDownToTs > maxUpToTs ||
                                    (maxDownToTs == maxUpToTs && maxDown >= maxUp);
                    if (downLater)
                    {
                        dir = "down"; changePct = -maxDown; fromTs = maxDownFromTs;
                    }
                    else
                    {
                        dir = "up"; changePct = maxUp; fromTs = maxUpFromTs;
                    }
                }
                else if (maxDown >= window.Pct)
                {
                    dir = "down"; changePct = -maxDown; fromTs = maxDownFromTs;
                }
                else if (maxUp >= window.Pct)
                {
                    dir = "up"; changePct = maxUp; fromTs = maxUpFromTs;
                }
                else
                {
                    continue;
                }

                // 触发区间的实际时长（低/高点 → 末点）
                var toTs = dir == "down" ? maxDownToTs : maxUpToTs;
                var spanMin = Math.Max(0.1, (toTs - fromTs).TotalMinutes);

                // 窗口择优：同方向时优先满足条件的最长窗口（更可靠），短窗口幅度远超阈值（>2倍）时优先短窗口（更及时）；
                // 方向不同时保留后发生者（提醒时效关注"刚发生的运动"，长窗口的反向命中不应覆盖更及时的方向）
                var ratio = Math.Abs(changePct) / window.Pct;
                var replace = bestMatch == null
                    || (dir == bestMatch.Direction && (window.Bars > bestMatch.WindowBars || ratio > 2))
                    || (dir != bestMatch.Direction && toTs > bestToTs);
                if (replace)
                {
                    bestMatch = new RapidMatch
                    {
                        Direction = dir,
                        ChangePct = changePct,
                        WindowBars = window.Bars,
                        WindowLabel = window.Label,
                        DownLabel = window.DownLabel,
                        CooldownMs = window.CooldownMs,
                        WindowMinutes = spanMin
                    };
                    bestToTs = toTs;
                }
            }

            return bestMatch;
        }
    }

    /// <summary>
    /// 快速涨跌信号冷却检查（含恶化升级穿透）：
    /// 冷却期内若幅度显著恶化（≥ 上次已提醒幅度的 1.5 倍），穿透冷却立即再提醒，
    /// 避免"提醒过 -1% 后一路跌到 -3% 仍静默"的漏报。
    /// </summary>
    public bool CanEmitRapidSignal(string planId, string direction, RapidMatch match)
    {
        var key = $"{planId}:rapid_window_{direction}";

        if (CanEmitSignal(key, "triggered", match.CooldownMs)) return true;

        if (_signalStore.SignalStates.TryGetValue(key, out var prev) &&
            prev.State == "triggered" && prev.Price.HasValue)
        {
            var lastAbs = Math.Abs(prev.Price.Value);
            if (lastAbs > 0 && Math.Abs(match.ChangePct) >= lastAbs * 1.5m)
            {
                Log.Information("[计划调度] 快速涨跌恶化升级穿透冷却: {Key} 上次 {Last}% 本次 {Now}%",
                    key, prev.Price.Value, match.ChangePct);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 提交快速涨跌信号状态（记录幅度供恶化升级穿透判定）
    /// </summary>
    public void CommitRapidSignalState(string planId, string direction, RapidMatch match)
    {
        var key = $"{planId}:rapid_window_{direction}";
        _signalStore.SignalStates[key] = new SignalStateEntry
        {
            State = "triggered",
            At = NowMs,
            Price = match.ChangePct,
            Reason = match.WindowLabel
        };
    }

    /// <summary>
    /// 分时数据缓存60秒（分时数据每分钟更新一次，快照10秒节奏拉全量分钟点过于频繁）
    private const int TrendsCacheTtlSec = 60;

    /// <summary>
    /// 保存快照到数据库 saveSnapshot
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
    /// 批量落地快照 _flushSnapshots
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

        foreach (var (stockCode, buffer) in _marketCache.SnapshotBuffer)
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

            // 事务包裹：Dapper 对 IEnumerable 参数逐行执行，无事务时每行独立自动提交，
            // 一次 flush 数百~数千行 = 数百次 fsync，且与写锁长时间争抢
            using var tx = conn.BeginTransaction();
            try
            {
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
                }), tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            Log.Debug("[计划调度] 批量落地 {Count} 条快照", allSnapshots.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 批量落地快照失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 数据获取缓存 fetchBatchDataWithCache / fetchDailyKlinesWithCache 等
    // ============================================================================



    // ============================================================================
    // 自定义提醒检查 checkCustomReminders
    // ============================================================================

    /// <summary>
    /// 自定义提醒检查 - 跨窗口触发锁 + 二次校验
    /// </summary>
}
