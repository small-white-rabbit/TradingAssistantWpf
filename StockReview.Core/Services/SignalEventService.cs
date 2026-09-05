using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 信号事件存储服务
/// 记录盘中信号事件、收盘后评估信号成功/失败、统计历史胜率供自进化引擎使用
/// 持久化到 appConfig 表
/// </summary>
public partial class SignalEventService
{
    private readonly IDatabaseService _db;
    private const string EventsKey = "pet_signal_events";
    private const string StatsKey = "pet_signal_stats";
    private const string AttributionKey = "pet_evolution_attribution";
    private const string MissedAnalysisKey = "pet_missed_sell_analysis";
    /// <summary>
    /// 内存与 appConfig JSON 中保留的交易日期键上限（滚动窗口）。
    /// 自进化统计窗口 EvolutionWindowDays=5 个交易日、漏报复盘摘要留 7 天、
    /// price_snapshots 保留 7 天，7 个有事件的日期键足以覆盖全部消费方；
    /// 超出的最旧日期键在加载/记录时裁剪并回写——pet_signal_events 单条 JSON
    /// 生产实测曾膨胀至 27MB（17 天 1.7 万事件），启动全量反序列化与盘中全量
    /// 重写是 LOH 碎片主因。
    /// </summary>
    private const int MaxHistoryDays = 7;
    public const int EvolutionWindowDays = 5;

    // 波次划分配置
    private const double WaveReversalPct = 0.5;
    private const double WaveMinRisePct = 0.8;
    private const long WaveTopToleranceMs = 5 * 60 * 1000L;
    private const double WaveCaptureHigh = 0.7;
    private const double WaveCaptureLow = 0.3;
    private const double WaveMinSuccessDepthPct = 0.8;
    public const double SignalMuteThreshold = 0.35;
    public const int MaxAlertsPerWave = 3;

    // 漏报复盘配置
    private const double MissedWaveMinRisePct = 1.0;
    private const double MissedWaveMinDepthPct = 1.5;
    private const int MissedAnalysisKeepDays = 7;

    // 信号评估配置
    private static readonly EvalConfig EvalConfig = new()
    {
        BuySignalEvalWindows = new[] { 5, 10, 30 },
        BuySuccessThreshold = 0.5,
        SellSignalEvalWindows = new[] { 5, 10, 30 },
        SellSuccessThreshold = -0.3,
        RapidRiseEvalWindows = new[] { 5, 10 },
        RapidRiseSuccessThreshold = 0.3,
        RapidFallEvalWindows = new[] { 5, 10 },
        RapidFallSuccessThreshold = -0.3
    };

    // 信号类型分类
    public static readonly HashSet<string> BuySignalTypes = new()
    {
        "price_alert_up", "rapid_rise", "breakthrough", "support_bounce",
        "bottom_divergence", "ma_support", "volume_breakthrough"
    };

    public static readonly HashSet<string> SellSignalTypes = new()
    {
        "surge_pullback", "volume_stagnant", "ma_suppress", "top_divergence",
        "volume_divergence", "double_top", "fishing_line", "triple_top",
        "platform_breakdown", "high_deviation_pullback", "vwap_breakdown",
        "vwap_rejection", "vwap_slope_down", "late_session_exit",
        "break_ma5", "break_ma10", "break_ma30", "break_support",
        "weak_rebound_failure", "price_alert_down", "sell_resonance",
        "multifactor_resonance", "spike_volume_top",
        "deep_drop_rebound", "atr_stop_loss", "atr_trailing_stop"
    };

    // 内存数据（_events 会被调度线程写、UI 线程读，所有访问必须持 _eventsLock）
    private readonly object _eventsLock = new();
    private Dictionary<string, List<SignalEvent>> _events = new();
    private Dictionary<string, SignalTypeStat> _stats = new();
    private AttributionLedger _attribution = new();
    private Dictionary<string, MissedAnalysisSummary> _missedAnalysis = new();

    // 写入节流：避免每次 RecordEvent 都全量序列化写入 DB
    private long _lastSaveEventsMs;
    private long _lastSaveStatsMs;
    private const long SaveThrottleMs = 5000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        // 兼容旧版备份的 camelCase 字段（WPF 自身写入为 PascalCase）
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public SignalEventService(IDatabaseService db)
    {
        _db = db;
        LoadFromStorage();
    }

    // ============ 持久化 ============

    private void LoadFromStorage()
    {
        try
        {
            var eventsRow = _db.GetById("appConfig", EventsKey);
            if (eventsRow != null && eventsRow.TryGetValue("value", out var evVal) && evVal != null)
            {
                _events = JsonSerializer.Deserialize<Dictionary<string, List<SignalEvent>>>(evVal.ToString()!, JsonOpts) ?? new();
                MaterializeEventsMetadata(_events);

                // 【2026-09-06 改路由】历史清洗：rapid_/target_/stop_ 属"提醒"类事件，
                // 误写入导致单条 JSON 膨胀至 28.5MB（LOH 碎片主因）。加载时一次性剔除。
                // 生产侧写入点已同日移除（PlanSchedulerService.Checking.cs）。
                var prunedEvents = PruneNonSignalEvents(_events);
                // 【2026-09-06 滚动窗口】只保留最近 MaxHistoryDays 个交易日期键，
                // 更旧的事件自进化/漏报复盘均不再消费，裁掉并立即回写收缩。
                var prunedDays = PruneOldEvents(_events, MaxHistoryDays);
                if (prunedEvents > 0 || prunedDays > 0)
                {
                    Log.Information("[SignalEvent] 加载裁剪：剔除提醒类事件 {Count} 条、过期日期 {Days} 天（保留最近 {Keep} 个交易日）",
                        prunedEvents, prunedDays, MaxHistoryDays);
                    _lastSaveEventsMs = 0; // 绕过 5s 节流，立即持久化收缩后的数据
                    SaveEvents();
                }
            }

            var statsRow = _db.GetById("appConfig", StatsKey);
            if (statsRow != null && statsRow.TryGetValue("value", out var stVal) && stVal != null)
            {
                _stats = JsonSerializer.Deserialize<Dictionary<string, SignalTypeStat>>(stVal.ToString()!, JsonOpts) ?? new();
                var prunedStats = _stats.Keys.Where(IsNonSignalType).ToList();
                if (prunedStats.Count > 0)
                {
                    foreach (var k in prunedStats) _stats.Remove(k);
                    _lastSaveStatsMs = 0;
                    SaveStats();
                }
            }

            var attrRow = _db.GetById("appConfig", AttributionKey);
            if (attrRow != null && attrRow.TryGetValue("value", out var attrVal) && attrVal != null)
            {
                _attribution = JsonSerializer.Deserialize<AttributionLedger>(attrVal.ToString()!, JsonOpts) ?? new();
            }
            if (_attribution.Entries == null) _attribution.Entries = new();

            var missedRow = _db.GetById("appConfig", MissedAnalysisKey);
            if (missedRow != null && missedRow.TryGetValue("value", out var mVal) && mVal != null)
            {
                _missedAnalysis = JsonSerializer.Deserialize<Dictionary<string, MissedAnalysisSummary>>(mVal.ToString()!, JsonOpts) ?? new();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 加载失败");
        }
    }

    /// <summary>
    /// 反序列化后 Metadata 的 object 值实际是 JsonElement，与运行期写入的 CLR 类型不一致，
    /// 会导致 `is double` / `is List&lt;object&gt;` / `is Dictionary&lt;string, object&gt;` 等模式匹配静默失败
    /// （自进化重放与因子奖励统计全部失明）。加载时一次性物化为 CLR 类型，下游零改动。
    /// </summary>
    private static void MaterializeEventsMetadata(Dictionary<string, List<SignalEvent>> events)
    {
        foreach (var list in events.Values)
        {
            if (list == null) continue;
            foreach (var evt in list)
            {
                if (evt.Metadata == null || evt.Metadata.Count == 0) continue;
                evt.Metadata = MaterializeDict(evt.Metadata);
            }
        }
    }

    private static Dictionary<string, object> MaterializeDict(Dictionary<string, object> src)
    {
        var result = new Dictionary<string, object>(src.Count);
        foreach (var (k, v) in src)
            result[k] = v is JsonElement je ? MaterializeValue(je) : v;
        return result;
    }

    private static object MaterializeValue(JsonElement je) => je.ValueKind switch
    {
        // 消费方统一按 is double 匹配数字，故统一物化为 double
        JsonValueKind.Number => je.GetDouble(),
        JsonValueKind.String => je.GetString() ?? "",
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => je.EnumerateArray().Select(MaterializeValue).ToList(),
        JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => MaterializeValue(p.Value)),
        _ => je.ToString()
    };


    private void SaveEvents()
    {
        string json;
        lock (_eventsLock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastSaveEventsMs < SaveThrottleMs) return;
            _lastSaveEventsMs = now;
            // 锁内只做序列化（CPU），DB IO 放锁外，避免持锁跨 IO
            json = JsonSerializer.Serialize(_events, JsonOpts);
        }
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = EventsKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 保存事件失败");
            lock (_eventsLock)
            {
                var dates = _events.Keys.OrderBy(k => k).ToList();
                while (dates.Count > MaxHistoryDays)
                {
                    var oldest = dates[0];
                    _events.Remove(oldest);
                    dates.RemoveAt(0);
                    try
                    {
                        var jsonRetry = JsonSerializer.Serialize(_events, JsonOpts);
                        _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = EventsKey, ["value"] = jsonRetry });
                        Log.Warning("[SignalEvent] 配额不足，裁剪旧数据后保存成功");
                        return;
                    }
                    catch { /* 继续裁剪 */ }
                }
            }
        }
    }


    private void SaveStats()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastSaveStatsMs < SaveThrottleMs) return;
        _lastSaveStatsMs = now;
        try
        {
            var json = JsonSerializer.Serialize(_stats, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = StatsKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 保存统计失败");
            // 裁剪 history 到最近 10 条后重试
            foreach (var stat in _stats.Values)
            {
                if (stat.History != null && stat.History.Count > 10)
                    stat.History = stat.History.TakeLast(10).ToList();
            }
            try
            {
                var json = JsonSerializer.Serialize(_stats, JsonOpts);
                _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = StatsKey, ["value"] = json });
            }
            catch (Exception e2)
            {
                Log.Error(e2, "[SignalEvent] 裁剪后仍无法保存统计");
            }
        }
    }


    private void SaveAttribution()
    {
        try
        {
            _attribution.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = JsonSerializer.Serialize(_attribution, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = AttributionKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 保存归因账本失败");
        }
    }


    private void SaveMissedAnalysis()
    {
        try
        {
            var keep = _missedAnalysis.Keys.OrderBy(k => k).TakeLast(MissedAnalysisKeepDays).ToHashSet();
            var toRemove = _missedAnalysis.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var k in toRemove) _missedAnalysis.Remove(k);

            var json = JsonSerializer.Serialize(_missedAnalysis, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = MissedAnalysisKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 保存漏报分析失败");
        }
    }

    /// <summary>
    /// 从存储全量重载
    /// </summary>
    public void ReloadFromStorage() => LoadFromStorage();

    // ============ 东八区日期键 ============

    private static string TodayKey()
    {
        var tz = CnTimeZone.Get;
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return $"{shanghai.Year:0000}-{shanghai.Month:00}-{shanghai.Day:00}";
    }

    // ============ 事件记录 ============

    // 【2026-09-06 改路由】提醒类事件前缀：快速涨跌/目标价/止损已改走宠物提醒通道
    // （PlanSchedulerService.Checking.cs），信号事件库只收录买卖信号。
    private static readonly string[] NonSignalTypePrefixes = { "rapid_", "target_", "stop_" };

    internal static bool IsNonSignalType(string? signalType)
    {
        if (string.IsNullOrEmpty(signalType)) return false;
        foreach (var p in NonSignalTypePrefixes)
        {
            if (signalType.StartsWith(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static int PruneNonSignalEvents(Dictionary<string, List<SignalEvent>> events)
    {
        var removed = 0;
        foreach (var date in events.Keys.ToList())
        {
            var list = events[date];
            if (list == null || list.Count == 0) continue;
            var kept = list.Where(e => !IsNonSignalType(e.SignalType)).ToList();
            if (kept.Count == list.Count) continue;
            removed += list.Count - kept.Count;
            if (kept.Count == 0) events.Remove(date);
            else events[date] = kept;
        }
        return removed;
    }

    /// <summary>
    /// 滚动窗口裁剪：日期键为 yyyy-MM-dd，字典序即时间序，只保留最近 keepDays 个。
    /// 返回被移除的日期键数量。调用方负责在裁剪后持久化回写。
    /// </summary>
    internal static int PruneOldEvents(Dictionary<string, List<SignalEvent>> events, int keepDays)
    {
        if (events.Count <= keepDays) return 0;
        var oldKeys = events.Keys.OrderBy(k => k).Take(events.Count - keepDays).ToList();
        foreach (var k in oldKeys) events.Remove(k);
        return oldKeys.Count;
    }

    /// <summary>
    /// 记录信号事件
    /// </summary>
    public SignalEvent RecordEvent(SignalEventInput input)
    {
        // 改路由守卫：提醒类事件不得进入信号库，防御未来误回归
        if (IsNonSignalType(input.SignalType))
        {
            Log.Debug("[SignalEvent] 拒绝提醒类事件写入（{SignalType}，应走宠物提醒通道）", input.SignalType);
            return null!;
        }

        var today = TodayKey();

        var ts = input.Timestamp > 0 ? input.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new SignalEvent
        {
            Id = $"{input.StockCode}_{ts}_{input.SignalType}",
            StockCode = input.StockCode,
            StockName = input.StockName ?? "",
            SignalType = input.SignalType,
            SignalLabel = input.SignalLabel ?? input.SignalType,
            Price = input.Price,
            Timestamp = ts,
            TimeStr = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            SnapshotIndex = input.SnapshotIndex,
            Metadata = input.Metadata ?? new Dictionary<string, object>(),
            DataMode = input.DataMode ?? "snapshot",
            Evaluation = null,
            Evaluated = false,
            IsOptimized = false,
            OptimizationVersion = 0
        };

        lock (_eventsLock)
        {
            if (!_events.ContainsKey(today))
                _events[today] = new List<SignalEvent>();

            // 去重：同一股票同一信号类型在30秒内只记录一次
            var existing = _events[today].Find(e =>
                e.StockCode == record.StockCode &&
                e.SignalType == record.SignalType &&
                Math.Abs(e.Timestamp - record.Timestamp) < 30000);
            if (existing != null) return existing;

            _events[today].Add(record);

            // 滚动窗口：跨天后新日期键使总数超窗时，挤出最旧日期键
            // （随后 SaveEvents 全量序列化的就是收缩后的字典，内存/JSON 同步有界）
            if (_events.Count > MaxHistoryDays)
            {
                PruneOldEvents(_events, MaxHistoryDays);
            }
        }   // 锁内不做 IO：持久化移到锁外

        SaveEvents();
        return record;
    }

    /// <summary>
    /// 获取指定日期的信号事件
    /// </summary>
    public List<SignalEvent> GetEventsByDate(string date)
    {
        lock (_eventsLock)
        {
            // 始终返回副本：避免外部持有内部 List 引用造成二次竞争
            return _events.TryGetValue(date, out var list) ? list.ToList() : new List<SignalEvent>();
        }
    }

    /// <summary>
    /// 获取今日信号事件
    /// </summary>
    public List<SignalEvent> GetTodayEvents(string? stockCode = null)
    {
        var today = TodayKey();
        lock (_eventsLock)
        {
            var events = _events.TryGetValue(today, out var list) ? list : new List<SignalEvent>();
            // 始终返回副本：避免外部持有内部 List 引用造成二次竞争
            return stockCode != null
                ? events.Where(e => e.StockCode == stockCode).ToList()
                : events.ToList();
        }
    }

    // ============ 信号评估 ============

    /// <summary>
    /// 评估单个信号事件
    /// </summary>

    // ============ 波次划分 (zigzag) ============

    /// <summary>
    /// zigzag 波次划分：把当日走势切成"起涨谷→峰顶→回落谷"的波序列
    /// </summary>
    public static List<Wave> SegmentWaves(List<Snapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 5) return new List<Wave>();
        var prices = snapshots.Select(s => (double)s.Price).ToList();
        long GetTs(int idx) => new DateTimeOffset(snapshots[idx].SnapshotAt).ToUnixTimeMilliseconds();
        double rev = WaveReversalPct / 100;

        // 1. zigzag 找 pivot
        var pivots = new List<Pivot>();
        int trend = 0;
        int extIdx = 0;
        for (int i = 1; i < prices.Count; i++)
        {
            double p = prices[i];
            if (trend == 0)
            {
                if (p >= prices[extIdx] * (1 + rev)) { trend = 1; pivots.Add(new Pivot { Idx = extIdx, Type = 'L', Price = prices[extIdx] }); extIdx = i; }
                else if (p <= prices[extIdx] * (1 - rev)) { trend = -1; pivots.Add(new Pivot { Idx = extIdx, Type = 'H', Price = prices[extIdx] }); extIdx = i; }
                else if (p < prices[extIdx]) extIdx = i;
            }
            else if (trend == 1)
            {
                if (p > prices[extIdx]) extIdx = i;
                else if (p <= prices[extIdx] * (1 - rev)) { trend = -1; pivots.Add(new Pivot { Idx = extIdx, Type = 'H', Price = prices[extIdx] }); extIdx = i; }
            }
            else
            {
                if (p < prices[extIdx]) extIdx = i;
                else if (p >= prices[extIdx] * (1 + rev)) { trend = 1; pivots.Add(new Pivot { Idx = extIdx, Type = 'L', Price = prices[extIdx] }); extIdx = i; }
            }
        }

        // 2. L→H 相邻对构成波
        var waves = new List<Wave>();
        for (int i = 0; i + 1 < pivots.Count; i++)
        {
            var a = pivots[i];
            var b = pivots[i + 1];
            if (a.Type != 'L' || b.Type != 'H') continue;
            double risePct = a.Price > 0 ? (b.Price - a.Price) / a.Price * 100 : 0;
            if (risePct < WaveMinRisePct) continue;

            // 波后回落谷底
            int endIdx = prices.Count - 1;
            for (int j = i + 2; j < pivots.Count; j++)
            {
                if (pivots[j].Type == 'L') { endIdx = pivots[j].Idx; break; }
            }
            int bottomIdx = b.Idx;
            double bottomPrice = b.Price;
            for (int j = b.Idx + 1; j <= endIdx; j++)
            {
                if (prices[j] < bottomPrice) { bottomPrice = prices[j]; bottomIdx = j; }
            }
            double depthPct = b.Price > 0 ? (b.Price - bottomPrice) / b.Price * 100 : 0;

            waves.Add(new Wave
            {
                WaveIdx = waves.Count,
                TroughIdx = a.Idx, TroughTime = GetTs(a.Idx), TroughPrice = a.Price,
                PeakIdx = b.Idx, PeakTime = GetTs(b.Idx), PeakPrice = b.Price,
                RisePct = risePct, EndIdx = endIdx,
                BottomIdx = bottomIdx, BottomTime = GetTs(bottomIdx), BottomPrice = bottomPrice,
                DepthPct = depthPct
            });
        }
        return waves;
    }

    // ============ 批量评估 ============

    /// <summary>
    /// 评估指定日期的所有信号事件
    /// </summary>

    // ============ 近期窗口统计 ============

    /// <summary>
    /// 近 N 个交易日的已评估事件现算统计
    /// </summary>
    public Dictionary<string, RecentSignalStat> GetRecentStats(int days = EvolutionWindowDays)
    {
        // 锁内取快照，锁外计算（Monitor 可重入，锁内遍历副本无竞态）
        Dictionary<string, List<SignalEvent>> snapshot;
        lock (_eventsLock)
        {
            snapshot = _events.OrderBy(kvp => kvp.Key).TakeLast(days)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }
        var stats = new Dictionary<string, RecentSignalStat>();

        foreach (var dk in snapshot.Keys)
        {
            if (!snapshot.TryGetValue(dk, out var events)) continue;
            foreach (var evt in events)
            {
                if (!evt.Evaluated || evt.Evaluation == null) continue;
                if (evt.SignalType == "hold_filtered") continue;
                var type = evt.SignalType;
                if (!stats.ContainsKey(type))
                {
                    stats[type] = new RecentSignalStat
                    {
                        SignalType = type, SignalLabel = evt.SignalLabel,
                        Total = 0, Success = 0, Fail = 0, Neutral = 0,
                        AvgChangePct = 0, AvgReward = 0.5,
                        NearDayHighCount = 0, BeforeMaxDrawdownCount = 0, NearDayLowCount = 0,
                        WaveHighCount = 0, WaveLowCount = 0
                    };
                }
                var stat = stats[type];
                stat.Total++;
                if (evt.Evaluation.Result == "success") stat.Success++;
                else if (evt.Evaluation.Result == "fail") stat.Fail++;
                else stat.Neutral++;

                double prevAvg = stat.AvgChangePct * (stat.Total - 1);
                stat.AvgChangePct = (prevAvg + evt.Evaluation.MaxChangePct) / stat.Total;
                double reward = evt.Evaluation.Reward ?? 0.5;
                double prevRewardAvg = stat.AvgReward * (stat.Total - 1);
                stat.AvgReward = (prevRewardAvg + reward) / stat.Total;

                if (evt.Evaluation.NearDayHigh) stat.NearDayHighCount++;
                if (evt.Evaluation.BeforeMaxDrawdown) stat.BeforeMaxDrawdownCount++;
                if (evt.Evaluation.NearDayLow) stat.NearDayLowCount++;
                if (evt.Evaluation.WaveHigh) stat.WaveHighCount++;
                if (evt.Evaluation.WaveLow) stat.WaveLowCount++;
            }
        }
        return stats;
    }

    // ============ 质量分类 ============

    /// <summary>
    /// 统一质量分类（波次感知）
    /// </summary>

    // ============ 按股票维度质量统计 ============

    public Dictionary<string, StockQualityStat> GetQualityStatsByStock(int days = EvolutionWindowDays)
    {
        Dictionary<string, List<SignalEvent>> snapshot;
        lock (_eventsLock)
        {
            snapshot = _events.OrderBy(kvp => kvp.Key).TakeLast(days)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }
        var today = TodayKey();
        var byStock = new Dictionary<string, StockQualityStat>();

        foreach (var dk in snapshot.Keys)
        {
            if (!snapshot.TryGetValue(dk, out var events)) continue;
            foreach (var evt in events)
            {
                if (evt.SignalType == "hold_filtered") continue;
                if (!SellSignalTypes.Contains(evt.SignalType)) continue;
                var key = evt.StockCode;
                if (string.IsNullOrEmpty(key)) continue;
                if (!byStock.ContainsKey(key))
                {
                    byStock[key] = new StockQualityStat
                    {
                        StockCode = key, StockName = evt.StockName ?? key,
                        TodayTotal = 0, Total = 0, High = 0, Mid = 0, Low = 0
                    };
                }
                var s = byStock[key];
                if (dk == today) s.TodayTotal++;
                if (!evt.Evaluated || evt.Evaluation == null) continue;
                s.Total++;
                var q = ClassifyQuality(evt.Evaluation);
                if (q == "high") s.High++;
                else if (q == "low") s.Low++;
                else s.Mid++;
            }
        }
        return byStock;
    }

    // ============ 两段式回放 ============

    /// <summary>
    /// 两段式回放（自进化搜索引擎核心）
    /// </summary>

    // ============ 因子级奖励统计 ============

    public Dictionary<string, FactorRewardStat> GetFactorRewardStats(int days = EvolutionWindowDays)
    {
        Dictionary<string, List<SignalEvent>> snapshot;
        lock (_eventsLock)
        {
            snapshot = _events.OrderBy(kvp => kvp.Key).TakeLast(days)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }
        var factorStats = new Dictionary<string, FactorRewardStat>();

        foreach (var dk in snapshot.Keys)
        {
            if (!snapshot.TryGetValue(dk, out var events)) continue;
            foreach (var evt in events)
            {
                if (!evt.Evaluated || evt.Evaluation == null) continue;
                if (evt.Metadata == null || !evt.Metadata.TryGetValue("factorScores", out var fsRaw) || fsRaw is not Dictionary<string, object> fsDict) continue;
                double reward = evt.Evaluation.Reward ?? 0.5;
                bool isOptimalHit = evt.Evaluation.NearDayHigh || evt.Evaluation.BeforeMaxDrawdown;
                bool isHighQuality = (reward > 0.65 || isOptimalHit) && !evt.Evaluation.NearDayLow;
                bool isLowQuality = reward < 0.4 || evt.Evaluation.NearDayLow;

                foreach (var (fkey, fscore) in fsDict)
                {
                    double fs = fscore is double d ? d : 0;
                    if (!factorStats.ContainsKey(fkey))
                    {
                        factorStats[fkey] = new FactorRewardStat
                        {
                            Total = 0, RewardSum = 0, ScoreSum = 0, HighRewardCount = 0, OptimalHitCount = 0,
                            HighQualityCount = 0, HighQualityScoreSum = 0,
                            LowQualityCount = 0, LowQualityScoreSum = 0
                        };
                    }
                    var fs_ = factorStats[fkey];
                    fs_.Total++;
                    fs_.RewardSum += reward;
                    fs_.ScoreSum += fs;
                    if (reward > 0.6) fs_.HighRewardCount++;
                    if (isOptimalHit) fs_.OptimalHitCount++;
                    if (isHighQuality) { fs_.HighQualityCount++; fs_.HighQualityScoreSum += fs; }
                    if (isLowQuality) { fs_.LowQualityCount++; fs_.LowQualityScoreSum += fs; }
                }
            }
        }

        foreach (var fs in factorStats.Values)
        {
            fs.AvgReward = fs.Total > 0 ? fs.RewardSum / fs.Total : 0.5;
            fs.AvgScore = fs.Total > 0 ? fs.ScoreSum / fs.Total : 0;
            fs.HighRewardRate = fs.Total > 0 ? (double)fs.HighRewardCount / fs.Total : 0;
            fs.OptimalHitRate = fs.Total > 0 ? (double)fs.OptimalHitCount / fs.Total : 0;
            fs.HighQualityAvgScore = fs.HighQualityCount > 0 ? fs.HighQualityScoreSum / fs.HighQualityCount : 0;
            fs.LowQualityAvgScore = fs.LowQualityCount > 0 ? fs.LowQualityScoreSum / fs.LowQualityCount : 0;
            fs.DiscriminativePower = (fs.HighQualityCount >= 3 && fs.LowQualityCount >= 3)
                ? fs.HighQualityAvgScore - fs.LowQualityAvgScore : 0;
        }
        return factorStats;
    }

    // ============ 自进化建议 ============

    public List<OptimizationSuggestion> GetOptimizationSuggestions(int days = EvolutionWindowDays)
    {
        var suggestions = new List<OptimizationSuggestion>();
        foreach (var (type, stat) in GetRecentStats(days))
        {
            if (stat.Total < 5) continue;
            double winRate = (double)stat.Success / stat.Total;
            double failRate = (double)stat.Fail / stat.Total;

            if (winRate <= 0.35 && failRate > 0.5)
            {
                suggestions.Add(new OptimizationSuggestion
                {
                    SignalType = type, SignalLabel = stat.SignalLabel,
                    Action = "increase_threshold",
                    Reason = $"胜率仅 {winRate * 100:F1}%（{stat.Success}/{stat.Total}），建议提高触发阈值减少误报",
                    WinRate = winRate, Total = stat.Total, AvgChangePct = stat.AvgChangePct
                });
            }
            else if (winRate > 0.7 && stat.Total >= 10)
            {
                suggestions.Add(new OptimizationSuggestion
                {
                    SignalType = type, SignalLabel = stat.SignalLabel,
                    Action = "decrease_threshold",
                    Reason = $"胜率高达 {winRate * 100:F1}%（{stat.Success}/{stat.Total}），可考虑降低阈值捕捉更多机会",
                    WinRate = winRate, Total = stat.Total, AvgChangePct = stat.AvgChangePct
                });
            }
        }
        return suggestions;
    }

    // ============ 归因账本 ============

    public AttributionLedger GetAttributionLedger() => _attribution;

    /// <summary>
    /// 读取指定日期的漏报分析摘要（自进化报告"漏报复盘"板块与漏报复活用）
    /// </summary>
    public MissedAnalysisSummary? GetMissedAnalysis(string dateKey)
        => _missedAnalysis.TryGetValue(dateKey, out var summary) ? summary : null;


    /// <summary>
    /// 近 N 日被静音类型在漏报波顶的累计命中统计
    /// </summary>
    public (Dictionary<string, int> Counts, Dictionary<string, string> Labels) GetRecentMutedMissCounts()
    {
        var counts = new Dictionary<string, int>();
        var labels = new Dictionary<string, string>();
        try
        {
            var recent = _missedAnalysis.Keys.OrderBy(k => k).TakeLast(EvolutionWindowDays).ToList();
            foreach (var k in recent)
            {
                if (!_missedAnalysis.TryGetValue(k, out var summary)) continue;
                if (summary.Missed == null) continue;
                foreach (var m in summary.Missed)
                {
                    if (m.Coverage != "muted") continue;
                    if (m.MutedTypes == null) continue;
                    foreach (var t in m.MutedTypes)
                    {
                        if (!counts.ContainsKey(t)) counts[t] = 0;
                        counts[t]++;
                        if (m.MutedLabels != null && m.MutedLabels.TryGetValue(t, out var label))
                            labels[t] = label;
                    }
                }
            }
        }
        catch { /* ignore */ }
        return (counts, labels);
    }

    // ============ 清理过期数据 ============

}
