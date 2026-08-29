using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.MarketData;

namespace StockReview.Core.Engines;

public partial class SellPointDetectorService
{
    private const int MaxIntradaySnapshots = 480;

    private static readonly TimeZoneInfo ChinaTz = StockReview.Core.Services.CnTimeZone.Get;

    private readonly DatabaseService _db;
    private readonly MarketDataAggregator _marketData;
    private readonly IPatternSimilarityCalculator? _patternSimilarity;
    private readonly IMultiFactorEvaluator? _multiFactorEngine;

    private static readonly HashSet<string> MaBreakTypes = new()
    { SignalTypes.VwapBreakdown, SignalTypes.BreakMa5, SignalTypes.BreakMa10, SignalTypes.BreakMa30 };
    private static readonly HashSet<string> StoplossOnlyTypes = new()
    { SignalTypes.WeakReboundFailure, SignalTypes.PlatformBreakdown, SignalTypes.BreakSupport, SignalTypes.DeepDropRebound };
    private static readonly HashSet<string> ProfitOnlyTypes = new()
    { SignalTypes.SurgePullback, SignalTypes.VolumeStagnant, SignalTypes.DoubleTop,
      SignalTypes.TripleTop, SignalTypes.FishingLine, SignalTypes.HighDeviationPullback,
      SignalTypes.TopDivergence, SignalTypes.VolumeDivergence };
    private static readonly HashSet<string> BreakdownTypes = new()
    { SignalTypes.WeakReboundFailure, SignalTypes.DeepDropRebound,
      SignalTypes.VwapBreakdown, SignalTypes.VwapSlopeDown,
      SignalTypes.PlatformBreakdown, SignalTypes.BreakMa5,
      SignalTypes.BreakMa10, SignalTypes.BreakMa30, SignalTypes.BreakSupport };

    private SellPointDetectorConfig _config = new();
    private readonly ConcurrentDictionary<string, PlanState> _planStates = new();
    private Dictionary<string, double> _signalMultipliers = new();
    private readonly object _multiplierLock = new();
    private bool _hasWarnedNoDailyKline;


    public SellPointDetectorService(
        DatabaseService db,
        MarketDataAggregator marketData,
        IPatternSimilarityCalculator? patternSimilarity = null,
        IMultiFactorEvaluator? multiFactorEngine = null)
    {
        _db = db;
        _marketData = marketData;
        _patternSimilarity = patternSimilarity;
        _multiFactorEngine = multiFactorEngine;
        _signalMultipliers = LoadSignalMultipliers();
    }

    // ==================== 配置 & 乘子管理 ====================

    public void UpdateConfig(SellPointDetectorConfig updates)
    {
        _config = updates;
    }


    public SellPointDetectorConfig GetConfig() => _config;

    /// <summary>
    /// 加载历史自进化信号权重乘子
    /// </summary>
    private Dictionary<string, double> LoadSignalMultipliers()
    {
        try
        {
            var row = _db?.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = @key",
                new { key = "pet_signal_weight_multipliers" });
            if (!string.IsNullOrEmpty(row))
                return JsonSerializer.Deserialize<Dictionary<string, double>>(row) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[卖点检测] 加载信号权重乘子失败");
        }
        return new();
    }

    /// <summary>
    /// 持久化信号权重乘子到 DB
    /// </summary>
    private void SaveSignalMultipliers()
    {
        try
        {
            var serialized = JsonSerializer.Serialize(_signalMultipliers);
            _db?.Execute(
                "INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)",
                new { key = "pet_signal_weight_multipliers", val = serialized });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[卖点检测] 持久化信号权重乘子失败");
        }
    }

    /// <summary>
    /// 更新信号权重乘子（由外部自进化引擎调用）
    /// </summary>
    public void UpdateSignalMultipliers(Dictionary<string, double> multipliers)
    {
        lock (_multiplierLock)
        {
            foreach (var (type, m) in multipliers)
            {
                if (!double.IsFinite(m)) continue;
                _signalMultipliers[type] = Math.Max(0.15, Math.Min(1.6, m));
            }
            SaveSignalMultipliers();
        }
    }

    /// <summary>
    /// 获取信号乘子（调试/展示用）
    /// </summary>
    public Dictionary<string, double> GetSignalMultipliers()
    {
        lock (_multiplierLock) { return new Dictionary<string, double>(_signalMultipliers); }
    }


    private double GetMultiplier(string type)
    {
        lock (_multiplierLock)
        {
            return _signalMultipliers.TryGetValue(type, out var m) ? m : 1.0;
        }
    }

    // ==================== 状态管理 ====================

    public PlanState GetPlanState(string planId)
    {
        return _planStates.GetOrAdd(planId, _ => new PlanState());
    }


    public void ClearPlanState(string planId)
    {
        _planStates.TryRemove(planId, out _);
    }


    public void ClearStaleStates(long maxAgeMs = 24 * 60 * 60 * 1000)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kvp in _planStates)
        {
            var refTime = kvp.Value.LastUpdatedAt > 0 ? kvp.Value.LastUpdatedAt : kvp.Value.CreatedAt;
            if (refTime > 0 && now - refTime > maxAgeMs)
                _planStates.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// 更新计划状态机
    /// </summary>
    public PlanState UpdatePlanState(string planId, List<IntradaySnapshot> snapshots, double currentPrice)
    {
        var state = GetPlanState(planId);
        state.LastUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var current = snapshots[^1];
        var avgPrice = current?.AvgPrice ?? 0;

        var maxPrice = currentPrice;
        foreach (var s in snapshots)
        {
            if (double.IsFinite(s.Price) && s.Price > maxPrice) maxPrice = s.Price;
        }
        if (maxPrice > state.PeakPrice)
        {
            state.PeakPrice = maxPrice;
            state.HighReached = true;
        }

        if (avgPrice > 0)
        {
            if (currentPrice < avgPrice)
            {
                if (state.VwapBreakdownSnapshotIndex < 0)
                {
                    state.VwapBreakdownAt = current?.SnapshotAt ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get);
                    state.VwapBreakdownSnapshotIndex = snapshots.Count - 1;
                    state.VwapBreakdownPrice = currentPrice;
                }
            }
            else
            {
                state.VwapBreakdownAt = null;
                state.VwapBreakdownSnapshotIndex = -1;
                state.VwapBreakdownPrice = 0;
                state.VwapBreakdownSignaled = false;
            }
        }

        state.LastSnapshotLength = snapshots.Count;
        return state;
    }

    // ==================== 数据预处理 ====================
    /// <summary>
    /// 裁剪日内全量快照上限（性能保护）
    /// </summary>
    public List<IntradaySnapshot> NormalizeIntraday(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return new();
        if (snapshots.Count <= MaxIntradaySnapshots) return snapshots;
        return snapshots.Skip(snapshots.Count - MaxIntradaySnapshots).ToList();
    }

    /// <summary>
    /// 成交量语义标准化（幂等安全）
    /// </summary>
    public void NormalizeVolumes(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return;
        for (var i = snapshots.Count - 1; i > 0; i--)
        {
            var curr = snapshots[i];
            var prev = snapshots[i - 1];
            if (curr.IntervalVolume.HasValue) continue;
            var currCum = curr.CumulativeVolume;
            var prevCum = prev.CumulativeVolume;
            if (currCum > 0 && prevCum > 0 && currCum >= prevCum)
            {
                curr.IntervalVolume = currCum - prevCum;
            }
        }
        if (snapshots[0] != null && !snapshots[0].IntervalVolume.HasValue)
        {
            snapshots[0].IntervalVolume = snapshots[0].Volume;
        }
    }

    /// <summary>
    /// 读取快照的区间成交量
    /// </summary>
    private static double GetIntervalVolume(IntradaySnapshot? snapshot)
    {
        if (snapshot == null) return 0;
        return snapshot.IntervalVolume ?? snapshot.Volume;
    }

    /// <summary>
    /// 分时均价修复：填充缺失/突变值
    /// </summary>
    public void RepairAvgPrice(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return;

        // Pass 1：填充缺失/为0的 avgPrice
        var lastValidAvg = 0.0;
        foreach (var s in snapshots)
        {
            if (s.AvgPrice > 0)
            {
                lastValidAvg = s.AvgPrice;
            }
            else if (lastValidAvg > 0)
            {
                s.AvgPrice = lastValidAvg;
            }
        }

        // Pass 2：突变校验——与前后有效值偏差 > 5% 的异常值用线性插值替换
        for (var i = 1; i < snapshots.Count - 1; i++)
        {
            var curr = snapshots[i].AvgPrice;
            var prev = snapshots[i - 1].AvgPrice;
            var next = snapshots[i + 1].AvgPrice;
            if (curr <= 0 || prev <= 0 || next <= 0) continue;

            var devPrev = Math.Abs(curr - prev) / prev;
            var devNext = Math.Abs(curr - next) / next;
            if (devPrev > 0.05 && devNext > 0.05)
            {
                snapshots[i].AvgPrice = (prev + next) / 2;
            }
        }
    }

    /// <summary>
    /// 鲁棒局部峰值检测（标准 prominence 口径）
    /// </summary>
    public List<PeakInfo> FindPeaksRobust(List<double> prices, int radius = 2, double minRelHeight = 0.2)
    {
        var peaks = new List<PeakInfo>();
        for (var i = radius; i < prices.Count - radius; i++)
        {
            var p = prices[i];
            var isPeak = true;
            for (var j = 1; j <= radius; j++)
            {
                if (p <= prices[i - j] || p <= prices[i + j]) { isPeak = false; break; }
            }
            if (!isPeak) continue;

            var leftMin = p;
            for (var k = i - 1; k >= 0; k--)
            {
                if (prices[k] >= p) break;
                if (prices[k] < leftMin) leftMin = prices[k];
            }
            var rightMin = p;
            for (var k = i + 1; k < prices.Count; k++)
            {
                if (prices[k] >= p) break;
                if (prices[k] < rightMin) rightMin = prices[k];
            }
            var prominence = p - Math.Max(leftMin, rightMin);
            var relHeight = p > 0 ? prominence / p * 100 : 0;
            if (relHeight < minRelHeight) continue;

            peaks.Add(new PeakInfo { Index = i, Price = p, Prominence = prominence, RelHeight = relHeight });
        }
        return peaks;
    }

    /// <summary>
    /// 信号去重
    /// </summary>
    public List<SellPointSignal> DeduplicateSignals(List<SellPointSignal> signals, long bucketMs = 300000)
    {
        if (signals == null || signals.Count <= 1) return signals ?? new();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var groups = new Dictionary<string, SellPointSignal>();
        foreach (var s in signals)
        {
            var ts = s.Timestamp > 0 ? s.Timestamp : now;
            var timeBucket = ts / bucketMs;
            var t = (s.Type ?? "").ToLowerInvariant();
            string category;
            if (t.Contains("vwap")) category = "vwap";
            else if (t.Contains("volume")) category = "volume";
            else if (t.Contains("top") || t.Contains("double")) category = "top";
            else if (t.StartsWith("atr_") || t == "time_stop") category = t;
            else category = "other";
            var key = $"{timeBucket}_{category}";
            if (!groups.ContainsKey(key) || s.Weight > groups[key].Weight)
                groups[key] = s;
        }
        return groups.Values.ToList();
    }

    /// <summary>
    /// 计算信号时间集中度得分
    /// </summary>

    private (double leftUpLegVol, double rightUpLegVol) CalculateLegVolumes(
        List<double> prices, List<double> volumes, int leftIdx, int rightIdx)
    {
        var leftLegStart = Math.Max(0, leftIdx - 12);
        var leftLegTroughIdx = leftLegStart;
        for (var k = leftLegStart; k <= leftIdx; k++)
        {
            if (prices[k] < prices[leftLegTroughIdx]) leftLegTroughIdx = k;
        }
        var leftUpLegVol = 0.0;
        for (var k = leftLegTroughIdx; k <= leftIdx; k++) leftUpLegVol += volumes[k];

        var neckTroughIdx = leftIdx;
        for (var k = leftIdx; k <= rightIdx; k++)
        {
            if (prices[k] < prices[neckTroughIdx]) neckTroughIdx = k;
        }
        var rightUpLegVol = 0.0;
        for (var k = neckTroughIdx; k <= rightIdx; k++) rightUpLegVol += volumes[k];

        return (leftUpLegVol, rightUpLegVol);
    }


    private (double leg1Vol, double leg3Vol) CalculateTripleLegVolumes(
        List<double> prices, List<double> volumes, int p1Idx, int p2Idx, int p3Idx)
    {
        var leg1Start = Math.Max(0, p1Idx - 12);
        var leg1TroughIdx = leg1Start;
        for (var k = leg1Start; k <= p1Idx; k++)
        {
            if (prices[k] < prices[leg1TroughIdx]) leg1TroughIdx = k;
        }
        var leg1Vol = 0.0;
        for (var k = leg1TroughIdx; k <= p1Idx; k++) leg1Vol += volumes[k];

        var trough23Idx = p2Idx;
        for (var k = p2Idx; k <= p3Idx; k++)
        {
            if (prices[k] < prices[trough23Idx]) trough23Idx = k;
        }
        var leg3Vol = 0.0;
        for (var k = trough23Idx; k <= p3Idx; k++) leg3Vol += volumes[k];

        return (leg1Vol, leg3Vol);
    }


    private static (int hour, int minute) GetHourMin(DateTime time)
    {
        var shTime = TimeZoneInfo.ConvertTime(time, ChinaTz);
        return (shTime.Hour, shTime.Minute);
    }

    // ==================== 便捷接口 ====================
    /// <summary>
    /// 检测卖点信号（兼容旧接口，内部调用 Analyze）
    /// </summary>
    public async Task<SellSignal?> DetectAsync(string stockCode, DateTime date, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var quote = await _marketData.GetQuoteAsync(stockCode);
        if (quote == null) return null;

        var result = Analyze(null, quote, new List<IntradaySnapshot>(), null, null, cancellationToken);
        if (result.Signals.Count == 0) return null;

        return new SellSignal
        {
            StockCode = stockCode,
            Date = date,
            Score = (decimal)result.TotalScore,
            Reasons = result.Signals.Select(s => s.LevelName).Distinct().ToList(),
            Level = result.TotalScore >= 70 ? SignalLevel.Extreme
                  : result.TotalScore >= 50 ? SignalLevel.Strong
                  : result.TotalScore >= 35 ? SignalLevel.Medium
                  : result.TotalScore >= 20 ? SignalLevel.Weak
                  : SignalLevel.None
        };
    }
}


