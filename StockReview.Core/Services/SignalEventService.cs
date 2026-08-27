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
/// 信号事件存储服务 - 对应 Electron 版 signalEventStore.js (1818行)
/// 记录盘中信号事件、收盘后评估信号成功/失败、统计历史胜率供自进化引擎使用
/// 持久化到 appConfig 表
/// </summary>
public class SignalEventService
{
    private readonly DatabaseService _db;
    private const string EventsKey = "pet_signal_events";
    private const string StatsKey = "pet_signal_stats";
    private const string AttributionKey = "pet_evolution_attribution";
    private const string MissedAnalysisKey = "pet_missed_sell_analysis";
    private const int MaxHistoryDays = 30;
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

    // 内存数据
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
        // 兼容 Electron 备份的 camelCase 字段（WPF 自身写入为 PascalCase）
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SignalEventService(DatabaseService db)
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
            }

            var statsRow = _db.GetById("appConfig", StatsKey);
            if (statsRow != null && statsRow.TryGetValue("value", out var stVal) && stVal != null)
            {
                _stats = JsonSerializer.Deserialize<Dictionary<string, SignalTypeStat>>(stVal.ToString()!, JsonOpts) ?? new();
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

    private void SaveEvents()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastSaveEventsMs < SaveThrottleMs) return;
        _lastSaveEventsMs = now;
        try
        {
            var json = JsonSerializer.Serialize(_events, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = EventsKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 保存事件失败");
            var dates = _events.Keys.OrderBy(k => k).ToList();
            while (dates.Count > 7)
            {
                var oldest = dates[0];
                _events.Remove(oldest);
                dates.RemoveAt(0);
                try
                {
                    var json = JsonSerializer.Serialize(_events, JsonOpts);
                    _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = EventsKey, ["value"] = json });
                    Log.Warning("[SignalEvent] 配额不足，裁剪旧数据后保存成功");
                    return;
                }
                catch { /* 继续裁剪 */ }
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

    /// <summary>
    /// 记录信号事件
    /// </summary>
    public SignalEvent RecordEvent(SignalEventInput input)
    {
        var today = TodayKey();
        if (!_events.ContainsKey(today))
            _events[today] = new List<SignalEvent>();

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

        // 去重：同一股票同一信号类型在30秒内只记录一次
        var existing = _events[today].Find(e =>
            e.StockCode == record.StockCode &&
            e.SignalType == record.SignalType &&
            Math.Abs(e.Timestamp - record.Timestamp) < 30000);
        if (existing != null) return existing;

        _events[today].Add(record);
        SaveEvents();
        return record;
    }

    /// <summary>
    /// 获取指定日期的信号事件
    /// </summary>
    public List<SignalEvent> GetEventsByDate(string date)
    {
        return _events.TryGetValue(date, out var list) ? list : new List<SignalEvent>();
    }

    /// <summary>
    /// 获取今日信号事件
    /// </summary>
    public List<SignalEvent> GetTodayEvents(string? stockCode = null)
    {
        var today = TodayKey();
        var events = _events.TryGetValue(today, out var list) ? list : new List<SignalEvent>();
        return stockCode != null ? events.Where(e => e.StockCode == stockCode).ToList() : events;
    }

    /// <summary>
    /// 标记事件已弹出气泡提醒
    /// </summary>
    public void MarkAlerted(SignalEvent record)
    {
        if (record == null) return;
        if (record.Metadata != null && record.Metadata.TryGetValue("alerted", out var alerted) && alerted is true) return;
        record.Metadata ??= new Dictionary<string, object>();
        record.Metadata["alerted"] = true;
        SaveEvents();
    }

    /// <summary>
    /// 获取今日已实际弹出提醒的事件
    /// </summary>
    public List<SignalEvent> GetTodayAlertedEvents(string? stockCode = null)
    {
        return GetTodayEvents(stockCode).Where(e =>
            e.Metadata != null && e.Metadata.TryGetValue("alerted", out var v) && v is true).ToList();
    }

    /// <summary>
    /// 标记某类信号为已优化
    /// </summary>
    public int MarkSignalsOptimized(string signalType, string? date = null)
    {
        var types = new[] { signalType };
        var dateKeys = date != null ? new[] { date } : _events.Keys.OrderBy(k => k).TakeLast(3).ToArray();
        int marked = 0;
        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
            foreach (var e in events)
            {
                if (types.Contains(e.SignalType) && !e.IsOptimized)
                {
                    e.IsOptimized = true;
                    e.OptimizationVersion = (e.OptimizationVersion ?? 0) + 1;
                    e.OptimizedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    marked++;
                }
            }
        }
        if (marked > 0) SaveEvents();
        return marked;
    }

    // ============ 信号评估 ============

    /// <summary>
    /// 评估单个信号事件
    /// </summary>
    public SignalEvaluation? EvaluateEvent(SignalEvent evt, List<Snapshot> snapshots, OptimalExitPoints? optimalPoints = null, List<Wave>? waves = null)
    {
        if (evt.Evaluated) return evt.Evaluation;

        // HOLD 过滤状态仅留痕
        if (evt.SignalType == "hold_filtered")
        {
            evt.Evaluated = true;
            evt.Evaluation = new SignalEvaluation
            {
                Result = "neutral", Reason = "HOLD过滤状态，不评估", MaxChangePct = 0, Reward = 0.5
            };
            return evt.Evaluation;
        }

        // 找到信号触发时间对应的快照索引
        int triggerIdx = -1;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapTs = new DateTimeOffset(snapshots[i].SnapshotAt).ToUnixTimeMilliseconds();
            if (snapTs >= evt.Timestamp) { triggerIdx = i; break; }
        }
        if (triggerIdx < 0 || triggerIdx >= snapshots.Count - 1)
        {
            evt.Evaluated = true;
            evt.Evaluation = new SignalEvaluation { Result = "neutral", Reason = "无后续数据", MaxChangePct = 0, Reward = 0.5, Quality = 0.5, Capture = 0, TimeEfficiency = 0.5, CapturePct = 0 };
            return evt.Evaluation;
        }

        var triggerPrice = (double)evt.Price;
        if (!double.IsFinite(triggerPrice) || triggerPrice <= 0)
        {
            evt.Evaluated = true;
            evt.Evaluation = new SignalEvaluation { Result = "neutral", Reason = "触发价无效", MaxChangePct = 0, Reward = 0.5, Quality = 0.5, Capture = 0, TimeEfficiency = 0.5, CapturePct = 0 };
            return evt.Evaluation;
        }

        var signalType = evt.SignalType ?? "unknown";
        var isBuySignal = BuySignalTypes.Contains(signalType);
        var isSellSignal = SellSignalTypes.Contains(signalType);

        int[] evalWindows;
        double successThreshold;
        bool lookForMax;
        if (isBuySignal || signalType.Contains("rise") || signalType.Contains("up"))
        {
            evalWindows = EvalConfig.BuySignalEvalWindows;
            successThreshold = EvalConfig.BuySuccessThreshold;
            lookForMax = true;
        }
        else if (isSellSignal || signalType.Contains("fall") || signalType.Contains("down"))
        {
            evalWindows = EvalConfig.SellSignalEvalWindows;
            successThreshold = EvalConfig.SellSuccessThreshold;
            lookForMax = false;
        }
        else
        {
            evalWindows = EvalConfig.BuySignalEvalWindows;
            successThreshold = EvalConfig.BuySuccessThreshold;
            lookForMax = true;
        }

        // 卖点门槛收紧
        if (isSellSignal) successThreshold = -WaveMinSuccessDepthPct;

        double bestChangePct = 0;
        int bestWindow = 0;
        var intervalMs = EstimateSnapshotIntervalMs(snapshots);
        const double msPerMin = 60 * 1000;

        foreach (var winMin in evalWindows)
        {
            int winBars = intervalMs > 0
                ? (int)Math.Ceiling(winMin * msPerMin / intervalMs)
                : winMin * 6;
            int endIdx = Math.Min(triggerIdx + winBars, snapshots.Count - 1);
            if (endIdx <= triggerIdx) continue;

            var futurePrices = snapshots.Skip(triggerIdx + 1).Take(endIdx - triggerIdx).Select(s => (double)s.Price).ToList();
            if (futurePrices.Count == 0) continue;

            double extremePrice = lookForMax ? futurePrices.Max() : futurePrices.Min();
            double changePct = ((extremePrice - triggerPrice) / triggerPrice) * 100;
            if (Math.Abs(changePct) > Math.Abs(bestChangePct))
            {
                bestChangePct = changePct;
                bestWindow = winMin;
            }
        }

        // 判断成功/失败
        string result = "neutral";
        if (lookForMax && bestChangePct >= successThreshold) result = "success";
        else if (!lookForMax && bestChangePct <= successThreshold) result = "success";
        else if (lookForMax && bestChangePct <= -successThreshold) result = "fail";
        else if (!lookForMax && bestChangePct >= -successThreshold) result = "fail";

        // 精细化奖励
        int maxWindowBars = intervalMs > 0
            ? (int)Math.Ceiling(evalWindows.Max() * msPerMin / intervalMs)
            : evalWindows.Max() * 6;
        int fullEndIdx = Math.Min(triggerIdx + maxWindowBars, snapshots.Count - 1);
        var fullFuturePrices = snapshots.Skip(triggerIdx + 1).Take(fullEndIdx - triggerIdx).Select(s => (double)s.Price).ToList();
        var rewardInfo = ComputeReward(triggerPrice, fullFuturePrices, !lookForMax);

        // 全日维度最优卖点分析
        bool nearDayHigh = false, beforeMaxDrawdown = false, nearDayLow = false;
        double enhancedQuality = rewardInfo.Quality;
        const long optimalToleranceMs = 5 * 60 * 1000;

        if (isSellSignal && optimalPoints != null)
        {
            var triggerTime = new DateTimeOffset(snapshots[triggerIdx].SnapshotAt).ToUnixTimeMilliseconds();

            if (Math.Abs(triggerTime - optimalPoints.DayHighTime) <= optimalToleranceMs)
            {
                nearDayHigh = true;
                enhancedQuality = Math.Min(1, enhancedQuality + 0.25);
            }
            if (optimalPoints.MaxDrawdownPct > 1.0 &&
                Math.Abs(triggerTime - optimalPoints.MaxDrawdownPeakTime) <= optimalToleranceMs)
            {
                beforeMaxDrawdown = true;
                enhancedQuality = Math.Min(1, enhancedQuality + 0.20);
            }
            if (!nearDayHigh && !beforeMaxDrawdown &&
                optimalPoints.DayLowTime.HasValue &&
                Math.Abs(triggerTime - optimalPoints.DayLowTime.Value) <= optimalToleranceMs)
            {
                nearDayLow = true;
                enhancedQuality = Math.Max(0, enhancedQuality - 0.25);
            }
        }

        // 波次归因
        int? waveIdx = null;
        double? waveCapture = null;
        bool nearWaveTop = false;
        double? waveDepthPct = null;
        double? rankScore = null;
        bool waveHigh = false, waveLow = false;

        if (isSellSignal && waves != null && waves.Count > 0)
        {
            var triggerTime = new DateTimeOffset(snapshots[triggerIdx].SnapshotAt).ToUnixTimeMilliseconds();
            Wave? wave = waves.Find(w =>
                triggerTime >= w.TroughTime - WaveTopToleranceMs &&
                triggerTime <= w.BottomTime + WaveTopToleranceMs);
            if (wave == null)
            {
                wave = waves.Where(w => w.PeakTime <= triggerTime + WaveTopToleranceMs)
                            .OrderByDescending(w => w.PeakTime)
                            .FirstOrDefault();
            }

            if (wave != null)
            {
                waveIdx = wave.WaveIdx;
                waveDepthPct = wave.DepthPct;
                double range = wave.PeakPrice - wave.BottomPrice;
                waveCapture = range > 0.0001
                    ? Math.Max(0, Math.Min(1, (triggerPrice - wave.BottomPrice) / range))
                    : 0.5;
                nearWaveTop = Math.Abs(triggerTime - wave.PeakTime) <= WaveTopToleranceMs;
                waveHigh = nearWaveTop || waveCapture >= WaveCaptureHigh;
                waveLow = !nearWaveTop && waveCapture < WaveCaptureLow;

                double dtMin = Math.Abs(triggerTime - wave.PeakTime) / (60.0 * 1000);
                double timeNearPeak = 1 - Math.Min(1, dtMin / 30);
                double strength = 0.5;
                if (evt.Metadata != null)
                {
                    if (evt.Metadata.TryGetValue("signalStrength", out var ss) && ss is double ssD)
                        strength = ssD;
                    else if (evt.Metadata.TryGetValue("totalScore", out var ts) && ts is double tsD)
                        strength = Math.Min(1, tsD / 100);
                }
                double factorConfirm = 0.5;
                if (evt.Metadata != null && evt.Metadata.TryGetValue("factorDirections", out var fd) && fd is Dictionary<string, object> fdDict)
                {
                    var keys = fdDict.Keys.ToList();
                    if (keys.Count > 0)
                    {
                        int bearCount = keys.Count(k => fdDict.TryGetValue(k, out var v) && v?.ToString() == "bear");
                        factorConfirm = (double)bearCount / keys.Count;
                    }
                }
                rankScore = 0.45 * waveCapture.Value + 0.25 * timeNearPeak + 0.15 * strength + 0.15 * factorConfirm;
            }
        }

        // 质量感知结果修正（仅卖点信号）
        if (isSellSignal)
        {
            bool depthOk = waveDepthPct.HasValue
                ? waveDepthPct.Value > WaveMinSuccessDepthPct
                : beforeMaxDrawdown || bestChangePct <= -WaveMinSuccessDepthPct;
            if (nearDayHigh || beforeMaxDrawdown) result = depthOk ? "success" : "neutral";
            else if (nearDayLow || waveLow) result = "fail";
            else if (waveHigh) result = depthOk ? "success" : "neutral";
            else if (waveCapture.HasValue && waveCapture < 0.3) result = "neutral";
        }

        double enhancedReward = (nearDayHigh || beforeMaxDrawdown || nearDayLow)
            ? 0.4 * enhancedQuality + 0.4 * rewardInfo.Capture + 0.2 * rewardInfo.TimeEfficiency
            : rewardInfo.Reward;

        evt.Evaluated = true;
        evt.Evaluation = new SignalEvaluation
        {
            Result = result,
            MaxChangePct = bestChangePct,
            EvalWindowMin = bestWindow,
            TriggerPrice = triggerPrice,
            Reward = enhancedReward,
            Quality = enhancedQuality,
            Capture = rewardInfo.Capture,
            TimeEfficiency = rewardInfo.TimeEfficiency,
            CapturePct = rewardInfo.CapturePct,
            NearDayHigh = nearDayHigh,
            BeforeMaxDrawdown = beforeMaxDrawdown,
            NearDayLow = nearDayLow,
            WaveIdx = waveIdx,
            WaveCapture = waveCapture,
            NearWaveTop = nearWaveTop,
            WaveDepthPct = waveDepthPct,
            WaveHigh = waveHigh,
            WaveLow = waveLow,
            RankScore = rankScore,
            Detail = lookForMax
                ? $"触发后{bestWindow}分钟内最高涨幅 {bestChangePct:F2}%"
                : $"触发后{bestWindow}分钟内最大跌幅 {bestChangePct:F2}%"
        };
        return evt.Evaluation;
    }

    /// <summary>
    /// 从快照数组推导间隔毫秒
    /// </summary>
    private static double EstimateSnapshotIntervalMs(List<Snapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 2) return -1;
        var diffs = new List<long>();
        for (int i = 1; i < snapshots.Count; i++)
        {
            var t1 = new DateTimeOffset(snapshots[i].SnapshotAt).ToUnixTimeMilliseconds();
            var t0 = new DateTimeOffset(snapshots[i - 1].SnapshotAt).ToUnixTimeMilliseconds();
            if (t1 > t0) diffs.Add(t1 - t0);
        }
        if (diffs.Count == 0) return -1;
        diffs.Sort();
        return diffs[diffs.Count / 2];
    }

    /// <summary>
    /// 精细化奖励函数
    /// </summary>
    private static RewardInfo ComputeReward(double triggerPrice, List<double> futurePrices, bool isSell)
    {
        if (!double.IsFinite(triggerPrice) || triggerPrice <= 0)
            return new RewardInfo { Reward = 0.5, Quality = 0.5, Capture = 0, TimeEfficiency = 0.5, CapturePct = 0 };
        if (futurePrices == null || futurePrices.Count < 2)
            return new RewardInfo { Reward = 0.5, Quality = 0.5, Capture = 0, TimeEfficiency = 0.5, CapturePct = 0 };

        double maxPrice = futurePrices.Max();
        double minPrice = futurePrices.Min();
        double range = maxPrice - minPrice;

        if (isSell)
        {
            double quality = range > 0 ? Math.Max(0, Math.Min(1, (triggerPrice - minPrice) / range)) : 0.5;
            double capturePct = ((triggerPrice - minPrice) / triggerPrice) * 100;
            double capture = Math.Max(0, Math.Min(1, capturePct / 2));
            int extremeIdx = futurePrices.IndexOf(maxPrice);
            double timeEfficiency = 1 - ((double)extremeIdx / (futurePrices.Count - 1));
            double reward = 0.4 * quality + 0.4 * capture + 0.2 * timeEfficiency;
            return new RewardInfo { Reward = reward, Quality = quality, Capture = capture, TimeEfficiency = timeEfficiency, CapturePct = capturePct };
        }
        else
        {
            double quality = range > 0 ? Math.Max(0, Math.Min(1, (maxPrice - triggerPrice) / range)) : 0.5;
            double capturePct = ((maxPrice - triggerPrice) / triggerPrice) * 100;
            double capture = Math.Max(0, Math.Min(1, capturePct / 2));
            int extremeIdx = futurePrices.IndexOf(minPrice);
            double timeEfficiency = 1 - ((double)extremeIdx / (futurePrices.Count - 1));
            double reward = 0.4 * quality + 0.4 * capture + 0.2 * timeEfficiency;
            return new RewardInfo { Reward = reward, Quality = quality, Capture = capture, TimeEfficiency = timeEfficiency, CapturePct = capturePct };
        }
    }

    // ============ 全日最优卖点 ============

    /// <summary>
    /// 计算当日最优卖点位置
    /// </summary>
    public static OptimalExitPoints? ComputeOptimalExitPoints(List<Snapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 2) return null;
        var prices = snapshots.Select(s => (double)s.Price).ToList();
        long GetTs(int idx) => new DateTimeOffset(snapshots[idx].SnapshotAt).ToUnixTimeMilliseconds();

        // 当日最高点
        int dayHighIdx = 0;
        double dayHigh = prices[0];
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i] > dayHigh) { dayHigh = prices[i]; dayHighIdx = i; }
        }

        // 最大回调峰值（从右向左扫描）
        int maxDrawdownPeakIdx = 0, maxDrawdownEndIdx = 0;
        double maxDrawdown = 0;
        double futureMin = prices[^1];
        int futureMinIdx = prices.Count - 1;
        for (int i = prices.Count - 1; i >= 0; i--)
        {
            if (prices[i] < futureMin) { futureMin = prices[i]; futureMinIdx = i; }
            double drawdown = prices[i] - futureMin;
            if (drawdown > maxDrawdown) { maxDrawdown = drawdown; maxDrawdownPeakIdx = i; maxDrawdownEndIdx = futureMinIdx; }
        }

        // 当日最低点
        int dayLowIdx = 0;
        double dayLow = prices[0];
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i] < dayLow) { dayLow = prices[i]; dayLowIdx = i; }
        }

        return new OptimalExitPoints
        {
            DayHighIdx = dayHighIdx,
            DayHighTime = GetTs(dayHighIdx),
            DayHighPrice = dayHigh,
            MaxDrawdownPeakIdx = maxDrawdownPeakIdx,
            MaxDrawdownPeakTime = GetTs(maxDrawdownPeakIdx),
            MaxDrawdownPeakPrice = prices[maxDrawdownPeakIdx],
            MaxDrawdownEndIdx = maxDrawdownEndIdx,
            MaxDrawdownEndPrice = prices[maxDrawdownEndIdx],
            MaxDrawdownPct = prices[maxDrawdownPeakIdx] > 0 ? (maxDrawdown / prices[maxDrawdownPeakIdx]) * 100 : 0,
            DayLowIdx = dayLowIdx,
            DayLowTime = GetTs(dayLowIdx),
            DayLowPrice = dayLow
        };
    }

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
    public void EvaluateDay(string date, Dictionary<string, List<Snapshot>> snapshotsMap)
    {
        if (!_events.TryGetValue(date, out var events) || events.Count == 0) return;

        var optimalCache = new Dictionary<string, OptimalExitPoints?>();
        var waveCache = new Dictionary<string, List<Wave>>();

        foreach (var evt in events)
        {
            if (evt.Evaluated) continue;
            if (!snapshotsMap.TryGetValue(evt.StockCode, out var snaps) || snaps.Count == 0) continue;
            if (!optimalCache.ContainsKey(evt.StockCode))
            {
                optimalCache[evt.StockCode] = ComputeOptimalExitPoints(snaps);
                waveCache[evt.StockCode] = SegmentWaves(snaps);
            }
            EvaluateEvent(evt, snaps, optimalCache[evt.StockCode], waveCache[evt.StockCode]);
        }

        UpdateStats(date);
        SaveEvents();
    }

    /// <summary>
    /// 更新信号类型统计
    /// </summary>
    private void UpdateStats(string date)
    {
        if (!_events.TryGetValue(date, out var events)) return;

        foreach (var evt in events)
        {
            if (!evt.Evaluated || evt.Evaluation == null) continue;
            if (evt.SignalType == "hold_filtered") continue;

            var type = evt.SignalType;
            if (!_stats.ContainsKey(type))
            {
                _stats[type] = new SignalTypeStat
                {
                    SignalType = type,
                    SignalLabel = evt.SignalLabel,
                    Total = 0, Success = 0, Fail = 0, Neutral = 0,
                    AvgChangePct = 0, History = new List<StatHistoryRecord>(),
                    NearDayHighCount = 0, BeforeMaxDrawdownCount = 0, NearDayLowCount = 0
                };
            }
            var stat = _stats[type];
            stat.Total++;
            if (evt.Evaluation.Result == "success") stat.Success++;
            else if (evt.Evaluation.Result == "fail") stat.Fail++;
            else stat.Neutral++;

            double prevAvg = stat.AvgChangePct * (stat.Total - 1);
            stat.AvgChangePct = (prevAvg + evt.Evaluation.MaxChangePct) / stat.Total;

            double reward = evt.Evaluation.Reward ?? 0.5;
            double prevRewardAvg = (stat.AvgReward ?? 0.5) * (stat.Total - 1);
            stat.AvgReward = (prevRewardAvg + reward) / stat.Total;

            if (evt.Evaluation.NearDayHigh) stat.NearDayHighCount++;
            if (evt.Evaluation.BeforeMaxDrawdown) stat.BeforeMaxDrawdownCount++;
            if (evt.Evaluation.NearDayLow) stat.NearDayLowCount++;

            stat.History ??= new List<StatHistoryRecord>();
            stat.History.Add(new StatHistoryRecord
            {
                Date = date,
                Result = evt.Evaluation.Result,
                ChangePct = evt.Evaluation.MaxChangePct,
                Reward = evt.Evaluation.Reward ?? 0.5,
                NearDayHigh = evt.Evaluation.NearDayHigh,
                BeforeMaxDrawdown = evt.Evaluation.BeforeMaxDrawdown,
                NearDayLow = evt.Evaluation.NearDayLow,
                StockCode = evt.StockCode
            });
            if (stat.History.Count > 30) stat.History = stat.History.TakeLast(30).ToList();
        }
        SaveStats();
    }

    // ============ 近期窗口统计 ============

    /// <summary>
    /// 近 N 个交易日的已评估事件现算统计
    /// </summary>
    public Dictionary<string, RecentSignalStat> GetRecentStats(int days = EvolutionWindowDays)
    {
        var dateKeys = _events.Keys.OrderBy(k => k).TakeLast(days).ToList();
        var stats = new Dictionary<string, RecentSignalStat>();

        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
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
    public static string ClassifyQuality(SignalEvaluation? ev)
    {
        if (ev == null) return "mid";
        bool hasWave = ev.WaveIdx.HasValue;
        if (hasWave)
        {
            if (ev.WaveHigh) return "high";
            if (ev.WaveLow || ev.NearDayLow) return "low";
            double reward = ev.Reward ?? 0.5;
            if (reward > 0.65 || ev.NearDayHigh || ev.BeforeMaxDrawdown) return "high";
            if (reward < 0.4) return "low";
            return "mid";
        }
        double r = ev.Reward ?? 0.5;
        bool isHigh = (r > 0.65 || ev.NearDayHigh || ev.BeforeMaxDrawdown) && !ev.NearDayLow;
        bool isLow = r < 0.4 || ev.NearDayLow;
        return isHigh ? "high" : (isLow ? "low" : "mid");
    }

    // ============ 按股票维度质量统计 ============

    public Dictionary<string, StockQualityStat> GetQualityStatsByStock(int days = EvolutionWindowDays)
    {
        var dateKeys = _events.Keys.OrderBy(k => k).TakeLast(days).ToList();
        var today = TodayKey();
        var byStock = new Dictionary<string, StockQualityStat>();

        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
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
    public ReplayResult ReplayWithParams(Dictionary<string, double>? newMultipliers, Dictionary<string, double>? newFactorWeights, int days = EvolutionWindowDays)
    {
        newMultipliers ??= new();
        newFactorWeights ??= new();
        var dateKeys = _events.Keys.OrderBy(k => k).TakeLast(days).ToList();
        var perEvent = new List<ReplayEventInfo>();
        var blame = new Dictionary<string, double>();
        var credit = new Dictionary<string, double>();

        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
            foreach (var evt in events)
            {
                if (!evt.Evaluated || evt.Evaluation == null) continue;
                if (evt.SignalType == "hold_filtered") continue;
                if (!SellSignalTypes.Contains(evt.SignalType)) continue;

                var quality = ClassifyQuality(evt.Evaluation);
                if (quality == "mid") continue;

                var md = evt.Metadata ?? new Dictionary<string, object>();
                var ev = new ReplayEventInfo
                {
                    Event = evt, Quality = quality, Stage1Pass = false, Stage2Pass = false,
                    Strength = 0.5, DateKey = dk
                };

                // 强度初始化
                if (md.TryGetValue("signalStrength", out var ss) && ss is double ssD)
                    ev.Strength = ssD;
                else if (md.TryGetValue("totalScore", out var ts) && ts is double tsD)
                    ev.Strength = Math.Min(1, tsD / 100);

                Dictionary<string, double>? contributions = null;

                // 共振事件 vs 单信号事件
                if (md.TryGetValue("composition", out var comp) && comp is List<object> compList && compList.Count > 0 &&
                    md.TryGetValue("scoreMods", out var sm) && sm is double scoreMods)
                {
                    // 共振事件重放
                    double newBase = 0;
                    contributions = new Dictionary<string, double>();
                    double contribSum = 0;
                    foreach (var item in compList)
                    {
                        if (item is not Dictionary<string, object> c) continue;
                        var cType = c.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";
                        double mult = newMultipliers.TryGetValue(cType, out var mv) ? mv : (c.TryGetValue("multiplier", out var m) && m is double mD ? mD : 1.0);
                        double cBase = c.TryGetValue("base", out var b) && b is double bD ? bD : 10;
                        bool halved = c.TryGetValue("halved", out var h) && h is true;
                        double contrib = cBase * mult * (halved ? 0.5 : 1);
                        newBase += contrib;
                        if (!contributions.ContainsKey(cType)) contributions[cType] = 0;
                        contributions[cType] += Math.Abs(contrib);
                        contribSum += Math.Abs(contrib);
                    }
                    double scoreBonus = md.TryGetValue("scoreBonus", out var sb) && sb is double sbD ? sbD : 0;
                    double newSignal = Math.Min(100, JsMath.JsRound(newBase * scoreMods + scoreBonus));

                    // 多因子评分重放
                    double? mfNew = null;
                    if (md.TryGetValue("factorScores", out var fs) && fs is Dictionary<string, object> fsDict)
                    {
                        var fd = md.TryGetValue("factorDirections", out var fd2) && fd2 is Dictionary<string, object> fdDict ? fdDict : new Dictionary<string, object>();
                        double num = 0, den = 0;
                        int bearCount = 0;
                        var factorContrib = new Dictionary<string, double>();
                        double factorContribSum = 0;
                        foreach (var (k, s) in fsDict)
                        {
                            double sv = s is double sd ? sd : 0;
                            var dir = fd.TryGetValue(k, out var dv) ? dv?.ToString() ?? "bear" : "bear";
                            double w = newFactorWeights.TryGetValue(k, out var wv) ? wv : 0;
                            if (dir == "bear")
                            {
                                num += sv * w; den += w; bearCount++;
                                var fk = $"factor:{k}";
                                if (!factorContrib.ContainsKey(fk)) factorContrib[fk] = 0;
                                factorContrib[fk] += Math.Abs(sv * w);
                                factorContribSum += Math.Abs(sv * w);
                            }
                            else if (dir == "bull") num -= sv * w * 0.5;
                        }
                        if (den > 0)
                        {
                            double resBonus = bearCount >= 5 ? 35 : bearCount >= 4 ? 25 : bearCount >= 3 ? 15 : 0;
                            mfNew = Math.Min(100, num / den + resBonus);
                        }
                        if (factorContribSum > 0 && mfNew.HasValue)
                        {
                            foreach (var (fk, fv) in factorContrib)
                            {
                                if (!contributions.ContainsKey(fk)) contributions[fk] = 0;
                                contributions[fk] += 0.4 * (fv / factorContribSum);
                            }
                        }
                    }

                    bool holdFilter = md.TryGetValue("holdFilter", out var hf) && hf is true;
                    double fused = mfNew.HasValue ? Math.Max(newSignal, newSignal * 0.6 + mfNew.Value * 0.4) : newSignal;
                    ev.Stage1Pass = fused >= 20 && (!holdFilter || fused >= 35);
                    ev.Strength = Math.Max(0, Math.Min(1, fused / 100));

                    if (contribSum > 0)
                    {
                        var keys = contributions.Keys.ToList();
                        foreach (var k in keys) contributions[k] /= contribSum;
                    }
                }
                else
                {
                    // 单信号事件
                    double mult = newMultipliers.TryGetValue(evt.SignalType, out var mv) ? mv : 1.0;
                    ev.Stage1Pass = mult > SignalMuteThreshold;
                    if (md.TryGetValue("baseWeight", out var bw) && bw is double bwD && bwD > 0)
                        ev.Strength = Math.Max(0, Math.Min(1, (bwD * mult) / 100));
                    contributions = new Dictionary<string, double> { [evt.SignalType] = 1 };
                }

                ev.Contributions = contributions;
                perEvent.Add(ev);
            }
        }

        // 第二段：波内限发
        var groups = new Dictionary<string, List<ReplayEventInfo>>();
        foreach (var ev in perEvent)
        {
            var waveIdx = ev.Event.Evaluation?.WaveIdx;
            string gKey = waveIdx.HasValue
                ? $"{ev.DateKey}|{ev.Event.StockCode}|{waveIdx.Value}"
                : $"__solo_{ev.Event.Id}";
            if (!groups.ContainsKey(gKey)) groups[gKey] = new List<ReplayEventInfo>();
            groups[gKey].Add(ev);
        }
        foreach (var list in groups.Values)
        {
            list.Sort((a, b) => a.Event.Timestamp.CompareTo(b.Event.Timestamp));
            int emitted = 0;
            double lastStrength = double.NegativeInfinity;
            foreach (var ev in list)
            {
                if (!ev.Stage1Pass) continue;
                if (emitted >= MaxAlertsPerWave) continue;
                if (emitted >= 1 && ev.Strength <= lastStrength) continue;
                ev.Stage2Pass = true;
                emitted++;
                lastStrength = ev.Strength;
            }
        }

        // 指标汇总
        int lowTotal = 0, lowFiltered = 0, highTotal = 0, highKept = 0;
        int stage1LowFiltered = 0, stage1HighKept = 0;
        var waveMap = new Dictionary<string, WaveReplayInfo>();

        foreach (var ev in perEvent)
        {
            bool alive = ev.Stage1Pass && ev.Stage2Pass;
            if (ev.Quality == "low")
            {
                lowTotal++;
                if (!alive)
                {
                    lowFiltered++;
                    if (ev.Stage1Pass) stage1LowFiltered++;
                }
                else if (ev.Contributions != null)
                {
                    foreach (var (k, share) in ev.Contributions)
                    {
                        if (!blame.ContainsKey(k)) blame[k] = 0;
                        blame[k] += share;
                    }
                }
            }
            else
            {
                highTotal++;
                if (alive)
                {
                    highKept++;
                    if (ev.Stage1Pass && ev.Stage2Pass) stage1HighKept++;
                }
                else if (ev.Contributions != null)
                {
                    foreach (var (k, share) in ev.Contributions)
                    {
                        if (!credit.ContainsKey(k)) credit[k] = 0;
                        credit[k] += share;
                    }
                }
                var waveIdx = ev.Event.Evaluation?.WaveIdx;
                if (waveIdx.HasValue)
                {
                    var wk = $"{ev.DateKey}|{ev.Event.StockCode}|{waveIdx.Value}";
                    if (!waveMap.ContainsKey(wk))
                    {
                        waveMap[wk] = new WaveReplayInfo
                        {
                            WaveKey = wk, DateKey = ev.DateKey, StockCode = ev.Event.StockCode,
                            StockName = ev.Event.StockName, WaveIdx = waveIdx.Value,
                            DepthPct = ev.Event.Evaluation?.WaveDepthPct,
                            HighTotal = 0, HighKept = 0, Top1Rank = -1, Top1Alive = false
                        };
                    }
                    var w = waveMap[wk];
                    w.HighTotal++;
                    if (alive) w.HighKept++;
                    double rank = ev.Event.Evaluation?.RankScore ?? 0;
                    if (rank > w.Top1Rank) { w.Top1Rank = rank; w.Top1Alive = alive; }
                }
            }
        }

        var waves = waveMap.Values.OrderByDescending(w => w.HighTotal).ToList();
        var waveViolations = waves
            .Where(w => (w.HighTotal >= 1 && !w.Top1Alive) || (w.HighTotal >= 2 && w.HighKept == 0))
            .Select(w => w.WaveKey).ToList();

        return new ReplayResult
        {
            Replayable = perEvent.Count,
            LowTotal = lowTotal, LowFiltered = lowFiltered,
            LowFilterRate = lowTotal > 0 ? (double?)lowFiltered / lowTotal : null,
            HighTotal = highTotal, HighKept = highKept,
            HighKeepRate = highTotal > 0 ? (double?)highKept / highTotal : null,
            Stage1LowFiltered = stage1LowFiltered, Stage1HighKept = stage1HighKept,
            Waves = waves, WaveViolations = waveViolations,
            Blame = blame, Credit = credit
        };
    }

    // ============ 因子级奖励统计 ============

    public Dictionary<string, FactorRewardStat> GetFactorRewardStats(int days = EvolutionWindowDays)
    {
        var dateKeys = _events.Keys.OrderBy(k => k).TakeLast(days).ToList();
        var factorStats = new Dictionary<string, FactorRewardStat>();

        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
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

    public void UpdateAttribution(List<AttributionRoundEntry> roundEntries)
    {
        if (roundEntries == null || roundEntries.Count == 0) return;
        var entries = _attribution.Entries ??= new();
        var touched = new List<string>();

        foreach (var re in roundEntries)
        {
            if (string.IsNullOrEmpty(re.ParamKey)) continue;
            var key = re.ParamKey;
            if (!entries.ContainsKey(key))
            {
                entries[key] = new AttributionEntry
                {
                    Kind = re.Kind ?? "signal", Label = re.Label ?? key,
                    Role = "normal", Frozen = false, FreezeReason = "",
                    DirectionStreak = 0, TotalLowFiltered = 0, TotalHighKilled = 0,
                    FailedSteps = 0, History = new List<AttributionHistoryRecord>()
                };
            }
            var entry = entries[key];
            entry.Kind = re.Kind ?? entry.Kind;
            entry.Label = re.Label ?? entry.Label;

            int dir = Math.Sign(re.Delta);
            if (dir != 0)
            {
                entry.DirectionStreak = Math.Sign(entry.DirectionStreak) == dir ? entry.DirectionStreak + dir : dir;
            }

            if (re.Failed)
            {
                entry.FailedSteps = (entry.FailedSteps ?? 0) + 1;
                if (entry.FailedSteps >= 2 && !entry.Frozen)
                {
                    entry.Frozen = true;
                    entry.FreezeReason = $"连续{entry.FailedSteps}次该方向调整被回滚";
                    entry.FrozenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            else
            {
                entry.FailedSteps = 0;
                entry.TotalLowFiltered += Math.Max(0, re.LowFiltered);
                entry.TotalHighKilled += Math.Max(0, re.HighKilled);
                double net = re.LowFiltered - 2 * re.HighKilled;
                entry.History ??= new();
                entry.History.Add(new AttributionHistoryRecord
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Delta = re.Delta, Net = net,
                    LowFiltered = re.LowFiltered, HighKilled = re.HighKilled
                });
                if (entry.History.Count > 10) entry.History = entry.History.TakeLast(10).ToList();
            }
            touched.Add(key);
        }

        // 等效参数归并
        if (touched.Count > 1)
        {
            var active = touched.Select(k => (k, net: EntryLastNet(entries[k])))
                .Where(x => x.net > 0)
                .OrderByDescending(x => x.net)
                .ToList();
            if (active.Count > 1)
            {
                double mainNet = active[0].net;
                entries[active[0].k].Role = "main";
                foreach (var item in active.Skip(1))
                {
                    if (item.net >= mainNet * 0.8)
                    {
                        entries[item.k].Role = "secondary";
                        entries[item.k].Frozen = true;
                        entries[item.k].FreezeReason = $"等效次选（主参数 {entries[active[0].k].Label} 已覆盖该方向）";
                        entries[item.k].FrozenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                }
            }
        }
        SaveAttribution();
    }

    private static double EntryLastNet(AttributionEntry entry)
    {
        if (entry.History == null || entry.History.Count == 0) return 0;
        return entry.History[^1].Net;
    }

    /// <summary>
    /// 归因冻结日衰减
    /// </summary>
    public void DecayAttributionFreezes()
    {
        try
        {
            var today = TodayKey();
            if (_attribution.DayKey == today) return;
            if (_attribution.Entries == null) return;

            bool changed = false;
            long weekAgo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7 * 24 * 60 * 60 * 1000L;

            foreach (var entry in _attribution.Entries.Values)
            {
                if ((entry.FailedSteps ?? 0) > 0) { entry.FailedSteps = 0; changed = true; }
                if (entry.Frozen)
                {
                    bool isFailFreeze = entry.FreezeReason?.StartsWith("连续") == true;
                    bool expired = (entry.FrozenAt ?? 0) < weekAgo;
                    if (isFailFreeze || expired)
                    {
                        entry.Frozen = false;
                        entry.FreezeReason = "";
                        entry.FrozenAt = 0;
                        if (entry.Role == "secondary") entry.Role = "normal";
                        changed = true;
                    }
                }
            }
            _attribution.DayKey = today;
            if (changed) SaveAttribution();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 衰减归因冻结失败");
        }
    }

    /// <summary>
    /// 解冻单个参数
    /// </summary>
    public void UnfreezeParam(string paramKey, string note = "")
    {
        if (_attribution.Entries == null || !_attribution.Entries.TryGetValue(paramKey, out var entry)) return;
        entry.Frozen = false;
        entry.FreezeReason = "";
        entry.FrozenAt = 0;
        entry.FailedSteps = 0;
        if (entry.Role == "secondary") entry.Role = "normal";
        if (!string.IsNullOrEmpty(note))
        {
            entry.History ??= new();
            entry.History.Add(new AttributionHistoryRecord
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Delta = 0, Net = 0, LowFiltered = 0, HighKilled = 0, Note = note
            });
            if (entry.History.Count > 10) entry.History = entry.History.TakeLast(10).ToList();
        }
        SaveAttribution();
    }

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

    public void Cleanup()
    {
        var tz = CnTimeZone.Get;
        var cutoffDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddDays(-MaxHistoryDays), tz);
        var cutoffStr = $"{cutoffDate.Year:0000}-{cutoffDate.Month:00}-{cutoffDate.Day:00}";

        bool changed = false;
        var toRemove = _events.Keys.Where(k => string.Compare(k, cutoffStr, StringComparison.Ordinal) < 0).ToList();
        foreach (var k in toRemove) { _events.Remove(k); changed = true; }
        if (changed) SaveEvents();
    }

    // ============ 漏报复盘 ============

    /// <summary>
    /// 漏报复盘：检测"该出现卖点而未出现"的显著回落波
    /// </summary>
    public MissedAnalysisSummary AnalyzeMissedSellPoints(string dateKey, Dictionary<string, List<Snapshot>> snapshotsMap)
    {
        var dayEvents = GetEventsByDate(dateKey);
        var nameByCode = new Dictionary<string, string>();
        foreach (var e in dayEvents)
        {
            if (!string.IsNullOrEmpty(e.StockName) && !nameByCode.ContainsKey(e.StockCode))
                nameByCode[e.StockCode] = e.StockName;
        }

        var waveList = new List<MissedWaveInfo>();
        if (snapshotsMap != null)
        {
            foreach (var (code, snaps) in snapshotsMap)
            {
                if (snaps == null || snaps.Count < 5) continue;
                foreach (var w in SegmentWaves(snaps))
                {
                    if (w.RisePct < MissedWaveMinRisePct) continue;
                    if (w.DepthPct < MissedWaveMinDepthPct) continue;

                    int activeCount = 0;
                    var mutedTypes = new HashSet<string>();
                    var mutedLabels = new Dictionary<string, string>();
                    foreach (var e in dayEvents)
                    {
                        if (e.StockCode != code) continue;
                        if (!SellSignalTypes.Contains(e.SignalType)) continue;
                        if (Math.Abs(e.Timestamp - w.PeakTime) > WaveTopToleranceMs) continue;
                        if (e.Metadata != null && e.Metadata.TryGetValue("mutedByEvolution", out var muted) && muted is true)
                        {
                            mutedTypes.Add(e.SignalType);
                            var label = e.SignalLabel ?? "";
                            mutedLabels[e.SignalType] = label.Replace("(已静音)", "");
                        }
                        else activeCount++;
                    }

                    waveList.Add(new MissedWaveInfo
                    {
                        StockCode = code,
                        StockName = nameByCode.GetValueOrDefault(code, code),
                        WaveIdx = w.WaveIdx,
                        PeakTime = w.PeakTime,
                        RisePct = JsMath.JsRound(w.RisePct, 2),
                        DepthPct = JsMath.JsRound(w.DepthPct, 2),
                        Coverage = activeCount > 0 ? "active" : (mutedTypes.Count > 0 ? "muted" : "zero"),
                        MutedTypes = mutedTypes.ToList(),
                        MutedLabels = mutedLabels,
                        Features = WaveFeatures(snaps, w)
                    });
                }
            }
        }

        var missed = waveList.Where(w => w.Coverage != "active").ToList();
        var covered = waveList.Where(w => w.Coverage == "active").ToList();
        var featureCompare = CompareWaveFeatures(covered, missed);

        // 静音过度提示
        var mutedHitTypes = new Dictionary<string, int>();
        var mutedHitLabels = new Dictionary<string, string>();
        foreach (var m in missed)
        {
            if (m.Coverage != "muted") continue;
            if (m.MutedTypes == null) continue;
            foreach (var t in m.MutedTypes)
            {
                if (!mutedHitTypes.ContainsKey(t)) mutedHitTypes[t] = 0;
                mutedHitTypes[t]++;
                if (m.MutedLabels != null && m.MutedLabels.TryGetValue(t, out var label))
                    mutedHitLabels[t] = label;
            }
        }
        int mutedHitTotal = mutedHitTypes.Values.Sum();
        string? mutedHint = mutedHitTotal > 0
            ? $"静音类型「{string.Join("、", mutedHitLabels.Values)}」在 {mutedHitTotal} 个漏报波顶本可覆盖（检测到但被自进化压制），系统将依据近5日累计自动复活"
            : null;

        var summary = new MissedAnalysisSummary
        {
            DateKey = dateKey,
            SignificantWaves = waveList.Count,
            MissedCount = missed.Count,
            Missed = missed,
            FeatureCompare = featureCompare,
            MutedHint = mutedHint,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _missedAnalysis[dateKey] = summary;
        SaveMissedAnalysis();

        var recent = _missedAnalysis.Keys.OrderBy(k => k).TakeLast(EvolutionWindowDays).ToList();
        summary.RecentSignificant = recent.Sum(k => _missedAnalysis[k]?.SignificantWaves ?? 0);
        summary.RecentMissed = recent.Sum(k => _missedAnalysis[k]?.MissedCount ?? 0);

        return summary;
    }

    private static WaveFeatures WaveFeatures(List<Snapshot> snaps, Wave wave)
    {
        var f = new WaveFeatures();
        try
        {
            var peakSnap = snaps[wave.PeakIdx];
            double avg = (double)peakSnap.AvgPrice.GetValueOrDefault();
            double peak = (double)peakSnap.Price;
            if (double.IsFinite(avg) && avg > 0 && double.IsFinite(peak) && peak > 0)
                f.VwapDevPct = JsMath.JsRound(((peak - avg) / avg * 100), 2);

            double durMin = Math.Max(1, (wave.PeakTime - wave.TroughTime) / 60000.0);
            f.SurgeSpeed5m = JsMath.JsRound(wave.RisePct / (durMin / 5), 2);

            int end = wave.PeakIdx;
            int s1 = Math.Max(0, end - 5);
            int s0 = Math.Max(0, s1 - 20);
            double recentSum = 0; int recentN = 0;
            double baseSum = 0; int baseN = 0;
            for (int i = s1; i <= end; i++)
            {
                double v = (double)snaps[i].Volume.GetValueOrDefault();
                bool reliable = snaps[i].VolumeReliable != false;
                if (reliable && double.IsFinite(v) && v > 0) { recentSum += v; recentN++; }
            }
            for (int i = s0; i < s1; i++)
            {
                double v = (double)snaps[i].Volume.GetValueOrDefault();
                bool reliable = snaps[i].VolumeReliable != false;
                if (reliable && double.IsFinite(v) && v > 0) { baseSum += v; baseN++; }
            }
            if (recentN > 0 && baseN > 0)
                f.VolumeExp = JsMath.JsRound((recentSum / recentN) / (baseSum / baseN), 2);
        }
        catch { /* ignore */ }
        return f;
    }

    private static FeatureCompareResult? CompareWaveFeatures(List<MissedWaveInfo> coveredList, List<MissedWaveInfo> missedList)
    {
        double? AvgOf(List<MissedWaveInfo> list, string key)
        {
            var vals = list.Select(w => w.Features).Where(f => f != null).Select(f =>
                key == "vwapDevPct" ? f!.VwapDevPct :
                key == "volumeExp" ? f!.VolumeExp :
                key == "surgeSpeed5m" ? f!.SurgeSpeed5m : (double?)null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? vals.Average() : null;
        }
        var cmp = new FeatureCompareResult
        {
            MissedVwapDev = AvgOf(missedList, "vwapDevPct"),
            CoveredVwapDev = AvgOf(coveredList, "vwapDevPct"),
            MissedVolExp = AvgOf(missedList, "volumeExp"),
            CoveredVolExp = AvgOf(coveredList, "volumeExp"),
            MissedSpeed = AvgOf(missedList, "surgeSpeed5m"),
            CoveredSpeed = AvgOf(coveredList, "surgeSpeed5m")
        };
        return (cmp.MissedVwapDev.HasValue || cmp.MissedVolExp.HasValue) ? cmp : null;
    }
}

// ============ 数据模型 ============

public class SignalEvent
{
    public string Id { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string SignalType { get; set; } = "";
    public string SignalLabel { get; set; } = "";
    public decimal Price { get; set; }
    public long Timestamp { get; set; }
    public string TimeStr { get; set; } = "";
    public int SnapshotIndex { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string DataMode { get; set; } = "snapshot";
    public SignalEvaluation? Evaluation { get; set; }
    public bool Evaluated { get; set; }
    public bool IsOptimized { get; set; }
    public int? OptimizationVersion { get; set; }
    public long? OptimizedAt { get; set; }
}

public class SignalEventInput
{
    public string StockCode { get; set; } = "";
    public string? StockName { get; set; }
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public decimal Price { get; set; }
    public long Timestamp { get; set; }
    public int SnapshotIndex { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? DataMode { get; set; }
}

public class SignalEvaluation
{
    public string Result { get; set; } = "neutral";
    public string? Reason { get; set; }
    public double MaxChangePct { get; set; }
    public int EvalWindowMin { get; set; }
    public double TriggerPrice { get; set; }
    public double? Reward { get; set; }
    public double Quality { get; set; }
    public double Capture { get; set; }
    public double TimeEfficiency { get; set; }
    public double CapturePct { get; set; }
    public bool NearDayHigh { get; set; }
    public bool BeforeMaxDrawdown { get; set; }
    public bool NearDayLow { get; set; }
    public int? WaveIdx { get; set; }
    public double? WaveCapture { get; set; }
    public bool NearWaveTop { get; set; }
    public double? WaveDepthPct { get; set; }
    public bool WaveHigh { get; set; }
    public bool WaveLow { get; set; }
    public double? RankScore { get; set; }
    public string? Detail { get; set; }
}

public class Snapshot
{
    public DateTime SnapshotAt { get; set; }
    public decimal Price { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Open { get; set; }
    public decimal? AvgPrice { get; set; }
    public decimal? Volume { get; set; }
    public decimal? CumulativeVolume { get; set; }
    public decimal? IntervalVolume { get; set; }
    public bool? VolumeReliable { get; set; }
    public decimal? PreClose { get; set; }
}

public class EvalConfig
{
    public int[] BuySignalEvalWindows { get; set; } = Array.Empty<int>();
    public double BuySuccessThreshold { get; set; }
    public int[] SellSignalEvalWindows { get; set; } = Array.Empty<int>();
    public double SellSuccessThreshold { get; set; }
    public int[] RapidRiseEvalWindows { get; set; } = Array.Empty<int>();
    public double RapidRiseSuccessThreshold { get; set; }
    public int[] RapidFallEvalWindows { get; set; } = Array.Empty<int>();
    public double RapidFallSuccessThreshold { get; set; }
}

public class RewardInfo
{
    public double Reward { get; set; }
    public double Quality { get; set; }
    public double Capture { get; set; }
    public double TimeEfficiency { get; set; }
    public double CapturePct { get; set; }
}

public class OptimalExitPoints
{
    public int DayHighIdx { get; set; }
    public long DayHighTime { get; set; }
    public double DayHighPrice { get; set; }
    public int MaxDrawdownPeakIdx { get; set; }
    public long MaxDrawdownPeakTime { get; set; }
    public double MaxDrawdownPeakPrice { get; set; }
    public int MaxDrawdownEndIdx { get; set; }
    public double MaxDrawdownEndPrice { get; set; }
    public double MaxDrawdownPct { get; set; }
    public int DayLowIdx { get; set; }
    public long? DayLowTime { get; set; }
    public double DayLowPrice { get; set; }
}

public class Pivot
{
    public int Idx { get; set; }
    public char Type { get; set; }
    public double Price { get; set; }
}

public class Wave
{
    public int WaveIdx { get; set; }
    public int TroughIdx { get; set; }
    public long TroughTime { get; set; }
    public double TroughPrice { get; set; }
    public int PeakIdx { get; set; }
    public long PeakTime { get; set; }
    public double PeakPrice { get; set; }
    public double RisePct { get; set; }
    public int EndIdx { get; set; }
    public int BottomIdx { get; set; }
    public long BottomTime { get; set; }
    public double BottomPrice { get; set; }
    public double DepthPct { get; set; }
}

public class SignalTypeStat
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Fail { get; set; }
    public int Neutral { get; set; }
    public double AvgChangePct { get; set; }
    public double? AvgReward { get; set; }
    public List<StatHistoryRecord>? History { get; set; }
    public int NearDayHighCount { get; set; }
    public int BeforeMaxDrawdownCount { get; set; }
    public int NearDayLowCount { get; set; }
}

public class StatHistoryRecord
{
    public string Date { get; set; } = "";
    public string Result { get; set; } = "";
    public double ChangePct { get; set; }
    public double Reward { get; set; }
    public bool NearDayHigh { get; set; }
    public bool BeforeMaxDrawdown { get; set; }
    public bool NearDayLow { get; set; }
    public string? StockCode { get; set; }
}

public class RecentSignalStat
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Fail { get; set; }
    public int Neutral { get; set; }
    public double AvgChangePct { get; set; }
    public double AvgReward { get; set; }
    public int NearDayHighCount { get; set; }
    public int BeforeMaxDrawdownCount { get; set; }
    public int NearDayLowCount { get; set; }
    public int WaveHighCount { get; set; }
    public int WaveLowCount { get; set; }
}

public class StockQualityStat
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int TodayTotal { get; set; }
    public int Total { get; set; }
    public int High { get; set; }
    public int Mid { get; set; }
    public int Low { get; set; }
}

public class ReplayResult
{
    public int Replayable { get; set; }
    public int LowTotal { get; set; }
    public int LowFiltered { get; set; }
    public double? LowFilterRate { get; set; }
    public int HighTotal { get; set; }
    public int HighKept { get; set; }
    public double? HighKeepRate { get; set; }
    public int Stage1LowFiltered { get; set; }
    public int Stage1HighKept { get; set; }
    public List<WaveReplayInfo> Waves { get; set; } = new();
    public List<string> WaveViolations { get; set; } = new();
    public Dictionary<string, double> Blame { get; set; } = new();
    public Dictionary<string, double> Credit { get; set; } = new();
}

public class ReplayEventInfo
{
    public SignalEvent Event { get; set; } = new();
    public string Quality { get; set; } = "";
    public bool Stage1Pass { get; set; }
    public bool Stage2Pass { get; set; }
    public double Strength { get; set; }
    public string DateKey { get; set; } = "";
    public Dictionary<string, double>? Contributions { get; set; }
}

public class WaveReplayInfo
{
    public string WaveKey { get; set; } = "";
    public string DateKey { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int WaveIdx { get; set; }
    public double? DepthPct { get; set; }
    public int HighTotal { get; set; }
    public int HighKept { get; set; }
    public double Top1Rank { get; set; }
    public bool Top1Alive { get; set; }
}

public class FactorRewardStat
{
    public int Total { get; set; }
    public double RewardSum { get; set; }
    public double ScoreSum { get; set; }
    public int HighRewardCount { get; set; }
    public int OptimalHitCount { get; set; }
    public int HighQualityCount { get; set; }
    public double HighQualityScoreSum { get; set; }
    public int LowQualityCount { get; set; }
    public double LowQualityScoreSum { get; set; }
    public double AvgReward { get; set; }
    public double AvgScore { get; set; }
    public double HighRewardRate { get; set; }
    public double OptimalHitRate { get; set; }
    public double HighQualityAvgScore { get; set; }
    public double LowQualityAvgScore { get; set; }
    public double DiscriminativePower { get; set; }
}

public class OptimizationSuggestion
{
    public string SignalType { get; set; } = "";
    public string? SignalLabel { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public double WinRate { get; set; }
    public int Total { get; set; }
    public double AvgChangePct { get; set; }
}

// ============ 归因账本模型 ============

public class AttributionLedger
{
    public Dictionary<string, AttributionEntry> Entries { get; set; } = new();
    public long UpdatedAt { get; set; }
    public string? DayKey { get; set; }
}

public class AttributionEntry
{
    public string Kind { get; set; } = "signal";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "normal";
    public bool Frozen { get; set; }
    public string FreezeReason { get; set; } = "";
    public long? FrozenAt { get; set; }
    public int DirectionStreak { get; set; }
    public int TotalLowFiltered { get; set; }
    public int TotalHighKilled { get; set; }
    public int? FailedSteps { get; set; }
    public List<AttributionHistoryRecord>? History { get; set; }
}

public class AttributionHistoryRecord
{
    public long Ts { get; set; }
    public double Delta { get; set; }
    public double Net { get; set; }
    public int LowFiltered { get; set; }
    public int HighKilled { get; set; }
    public string? Note { get; set; }
}

public class AttributionRoundEntry
{
    public string? ParamKey { get; set; }
    public string? Kind { get; set; }
    public string? Label { get; set; }
    public double Delta { get; set; }
    public int LowFiltered { get; set; }
    public int HighKilled { get; set; }
    public bool Failed { get; set; }
}

// ============ 漏报分析模型 ============

public class MissedAnalysisSummary
{
    public string DateKey { get; set; } = "";
    public int SignificantWaves { get; set; }
    public int MissedCount { get; set; }
    public List<MissedWaveInfo>? Missed { get; set; }
    public FeatureCompareResult? FeatureCompare { get; set; }
    public string? MutedHint { get; set; }
    public long UpdatedAt { get; set; }
    public int RecentSignificant { get; set; }
    public int RecentMissed { get; set; }
}

public class MissedWaveInfo
{
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public int WaveIdx { get; set; }
    public long PeakTime { get; set; }
    public string? PeakTimeStr { get; set; }
    public double RisePct { get; set; }
    public double DepthPct { get; set; }
    public string Coverage { get; set; } = "";
    public List<string>? MutedTypes { get; set; }
    public Dictionary<string, string>? MutedLabels { get; set; }
    public WaveFeatures? Features { get; set; }
}

public class WaveFeatures
{
    public double? VwapDevPct { get; set; }
    public double? SurgeSpeed5m { get; set; }
    public double? VolumeExp { get; set; }
}

public class FeatureCompareResult
{
    public double? MissedVwapDev { get; set; }
    public double? CoveredVwapDev { get; set; }
    public double? MissedVolExp { get; set; }
    public double? CoveredVolExp { get; set; }
    public double? MissedSpeed { get; set; }
    public double? CoveredSpeed { get; set; }
}
