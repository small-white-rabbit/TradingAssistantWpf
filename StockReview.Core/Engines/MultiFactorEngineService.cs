using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Engines;

/// <summary>
/// 多因子评分引擎
/// 9 个因子：价格位置/拉升角度/量能/均线压力/K线形态/分时形态/动量/时间/资金流向
/// 综合评分 = Σ(factorScore × factorWeight × directionModifier) + 共振加成
/// 自进化：权重可被动态调整，持久化到 appConfig 表
/// </summary>
public class MultiFactorEngineService
{
    private const string WeightsKey = "pet_multifactor_weights";
    private readonly IDatabaseService? _db;

    /// <summary>
    /// 默认因子权重（sum=1.0）
    /// </summary>
    public static readonly Dictionary<string, double> DefaultWeights = new()
    {
        ["pricePosition"] = 0.13,
        ["surgeAngle"] = 0.16,
        ["volume"] = 0.16,
        ["maPressure"] = 0.19,
        ["klinePattern"] = 0.06,
        ["intradayPattern"] = 0.11,
        ["momentum"] = 0.09,
        ["timeFactor"] = 0.04,
        ["capitalFlow"] = 0.06
    };

    private Dictionary<string, double> _weights;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    public MultiFactorEngineService(IDatabaseService? db = null)
    {
        _db = db;
        _weights = LoadWeights();
        Log.Information("[MultiFactorEngine] 多因子引擎初始化完成");
    }

    // ============ 权重持久化 ============

    private Dictionary<string, double> LoadWeights()
    {
        try
        {
            if (_db != null)
            {
                var row = _db.GetById("appConfig", WeightsKey);
                if (row != null && row.TryGetValue("value", out var v) && v != null)
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(v.ToString()!, JsonOpts);
                    if (saved != null)
                    {
                        // 合并：保留默认中存在但 saved 缺失的字段
                        var merged = new Dictionary<string, double>(DefaultWeights);
                        foreach (var (k, val) in saved) merged[k] = val;
                        return merged;
                    }
                }
            }
        }
        catch (Exception e) { Log.Warning(e, "[MultiFactorEngine] 加载权重失败"); }
        return new Dictionary<string, double>(DefaultWeights);
    }

    private void SaveWeights()
    {
        try
        {
            if (_db == null) return;
            var json = JsonSerializer.Serialize(_weights, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = WeightsKey, ["value"] = json
            });
        }
        catch (Exception e) { Log.Warning(e, "[MultiFactorEngine] 保存权重失败"); }
    }

    public void ReloadWeights()
    {
        _weights = LoadWeights();
    }

    // ============ 主入口 ============

    /// <summary>
    /// 提取所有因子并计算综合评分
    /// </summary>
    public MultiFactorResult Evaluate(
        List<MarketSnapshot> snapshots, double currentPrice,
        List<DailyKline>? dailyKlines = null,
        List<DetectedSignal>? detectedSignals = null,
        CapitalFlowData? capitalFlowData = null,
        CancellationToken ct = default)
    {
        if (snapshots == null || snapshots.Count < 5)
        {
            return new MultiFactorResult { TotalScore = 0, Direction = "neutral", Confidence = 0, Detail = "数据不足" };
        }

        var factors = new List<FactorResult>();

        factors.Add(ExtractPricePositionFactor(snapshots, currentPrice));
        factors.Add(ExtractSurgeAngleFactor(snapshots, currentPrice));
        factors.Add(ExtractVolumeFactor(snapshots, currentPrice));
        factors.Add(ExtractMAPressureFactor(snapshots, currentPrice, dailyKlines));
        factors.Add(ExtractKlinePatternFactor(snapshots, currentPrice));
        factors.Add(ExtractIntradayPatternFactor(detectedSignals ?? new()));
        factors.Add(ExtractMomentumFactor(snapshots, currentPrice));
        factors.Add(ExtractTimeFactor(snapshots));

        if (capitalFlowData != null && capitalFlowData.Available)
        {
            factors.Add(ExtractCapitalFlowFactor(capitalFlowData, snapshots, currentPrice));
        }

        // 综合评分
        var totalScore = 0.0;
        var totalWeight = 0.0;
        var bearCount = 0;
        var bullCount = 0;

        foreach (var f in factors)
        {
            if (f.Direction == "neutral") continue;
            var weight = _weights.GetValueOrDefault(f.Key, 0);
            if (f.Direction == "bear")
            {
                totalScore += f.Score * weight;
                totalWeight += weight;
                bearCount++;
            }
            else if (f.Direction == "bull")
            {
                totalScore -= f.Score * weight * 0.5;
                bullCount++;
            }
        }

        var normalizedScore = totalWeight > 0
            ? Math.Max(0, Math.Min(100, totalScore / totalWeight)) : 0.0;

        // 共振加成
        var resonanceBonus = 0;
        if (bearCount >= 5) resonanceBonus = 35;
        else if (bearCount >= 4) resonanceBonus = 25;
        else if (bearCount >= 3) resonanceBonus = 15;

        var finalScore = Math.Min(100, normalizedScore + resonanceBonus);

        // 方向判定
        var direction = "neutral";
        if (finalScore >= 60 && bearCount >= 2) direction = "bear";
        else if (finalScore <= 30 && bullCount >= 2) direction = "bull";

        // 置信度
        var consistency = bearCount + bullCount > 0
            ? (double)Math.Max(bearCount, bullCount) / (bearCount + bullCount) : 0.5;
        var sampleConfidence = Math.Min(1.0, (double)snapshots.Count / 60);
        var confidence = consistency * sampleConfidence;

        // 详情
        var bearFactors = factors.Where(f => f.Direction == "bear").OrderByDescending(f => f.Score).ToList();
        var detail = bearFactors.Count > 0
            ? string.Join(" + ", bearFactors.Select(f => $"{f.Name}:{f.Score:F2}"))
            : "无明显看空因子";

        return new MultiFactorResult
        {
            TotalScore = finalScore,
            Factors = factors,
            Direction = direction,
            Confidence = confidence,
            BearCount = bearCount,
            BullCount = bullCount,
            ResonanceBonus = resonanceBonus,
            Detail = detail,
            Weights = new Dictionary<string, double>(_weights)
        };
    }

    // ============ 因子1: 价格位置 ============
    public FactorResult ExtractPricePositionFactor(List<MarketSnapshot> snapshots, double currentPrice)
    {
        var prices = snapshots.Select(s => s.Price).Where(p => p > 0).ToList();
        if (prices.Count < 2)
            return new() { Key = "pricePosition", Name = "价格位置", Score = 0, Direction = "neutral", Detail = "数据不足" };

        var dayLow = prices.Min();
        var dayHigh = prices.Max();
        var range = dayHigh - dayLow;
        var position = range > 0 ? (currentPrice - dayLow) / range : 0.5;

        var avgPrice = snapshots.Last().AvgPrice;
        var deviationPct = avgPrice > 0 ? ((currentPrice - avgPrice) / avgPrice) * 100 : 0;

        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        if (position > 0.8) { score += 40; direction = "bear"; reasons.Add($"日内高位({position * 100:F0}%)"); }
        else if (position > 0.6) { score += 20; direction = "bear"; reasons.Add($"偏高位({position * 100:F0}%)"); }
        else if (position < 0.2) { score += 30; direction = "bull"; reasons.Add($"日内低位({position * 100:F0}%)"); }

        if (deviationPct > 2.0) { score += 35; direction = "bear"; reasons.Add($"高乖离{deviationPct:F1}%"); }
        else if (deviationPct > 1.0) { score += 15; if (direction != "bear") direction = "bear"; reasons.Add($"乖离{deviationPct:F1}%"); }

        return new()
        {
            Key = "pricePosition", Name = "价格位置",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : $"位置{position * 100:F0}%, 乖离{deviationPct:F1}%"
        };
    }

    // ============ 因子2: 拉升角度 ============
    public FactorResult ExtractSurgeAngleFactor(List<MarketSnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 9)
            return new() { Key = "surgeAngle", Name = "拉升角度", Score = 0, Direction = "neutral", Detail = "数据不足" };

        var windows = new[] { 9, 30, 60 };
        var maxSlope = 0.0;
        var maxSlopeWindow = "";
        var hasRecentSurge = false;
        var isStagnating = false;

        foreach (var bars in windows)
        {
            if (snapshots.Count < bars) continue;
            var recent = snapshots.TakeLast(bars).ToList();
            var prices = recent.Select(s => s.Price).Where(p => p > 0).ToList();
            if (prices.Count < 2) continue;

            var firstPrice = prices[0];
            var highPrice = prices.Max();
            var lowPrice = prices.Min();
            var surgePct = lowPrice > 0 ? ((highPrice - lowPrice) / lowPrice) * 100 : 0;
            var minutes = bars * 10.0 / 60;
            var slope = surgePct / minutes;

            if (slope > maxSlope) { maxSlope = slope; maxSlopeWindow = $"{minutes:F1}min"; }
            if (surgePct >= 1.5 && minutes <= 5) hasRecentSurge = true;
        }

        // 滞涨检测
        if (snapshots.Count >= 15)
        {
            var recent5 = snapshots.TakeLast(5).ToList();
            var prev10 = snapshots.Skip(snapshots.Count - 15).Take(10).ToList();
            var recent5Change = Math.Abs((recent5[^1].Price - recent5[0].Price) / recent5[0].Price * 100);
            var prev10Change = (prev10[^1].Price - prev10[0].Price) / prev10[0].Price * 100;
            if (prev10Change >= 1.0 && recent5Change < 0.3) isStagnating = true;
        }

        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        if (hasRecentSurge && isStagnating) { score = 70; direction = "bear"; reasons.Add("急拉后滞涨"); }
        else if (hasRecentSurge) { score = 40; direction = "bear"; reasons.Add($"近期急拉(斜率{maxSlope:F2}%/min)"); }
        else if (maxSlope > 0.5) { score = 25; direction = "bear"; reasons.Add($"持续拉升({maxSlopeWindow}斜率{maxSlope:F2}%)"); }

        if (maxSlope > 2.0) { score = Math.Min(100, score + 20); reasons.Add("拉升过急"); }

        return new()
        {
            Key = "surgeAngle", Name = "拉升角度",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : $"最大斜率{maxSlope:F2}%/min"
        };
    }

    // ============ 因子3: 量能 ============
    public FactorResult ExtractVolumeFactor(List<MarketSnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10)
            return new() { Key = "volume", Name = "量能", Score = 0, Direction = "neutral", Detail = "数据不足" };

        double GetVol(MarketSnapshot s) => (s.IntervalVolume ?? 0) > 0 ? s.IntervalVolume!.Value : s.Volume;

        var recent = snapshots.TakeLast(6).ToList();
        var prev = snapshots.Skip(snapshots.Count - 12).Take(6).ToList();
        var recentVol = recent.Average(s => GetVol(s));
        var prevVol = prev.Count > 0 ? prev.Average(s => GetVol(s)) : 0;

        if (prevVol == 0 || recentVol == 0)
            return new() { Key = "volume", Name = "量能", Score = 0, Direction = "neutral", Detail = "无量" };

        var volRatio = recentVol / prevVol;
        var priceChange = prev.Count > 0 && prev[0].Price > 0
            ? ((currentPrice - prev[0].Price) / prev[0].Price) * 100 : 0;

        // 单根巨量检测
        var lastVol = GetVol(snapshots[^1]);
        var prev5Vols = snapshots.Skip(Math.Max(0, snapshots.Count - 6)).Take(5).Select(GetVol).ToList();
        var prev5Avg = prev5Vols.Count > 0 ? prev5Vols.Average() : 0;
        var spikeMultiple = prev5Avg > 0 ? lastVol / prev5Avg : 0;

        // CV
        var prev5Mean = prev5Avg;
        var prev5Variance = prev5Mean > 0
            ? prev5Vols.Select(v => Math.Pow(v - prev5Mean, 2)).Sum() / Math.Max(1, prev5Vols.Count) : 0;
        var prev5Cv = prev5Mean > 0 ? Math.Sqrt(prev5Variance) / prev5Mean : 0;

        // 价格位置
        var allPrices = snapshots.Select(s => s.Price).ToList();
        var dayLow = allPrices.Min();
        var dayHigh = allPrices.Max();
        var dayRange = dayHigh - dayLow;
        var currentPosition = dayRange > 0 ? (currentPrice - dayLow) / dayRange : 0.5;

        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        // 优先级最高：单根巨量做顶
        if (spikeMultiple >= 2.5 && prev5Cv < 0.45 && currentPosition >= 0.55)
        {
            score = 78; direction = "bear";
            reasons.Add($"单根巨量{spikeMultiple:F1}倍做顶");
            if (prev5Cv < 0.25) { score = 85; reasons.Add("量能断层(非连续带量)"); }
        }
        else if (volRatio >= 1.6 && Math.Abs(priceChange) < 0.5)
        { score = 65; direction = "bear"; reasons.Add($"放量{volRatio:F1}倍滞涨"); }
        else if (volRatio >= 1.5 && priceChange < -0.3)
        { score = 55; direction = "bear"; reasons.Add($"放量{volRatio:F1}倍下跌"); }
        else if (volRatio < 0.6 && priceChange > 0.3)
        { score = 45; direction = "bear"; reasons.Add($"缩量上涨(量比{volRatio:F2})"); }
        else if (volRatio >= 1.3 && priceChange > 0.3)
        {
            score = 30; direction = "bull";
            if (prev5Cv >= 0.45 && spikeMultiple < 2.0)
                reasons.Add($"连续带量上涨(量比{volRatio:F1},CV{prev5Cv:F2})");
            else
                reasons.Add($"放量上涨(量比{volRatio:F1})");
        }

        return new()
        {
            Key = "volume", Name = "量能",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : $"量比{volRatio:F2}"
        };
    }

    // ============ 因子4: 均线压力 ============
    public FactorResult ExtractMAPressureFactor(List<MarketSnapshot> snapshots, double currentPrice,
        List<DailyKline>? dailyKlines)
    {
        MAValues? maValues = null;

        if (dailyKlines != null && dailyKlines.Count > 0)
        {
            double? CalcMA(int period)
            {
                if (dailyKlines.Count < period) return null;
                return dailyKlines.TakeLast(period).Average(k => k.Close);
            }
            maValues = new()
            {
                MA5 = CalcMA(5), MA10 = CalcMA(10), MA20 = CalcMA(20), MA30 = CalcMA(30),
                PreClose = dailyKlines[^1].Close
            };
        }

        if (maValues == null)
        {
            var avgPrice = snapshots.Last().AvgPrice;
            if (avgPrice > 0)
            {
                var distPct = ((currentPrice - avgPrice) / avgPrice) * 100;
                if (distPct > 1.5)
                    return new() { Key = "maPressure", Name = "均线压力", Score = 35, Direction = "bear", Detail = $"远离均价线{distPct:F1}%" };
            }
            return new() { Key = "maPressure", Name = "均线压力", Score = 0, Direction = "neutral", Detail = "无日K数据" };
        }

        var mas = new List<(string Name, double Value, double Weight)>();
        if (maValues.MA5.HasValue) mas.Add(("MA5", maValues.MA5.Value, 1.2));
        if (maValues.MA10.HasValue) mas.Add(("MA10", maValues.MA10.Value, 1.0));
        if (maValues.MA20.HasValue) mas.Add(("MA20", maValues.MA20.Value, 0.8));
        if (maValues.MA30.HasValue) mas.Add(("MA30", maValues.MA30.Value, 0.6));
        if (maValues.PreClose.HasValue) mas.Add(("昨收", maValues.PreClose.Value, 0.9));

        var maxScore = 0.0;
        var nearestMA = "";
        var direction = "neutral";
        var reasons = new List<string>();

        foreach (var (name, value, weight) in mas)
        {
            var distPct = Math.Abs((currentPrice - value) / value * 100);
            if (distPct < 0.5)
            {
                var score = 60 * weight;
                if (score > maxScore) { maxScore = score; nearestMA = name; direction = "bear"; }
                reasons.Add($"{name}近距{distPct:F2}%");
            }
            else if (distPct < 1.0)
            {
                var score = 35 * weight;
                if (score > maxScore) { maxScore = score; nearestMA = name; direction = "bear"; }
                reasons.Add($"{name}接近{distPct:F2}%");
            }
        }

        if (reasons.Count >= 3)
        {
            maxScore = Math.Min(100, maxScore + 25);
            reasons.Add("多均线密集");
            direction = "bear";
        }

        return new()
        {
            Key = "maPressure", Name = "均线压力",
            Score = (int)Math.Min(100, maxScore), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : (nearestMA != "" ? $"最近{nearestMA}" : "无明显压力")
        };
    }

    // ============ 因子5: K线形态 ============
    public FactorResult ExtractKlinePatternFactor(List<MarketSnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 3)
            return new() { Key = "klinePattern", Name = "K线形态", Score = 0, Direction = "neutral", Detail = "数据不足" };

        var recent5 = snapshots.TakeLast(5).ToList();
        var open = recent5[0].Price;
        var close = recent5[^1].Price;
        var high = recent5.Select(s => s.Price).Max();
        var low = recent5.Select(s => s.Price).Min();
        var range = high - low;

        if (range == 0)
            return new() { Key = "klinePattern", Name = "K线形态", Score = 0, Direction = "neutral", Detail = "无波动" };

        var upperShadow = high - Math.Max(open, close);
        var lowerShadow = Math.Min(open, close) - low;
        var body = Math.Abs(close - open);

        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        if (upperShadow > body * 2 && upperShadow > range * 0.5)
        { score = 55; direction = "bear"; reasons.Add("长上影线"); }
        else if (body < range * 0.3)
        { score = 35; direction = "bear"; reasons.Add("十字星"); }
        else if (close < open && body > range * 0.6)
        { score = 40; direction = "bear"; reasons.Add("大阴线"); }

        return new()
        {
            Key = "klinePattern", Name = "K线形态",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : "正常波动"
        };
    }

    // ============ 因子6: 分时形态 ============
    public FactorResult ExtractIntradayPatternFactor(List<DetectedSignal> detectedSignals)
    {
        var patternTypes = detectedSignals.Select(s => s.Type).ToHashSet();
        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        if (patternTypes.Contains("double_top")) { score += 50; direction = "bear"; reasons.Add("双顶形态"); }
        if (patternTypes.Contains("triple_top")) { score += 55; direction = "bear"; reasons.Add("三次上攻失败"); }
        if (patternTypes.Contains("fishing_line")) { score += 60; direction = "bear"; reasons.Add("钓鱼线"); }
        if (patternTypes.Contains("surge_pullback")) { score += 40; direction = "bear"; reasons.Add("冲高回落"); }
        if (patternTypes.Contains("top_divergence")) { score += 45; direction = "bear"; reasons.Add("顶背离"); }
        if (patternTypes.Contains("platform_breakdown")) { score += 50; direction = "bear"; reasons.Add("平台跌破"); }
        if (patternTypes.Contains("high_deviation_pullback")) { score += 42; direction = "bear"; reasons.Add("高乖离回落"); }

        return new()
        {
            Key = "intradayPattern", Name = "分时形态",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : "无明显形态"
        };
    }

    // ============ 因子7: 动量 ============
    public FactorResult ExtractMomentumFactor(List<MarketSnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 20)
            return new() { Key = "momentum", Name = "动量", Score = 0, Direction = "neutral", Detail = "数据不足" };

        var recent20 = snapshots.TakeLast(20).ToList();
        var upCount = 0;
        for (var i = 1; i < recent20.Count; i++)
            if (recent20[i].Price > recent20[i - 1].Price) upCount++;
        var upRatio = (double)upCount / (recent20.Count - 1) * 100;

        var first10 = recent20.Take(10).ToList();
        var last10 = recent20.Skip(10).ToList();
        var first10Change = first10.Count > 0 && first10[0].Price > 0
            ? (first10[^1].Price - first10[0].Price) / first10[0].Price * 100 : 0;
        var last10Change = last10.Count > 0 && last10[0].Price > 0
            ? (last10[^1].Price - last10[0].Price) / last10[0].Price * 100 : 0;
        var momentumDecay = first10Change - last10Change;

        var score = 0;
        var direction = "neutral";
        var reasons = new List<string>();

        if (upRatio > 75) { score += 40; direction = "bear"; reasons.Add($"上涨占比高({upRatio:F0})"); }
        else if (upRatio > 65) { score += 20; direction = "bear"; reasons.Add($"上涨占比偏高({upRatio:F0})"); }

        if (momentumDecay > 0.5 && last10Change < 0.3)
        { score += 35; direction = "bear"; reasons.Add("动量衰减"); }

        return new()
        {
            Key = "momentum", Name = "动量",
            Score = Math.Min(100, score), Direction = direction,
            Detail = reasons.Count > 0 ? string.Join(" + ", reasons) : $"上涨占比={upRatio:F0}"
        };
    }

    // ============ 因子8: 时间 ============
    public FactorResult ExtractTimeFactor(List<MarketSnapshot> snapshots)
    {
        var last = snapshots.Last();
        var tz = StockReview.Core.Services.CnTimeZone.Get;
        var nowSh = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var hour = nowSh.Hour;
        var minute = nowSh.Minute;
        var timeStr = $"{hour}:{minute:D2}";

        var score = 0;
        var direction = "neutral";
        var detail = timeStr;

        if (hour == 14 && minute >= 30) { score = 30; direction = "bear"; detail = $"尾盘{timeStr}"; }
        else if (hour == 9 && minute < 45) { score = 0; direction = "neutral"; detail = $"开盘保护期{timeStr}"; }
        else if (hour == 11 && minute >= 15) { score = 20; direction = "bear"; detail = $"上午尾盘{timeStr}"; }

        return new() { Key = "timeFactor", Name = "时间", Score = score, Direction = direction, Detail = detail };
    }

    // ============ 因子9: 资金流向 ============
    public FactorResult ExtractCapitalFlowFactor(CapitalFlowData capitalFlowData,
        List<MarketSnapshot> snapshots, double currentPrice)
    {
        if (capitalFlowData == null)
            return new() { Key = "capitalFlow", Name = "资金流向", Score = 0, Direction = "neutral", Detail = "无数据" };

        var main = capitalFlowData.MainInFlow;
        var lastSnap = snapshots.Last();
        var totalVol = lastSnap.CumulativeVolume;
        var avgPrice = lastSnap.Price > 0 ? lastSnap.Price : (currentPrice > 0 ? currentPrice : 1);
        var dayTurnover = totalVol * avgPrice;
        if (dayTurnover <= 0)
            dayTurnover = Math.Abs(main) + Math.Abs(capitalFlowData.SuperInFlow) + Math.Abs(capitalFlowData.BigInFlow);
        if (dayTurnover <= 0)
            return new() { Key = "capitalFlow", Name = "资金流向", Score = 0, Direction = "neutral", Detail = "无成交" };

        var mainRatio = main / dayTurnover;
        var priceChange = snapshots.Count >= 2 && snapshots[0].Price > 0
            ? ((currentPrice - snapshots[0].Price) / snapshots[0].Price) * 100 : 0;

        var score = 0;
        var direction = "neutral";
        var detail = $"主力净额{mainRatio * 100:F2}%";

        if (mainRatio <= -0.008)
        {
            score = (int)Math.Min(90, 60 + Math.Abs(mainRatio) * 1000);
            direction = "bear";
            detail = $"主力净流出{Math.Abs(mainRatio) * 100:F2}%";
        }
        else if (mainRatio >= 0.01)
        {
            if (priceChange > 0.3) { score = 45; direction = "bull"; detail = $"主力净流入{mainRatio * 100:F2}%"; }
            else { score = 35; direction = "bear"; detail = $"主力流入滞涨(占比{mainRatio * 100:F2}%)"; }
        }

        return new() { Key = "capitalFlow", Name = "资金流向", Score = Math.Min(100, score), Direction = direction, Detail = detail };
    }

    // ============ 权重管理 ============

    public void UpdateWeights(Dictionary<string, double> newWeights)
    {
        foreach (var (key, value) in newWeights)
        {
            if (_weights.ContainsKey(key)) _weights[key] = value;
        }
        // 归一化
        var sum = _weights.Values.Sum();
        if (sum > 0)
        {
            foreach (var key in _weights.Keys.ToList()) _weights[key] /= sum;
        }
        // Clamp [0.05, 0.40]
        foreach (var key in _weights.Keys.ToList())
        {
            _weights[key] = Math.Max(0.05, Math.Min(0.40, _weights[key]));
        }
        SaveWeights();
    }

    public Dictionary<string, double> GetWeights() => new(_weights);
    public Dictionary<string, double> GetDefaultWeights() => new(DefaultWeights);
}

// ============ 数据模型 ============

public class DailyKline
{
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public DateTime Date { get; set; }
}

public class DetectedSignal
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public int Score { get; set; }
}

public class CapitalFlowData
{
    public bool Available { get; set; }
    public double InFlow { get; set; }
    public double MainInFlow { get; set; }
    public double SuperInFlow { get; set; }
    public double BigInFlow { get; set; }
    public double MidInFlow { get; set; }
    public double SmlInFlow { get; set; }
}

public class MAValues
{
    public double? MA5 { get; set; }
    public double? MA10 { get; set; }
    public double? MA20 { get; set; }
    public double? MA30 { get; set; }
    public double? PreClose { get; set; }
}

public class FactorResult
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public string Direction { get; set; } = "neutral"; // bear | bull | neutral
    public string Detail { get; set; } = "";
}

public class MultiFactorResult
{
    public double TotalScore { get; set; }
    public List<FactorResult> Factors { get; set; } = new();
    public string Direction { get; set; } = "neutral";
    public double Confidence { get; set; }
    public int BearCount { get; set; }
    public int BullCount { get; set; }
    public int ResonanceBonus { get; set; }
    public string Detail { get; set; } = "";
    public Dictionary<string, double> Weights { get; set; } = new();
}
