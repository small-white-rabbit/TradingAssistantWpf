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

    // ==================== 分析入口 ====================

    /// <summary>
    /// 入口：分析快照，返回所有触发的卖点信号及评分
    /// 对应 JS analyze 方法
    /// </summary>
    public AnalyzeResult Analyze(
        TradingPlanInfo? plan,
        StockQuote? currentData,
        List<IntradaySnapshot> snapshots,
        List<KLineData>? dailyKlines = null,
        object? capitalFlow = null,
        CancellationToken cancellationToken = default)
    {
        var result = new AnalyzeResult();

        if (currentData == null || snapshots == null || snapshots.Count < 5)
            return result;

        var signals = new List<SellPointSignal>();
        var currentPrice = (double)currentData.CurrentPrice;
        var planId = plan?.Id ?? "default";

        // 排序：按时间正序（旧→新），并裁剪日内全量上限
        var sortedSnapshots = snapshots
            .OrderBy(s => s.SnapshotAt)
            .ToList();
        sortedSnapshots = NormalizeIntraday(sortedSnapshots);
        NormalizeVolumes(sortedSnapshots);
        RepairAvgPrice(sortedSnapshots);

        // 更新状态机
        var planState = UpdatePlanState(planId, sortedSnapshots, currentPrice);

        // 市场环境上下文
        var marketCtx = GetMarketContext(sortedSnapshots[^1]);
        if (marketCtx.IsDownLimit)
        {
            // 跌停附近：流动性危机，不触发任何卖点
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. 冲高回落
        var surge = DetectSurgePullback(sortedSnapshots, currentPrice);
        if (surge != null) signals.Add(surge.WithType(SignalTypes.SurgePullback));

        // 2. 放量滞涨
        var stagnant = DetectVolumeStagnant(sortedSnapshots, currentPrice);
        if (stagnant != null) signals.Add(stagnant.WithType(SignalTypes.VolumeStagnant));

        // 2b. 单根巨量做顶
        var spikeVolTop = DetectSpikeVolumeTop(sortedSnapshots, currentPrice, planState);
        if (spikeVolTop != null) signals.Add(spikeVolTop.WithType(SignalTypes.SpikeVolumeTop));

        // 3. 分时均线压制
        var maSuppress = DetectMASuppress(sortedSnapshots, currentPrice);
        if (maSuppress != null) signals.Add(maSuppress.WithType(SignalTypes.MaSuppress));

        // 4. 顶背离
        var topDiv = DetectTopDivergence(sortedSnapshots, currentPrice);
        if (topDiv != null) signals.Add(topDiv.WithType(SignalTypes.TopDivergence));

        // 5. 量价背离
        var volDiv = DetectVolumeDivergence(sortedSnapshots, currentPrice);
        if (volDiv != null) signals.Add(volDiv.WithType(SignalTypes.VolumeDivergence));

        // 6. 双顶形态
        var doubleTop = DetectDoubleTop(sortedSnapshots, currentPrice);
        if (doubleTop != null) signals.Add(doubleTop.WithType(SignalTypes.DoubleTop));

        // 7. 钓鱼线
        var fishingLine = DetectFishingLine(sortedSnapshots, currentPrice);
        if (fishingLine != null) signals.Add(fishingLine.WithType(SignalTypes.FishingLine));

        // 8. 关键位置跌破
        var keyBreak = DetectKeyLevelBreakdown(sortedSnapshots, currentPrice, dailyKlines);
        if (keyBreak != null) signals.Add(keyBreak);

        // 9. 三次上攻不创新高
        var tripleTop = DetectTripleTop(sortedSnapshots, currentPrice);
        if (tripleTop != null) signals.Add(tripleTop.WithType(SignalTypes.TripleTop));

        // 10. 跌破平台/箱体
        var platformBreak = DetectPlatformBreakdown(sortedSnapshots, currentPrice);
        if (platformBreak != null)
        {
            platformBreak.IsStopLoss = true;
            signals.Add(platformBreak.WithType(SignalTypes.PlatformBreakdown));
        }

        // 11. 高乖离回落
        var highDevPullback = DetectHighDeviationPullback(sortedSnapshots, currentPrice);
        if (highDevPullback != null) signals.Add(highDevPullback.WithType(SignalTypes.HighDeviationPullback));

        // 12. 跌破分时均价线
        var vwapBreakdown = DetectVWAPBreakdown(sortedSnapshots, currentPrice, planState);
        if (vwapBreakdown != null) signals.Add(vwapBreakdown.WithType(SignalTypes.VwapBreakdown));

        // 13. 均线挡道
        var vwapRejection = DetectVWAPRejection(sortedSnapshots, currentPrice);
        if (vwapRejection != null) signals.Add(vwapRejection.WithType(SignalTypes.VwapRejection));

        // 14. 均价线拐头向下
        var vwapSlopeDown = DetectVWAPSlopeDown(sortedSnapshots, currentPrice);
        if (vwapSlopeDown != null) signals.Add(vwapSlopeDown.WithType(SignalTypes.VwapSlopeDown));

        // 15. 尾盘资金出逃
        var lateSession = DetectLateSessionExit(sortedSnapshots, currentPrice);
        if (lateSession != null) signals.Add(lateSession.WithType(SignalTypes.LateSessionExit));

        // 16. 缩量均线反弹失败
        var weakRebound = DetectWeakReboundFailure(sortedSnapshots, currentPrice);
        if (weakRebound != null) signals.Add(weakRebound.WithType(SignalTypes.WeakReboundFailure));

        // 16b. 大跌反抽卖点
        var deepDropRebound = DetectDeepDropRebound(sortedSnapshots, currentPrice);
        if (deepDropRebound != null) signals.Add(deepDropRebound.WithType(SignalTypes.DeepDropRebound));

        // 17. ATR 止损止盈检测
        var atrStop = DetectATRStopLoss(sortedSnapshots, currentPrice, plan);
        if (atrStop != null) signals.Add(atrStop);

        cancellationToken.ThrowIfCancellationRequested();

        // ===== 多信号共振评分系统 =====
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var s in signals)
        {
            s.Weight = GetSignalWeight(s.Type);
            s.IsResonance = signals.Count >= 2;
            s.Timestamp = now;
        }

        // 多因子综合评分
        var multiFactorResult = new MultiFactorResult();
        if (_multiFactorEngine != null)
        {
            try
            {
                multiFactorResult = _multiFactorEngine.Evaluate(
                    sortedSnapshots, currentPrice, dailyKlines, signals, capitalFlow);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[卖点检测] 多因子引擎评估失败，降级为纯信号评分");
            }
        }

        var scoreResult = EvaluateSignals(
            signals, sortedSnapshots, currentPrice, dailyKlines, marketCtx,
            multiFactorResult.TotalScore);

        // F1 修复：用过滤后的信号集替换
        if (scoreResult.FilteredSignals.Count > 0 || scoreResult.FilteredSignals != null)
        {
            signals = scoreResult.FilteredSignals;
            foreach (var s in signals) s.IsResonance = signals.Count >= 2;
        }

        var factorScore = multiFactorResult.TotalScore;

        // 多因子高分但无信号 → 注入虚拟信号
        var virtualInjected = false;
        if (signals.Count == 0 && factorScore >= 60 && multiFactorResult.BearCount >= 3)
        {
            signals.Add(new SellPointSignal
            {
                Type = SignalTypes.MultifactorResonance,
                LevelName = "多因子共振",
                CurrentPrice = currentPrice,
                IsResonance = true,
                Weight = 3,
                Timestamp = now
            });
            virtualInjected = true;
        }

        // 因子上下文挂到每个信号
        foreach (var s in signals)
        {
            s.Set("mfFactors", multiFactorResult.Factors);
            s.Set("mfWeights", multiFactorResult.Weights);
            s.Set("mfScore", multiFactorResult.TotalScore);
        }

        result.Signals = signals;
        result.TotalScore = virtualInjected
            ? Math.Max(scoreResult.TotalScore, (int)JsMath.JsRound(multiFactorResult.TotalScore))
            : scoreResult.TotalScore;
        result.Priority = scoreResult.Priority;
        result.PriorityName = scoreResult.PriorityName;
        result.HasStopLossSignal = scoreResult.HasStopLossSignal;
        result.HoldFilter = scoreResult.HoldFilter;
        result.SignalScore = scoreResult.SignalScore;
        result.Composition = scoreResult.Composition;
        result.ScoreMods = scoreResult.ScoreMods;
        result.ScoreBonus = scoreResult.Bonus;
        result.MultiFactorScore = multiFactorResult.TotalScore;
        result.MultiFactorDetail = multiFactorResult.Detail;

        // VWAP_SLOPE_DOWN 单独时不提醒
        var slopeDownCount = signals.Count(s => s.Type == SignalTypes.VwapSlopeDown);
        if (slopeDownCount == signals.Count || scoreResult.TotalScore == 0)
        {
            signals.RemoveAll(s => s.Type == SignalTypes.VwapSlopeDown);
            result.Signals = signals;
        }

        return result;
    }

    // ==================== 检测方法 ====================
    /// <summary>
    /// 1. 冲高回落检测
    /// </summary>
    public SellPointSignal? DetectSurgePullback(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 6) return null;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();
        var total = prices.Count;
        var basePriceGlobal = snapshots[^1].PreClose > 0
            ? snapshots[^1].PreClose
            : (snapshots[0].PreClose > 0 ? snapshots[0].PreClose : snapshots[0].Price);

        // 位置过滤
        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }

        // 趋势过滤
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var scanStart = Math.Max(2, total - 25);
        var bestPeakIdx = -1;
        var bestBasePrice = 0.0;
        var bestSurgeAbs = 0.0;
        var fastSpan = _config.SurgeFastSpan;
        var fastMinRisePct = _config.SurgeFastMinRisePct;
        var fastPullbackRatio = Math.Max(_config.PullbackRatio, 0.5);

        for (var p = total - 2; p >= scanStart; p--)
        {
            if (prices[p] <= prices[p + 1]) continue;

            var upStart = Math.Max(0, p - 12);
            var upLegPrices = prices.GetRange(upStart, p + 1 - upStart);
            var upLegLow = upLegPrices.Min();
            var surgeAbs = prices[p] - upLegLow;
            if (surgeAbs <= 0) continue;

            var basePrice = snapshots[upStart].PreClose > 0
                ? snapshots[upStart].PreClose
                : basePriceGlobal;
            if (basePrice <= 0) continue;
            var surgePct = surgeAbs / basePrice * 100;

            var fastPass = false;
            if (p + 1 >= fastSpan)
            {
                var fastSlice = prices.GetRange(Math.Max(0, p + 1 - fastSpan), fastSpan);
                var fastLow = fastSlice.Min();
                var fastRise = (prices[p] - fastLow) / (basePrice > 0 ? basePrice : fastLow) * 100;
                if (fastRise >= fastMinRisePct) fastPass = true;
            }

            if (surgePct < _config.SurgePullbackThreshold && !fastPass) continue;

            var downLegPrices = prices.GetRange(p + 1, total - p - 1);
            if (downLegPrices.Count < 2) continue;
            var downLegLow = downLegPrices.Min();
            var finalPrice = currentPrice > 0 ? currentPrice : downLegPrices[^1];
            var actualTrough = Math.Min(downLegLow, finalPrice);
            var pullbackAbs = prices[p] - actualTrough;
            if (pullbackAbs <= 0) continue;
            var pullbackRatio = pullbackAbs / surgeAbs;
            var minPullback = fastPass ? fastPullbackRatio : _config.PullbackRatio;
            if (pullbackRatio < minPullback) continue;

            bestPeakIdx = p;
            bestBasePrice = basePrice;
            bestSurgeAbs = surgeAbs;
            break;
        }

        // 秒级通道
        var intraBarHigh = 0.0;
        var isIntraBar = false;
        if (bestPeakIdx < 0)
        {
            var fHigh = snapshots[total - 1].High;
            var prevClose = prices[total - 2];
            if (double.IsFinite(fHigh) && fHigh > 0
                && double.IsFinite(prevClose) && fHigh > prevClose && fHigh > currentPrice)
            {
                var upStart = Math.Max(0, total - 1 - 12);
                var upLegPrices = prices.GetRange(upStart, total - 1 - upStart);
                var upLegLow = upLegPrices.Count > 0 ? upLegPrices.Min() : prevClose;
                var surgeAbs = fHigh - upLegLow;
                var basePrice = snapshots[upStart].PreClose > 0
                    ? snapshots[upStart].PreClose
                    : basePriceGlobal;
                if (surgeAbs > 0 && basePrice > 0)
                {
                    var surgePct = surgeAbs / basePrice * 100;
                    var fastSlice = prices.GetRange(Math.Max(0, total - 1 - fastSpan), total - 1 - Math.Max(0, total - 1 - fastSpan));
                    var fastLow = fastSlice.Count > 0 ? fastSlice.Min() : prevClose;
                    var fastRise = (fHigh - fastLow) / (basePrice > 0 ? basePrice : fastLow) * 100;
                    var fastPass = fastRise >= fastMinRisePct;
                    var pullbackAbs = fHigh - currentPrice;
                    var pullbackRatio = pullbackAbs / surgeAbs;
                    var minPullback = fastPass ? fastPullbackRatio : _config.PullbackRatio;
                    if ((surgePct >= _config.SurgePullbackThreshold || fastPass)
                        && pullbackRatio >= minPullback)
                    {
                        bestPeakIdx = total - 1;
                        bestBasePrice = basePrice;
                        bestSurgeAbs = surgeAbs;
                        intraBarHigh = fHigh;
                        isIntraBar = true;
                    }
                }
            }
        }

        if (bestPeakIdx < 0) return null;

        var peakPrice = isIntraBar ? intraBarHigh : prices[bestPeakIdx];
        var downLegFinal = prices.GetRange(bestPeakIdx + 1, total - bestPeakIdx - 1);
        var downLegLowFinal = downLegFinal.Count > 0 ? downLegFinal.Min() : currentPrice;
        var finalPriceFinal = currentPrice > 0 ? currentPrice : (downLegFinal.Count > 0 ? downLegFinal[^1] : currentPrice);
        var actualTroughFinal = Math.Min(downLegLowFinal, finalPriceFinal);
        var pullbackAbsFinal = peakPrice - actualTroughFinal;
        var pullbackRatioFinal = pullbackAbsFinal / bestSurgeAbs;

        var isVolumeAmplified = CheckVolumeAmplified(snapshots);
        var currentChangePct = (currentPrice - bestBasePrice) / bestBasePrice * 100;
        var peakChangePct = (peakPrice - bestBasePrice) / bestBasePrice * 100;

        var signal = new SellPointSignal
        {
            LevelName = "冲高回落",
            LevelPrice = peakPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = isVolumeAmplified
        };
        signal.Set("peakPrice", peakPrice);
        signal.Set("peakChangePct", peakChangePct);
        signal.Set("surgeAbs", bestSurgeAbs);
        signal.Set("pullbackAbs", pullbackAbsFinal);
        signal.Set("pullbackRatio", pullbackRatioFinal * 100);
        signal.Set("currentChangePct", currentChangePct);
        signal.Set("intraBar", isIntraBar);

        // 形态相似度过滤
        if (_config.EnablePatternSimilarity && _patternSimilarity != null)
        {
            var patternStart = Math.Max(0, bestPeakIdx - 10);
            List<double> candidatePrices;
            List<double> candidateVolumes;
            int peakPoint;

            if (isIntraBar)
            {
                candidatePrices = prices.GetRange(patternStart, total - 1 - patternStart);
                candidatePrices.Add(intraBarHigh);
                candidatePrices.Add(currentPrice);
                candidateVolumes = volumes.GetRange(patternStart, total - 1 - patternStart);
                candidateVolumes.Add(volumes[total - 1]);
                candidateVolumes.Add(0);
                peakPoint = candidatePrices.Count - 2;
            }
            else
            {
                candidatePrices = prices.GetRange(patternStart, total - patternStart);
                candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                peakPoint = bestPeakIdx - patternStart;
            }

            var keyPoints = new Dictionary<string, int>
            {
                ["base"] = isIntraBar ? Math.Max(0, peakPoint - 3) : Math.Max(0, bestPeakIdx - 3) - patternStart,
                ["peak"] = peakPoint,
                ["pullback"] = candidatePrices.Count - 1
            };

            var (similarity, details) = _patternSimilarity.CalculateSimilarity(
                candidatePrices, "surge_pullback", keyPoints, candidateVolumes);
            if (similarity < _config.SurgePullbackSimilarityMin) return null;

            signal.Set("similarity", similarity);
            signal.Set("similarityDetails", details);
        }

        return signal;
    }

    /// <summary>
    /// 2. 放量滞涨检测
    /// </summary>
    public SellPointSignal? DetectVolumeStagnant(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 6) return null;

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var current = snapshots[^1];
        var previous = snapshots.Skip(snapshots.Count - 6).Take(5).ToList();
        var avgVolume = previous.Sum(s => GetIntervalVolume(s)) / previous.Count;
        var currentVol = GetIntervalVolume(current);

        if (currentVol == 0 || avgVolume == 0) return null;
        if (currentVol < avgVolume * _config.VolumeAmplifyMultiple) return null;

        var recentWindow = Math.Min(10, snapshots.Count - 1);
        var recentStart = snapshots[snapshots.Count - 1 - recentWindow];
        var recentBasePrice = recentStart.Price > 0 ? recentStart.Price : currentPrice;
        if (recentBasePrice <= 0) return null;
        var recentChangePct = (currentPrice - recentBasePrice) / recentBasePrice * 100;
        if (recentChangePct < -0.5 || recentChangePct >= _config.StagnantThreshold) return null;

        // 慢牛中继过滤
        if (snapshots.Count >= 30)
        {
            var midWindow = Math.Min(30, snapshots.Count - 1);
            var midStart = snapshots[snapshots.Count - 1 - midWindow];
            if (midStart.Price > 0)
            {
                var midChangePct = (currentPrice - midStart.Price) / midStart.Price * 100;
                if (midChangePct >= 1.5) return null;
            }
        }
        if (snapshots.Count >= 60)
        {
            var longWindow = Math.Min(60, snapshots.Count - 1);
            var longStart = snapshots[snapshots.Count - 1 - longWindow];
            if (longStart.Price > 0)
            {
                var longChangePct = (currentPrice - longStart.Price) / longStart.Price * 100;
                if (longChangePct >= 2.0) return null;
            }
        }

        var avgPrice = current.AvgPrice;
        if (avgPrice <= 0) return null;
        if (currentPrice <= avgPrice) return null;

        var distancePct = (currentPrice - avgPrice) / avgPrice * 100;
        if (distancePct < _config.AvgPriceDistancePct) return null;

        var signal = new SellPointSignal
        {
            LevelName = "放量滞涨",
            LevelPrice = currentPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = true
        };
        signal.Set("currentVolume", currentVol);
        signal.Set("avgVolume", avgVolume);
        signal.Set("volumeMultiple", currentVol / avgVolume);
        signal.Set("changePct", recentChangePct);
        signal.Set("avgPrice", avgPrice);
        signal.Set("distancePct", distancePct);
        return signal;
    }

    /// <summary>
    /// 2b. 单根巨量做顶检测
    /// </summary>
    public SellPointSignal? DetectSpikeVolumeTop(List<IntradaySnapshot> snapshots, double currentPrice, PlanState planState)
    {
        if (snapshots.Count < 12) return null;

        var current = snapshots[^1];
        var currentVol = GetIntervalVolume(current);

        var lookback = Math.Min(6, snapshots.Count - 1);
        var prevVols = new List<double>();
        for (var i = snapshots.Count - 1 - lookback; i < snapshots.Count - 1; i++)
            prevVols.Add(GetIntervalVolume(snapshots[i]));
        var prevAvgVol = prevVols.Sum() / prevVols.Count;

        if (currentVol == 0 || prevAvgVol == 0) return null;
        var volMultiple = currentVol / prevAvgVol;
        if (volMultiple < _config.SpikeVolumeMultiple) return null;

        // 前期量能变异系数
        var prevMean = prevAvgVol;
        var prevVariance = prevVols.Sum(v => Math.Pow(v - prevMean, 2)) / prevVols.Count;
        var prevStd = Math.Sqrt(prevVariance);
        var prevCv = prevMean > 0 ? prevStd / prevMean : 0;
        if (prevCv > _config.SpikeVolumePrevCvMax) return null;

        // 日内高位
        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.SpikeVolumeMinPosition) return null;
        }

        // 拉升状态
        var surgeLookback = Math.Min(_config.SpikeVolumeSurgeLookback, snapshots.Count - 1);
        var windowLow = snapshots[snapshots.Count - 1 - surgeLookback].Price;
        for (var i = snapshots.Count - 1 - surgeLookback; i < snapshots.Count; i++)
        {
            if (snapshots[i].Price < windowLow) windowLow = snapshots[i].Price;
        }
        if (windowLow <= 0) return null;
        var surgeRisePct = (currentPrice - windowLow) / windowLow * 100;
        if (surgeRisePct < _config.SpikeVolumeSurgeMinRise) return null;

        // 冷却
        var currentIdx = snapshots.Count - 1;
        if (currentIdx - planState.SpikeVolCooldownIdx < _config.SpikeVolumeCooldownBars) return null;
        planState.SpikeVolCooldownIdx = currentIdx;

        var signal = new SellPointSignal
        {
            LevelName = "单根巨量做顶",
            LevelPrice = currentPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = true
        };
        signal.Set("currentVolume", currentVol);
        signal.Set("avgVolume", prevAvgVol);
        signal.Set("volumeMultiple", volMultiple);
        signal.Set("prevCv", prevCv);
        signal.Set("surgeRisePct", surgeRisePct);
        signal.Set("currentPosition", dayRange > 0 ? (currentPrice - ctx.DayLow) / dayRange : 0);
        return signal;
    }

    /// <summary>
    /// 3. 分时均线压制
    /// </summary>
    public SellPointSignal? DetectMASuppress(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10) return null;

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var total = snapshots.Count;
        var windowSize = Math.Min(total, 20);
        var candles = snapshots.Skip(total - windowSize).ToList();
        var current = candles[^1];
        var maValue = current.AvgPrice;
        if (maValue <= 0) return null;
        if (currentPrice >= maValue) return null;

        var currentGapPct = (maValue - currentPrice) / maValue * 100;
        if (currentGapPct < 0.1 || currentGapPct > 2) return null;

        // 找最近一波拉升
        var recent10 = candles.Skip(candles.Count - 10).ToList();
        var surgeStartIdx = -1;
        var surgePeakIdx = -1;
        var surgeGainPct = 0.0;

        for (var i = 0; i < recent10.Count - 2; i++)
        {
            var maxGain = 0.0;
            var peakIdx = i;
            for (var j = i + 1; j < recent10.Count; j++)
            {
                var gain = (recent10[j].Price - recent10[i].Price) / recent10[i].Price * 100;
                if (gain > maxGain) { maxGain = gain; peakIdx = j; }
            }
            if (maxGain >= 1.5 && peakIdx > i + 1)
            {
                surgeStartIdx = i;
                surgePeakIdx = peakIdx;
                surgeGainPct = maxGain;
                break;
            }
        }
        if (surgeStartIdx < 0) return null;

        var peakPrice = recent10[surgePeakIdx].Price;
        var peakAvgPrice = recent10[surgePeakIdx].AvgPrice > 0 ? recent10[surgePeakIdx].AvgPrice : maValue;
        if (peakAvgPrice <= 0) return null;
        if (peakPrice > peakAvgPrice * 1.002) return null;

        var peakGapPct = (peakAvgPrice - peakPrice) / peakAvgPrice * 100;
        if (peakGapPct < 0 || peakGapPct > 1) return null;

        // 缩量判断
        var surgeStart = Math.Max(0, surgeStartIdx - 3);
        var preSurge = candles.GetRange(surgeStart, surgeStartIdx + 1 - surgeStart);
        var surgeSeg = candles.GetRange(surgeStartIdx, surgePeakIdx + 1 - surgeStartIdx);
        if (preSurge.Count < 3 || surgeSeg.Count == 0) return null;

        var preAvgVol = preSurge.Sum(s => GetIntervalVolume(s)) / preSurge.Count;
        var surgeAvgVol = surgeSeg.Sum(s => GetIntervalVolume(s)) / surgeSeg.Count;
        if (preAvgVol == 0) return null;

        var volumeRatio = surgeAvgVol / preAvgVol;
        if (volumeRatio > 0.8) return null;

        var signal = new SellPointSignal
        {
            LevelName = "分时均线压制",
            LevelPrice = maValue,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = false
        };
        signal.Set("surgeGainPct", surgeGainPct);
        signal.Set("volumeRatio", volumeRatio);
        signal.Set("peakGapPct", peakGapPct);
        signal.Set("currentGapPct", currentGapPct);
        return signal;
    }

    /// <summary>
    /// 4. 顶背离检测（基于均价线偏离度 + 成交量）
    /// </summary>
    public SellPointSignal? DetectTopDivergence(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 20) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var avgPrices = snapshots.Select(s => s.AvgPrice).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();
        var currentPriceUsed = currentPrice > 0 ? currentPrice : prices[^1];

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPriceUsed - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var allPeaks = FindPeaksRobust(prices, 2, _config.TopDivergenceMinRelHeight)
            .Where(p => p.Index >= 5 && p.Index < total - 2)
            .ToList();
        if (allPeaks.Count < 2) return null;

        var p2 = allPeaks[^1];
        var p1 = allPeaks[^2];
        if (p2.Index - p1.Index < 5) return null;
        if (p2.Price <= p1.Price * (1 + _config.TopDivergenceNewHighPct / 100)) return null;

        // 前置涨幅
        var preP1Prices = prices.GetRange(Math.Max(0, p1.Index - 20), p1.Index + 1 - Math.Max(0, p1.Index - 20));
        var preP1Low = preP1Prices.Min();
        var preRisePct = preP1Low > 0 ? (p1.Price - preP1Low) / preP1Low * 100 : 0;
        if (preRisePct < _config.TopPatternMinPreRisePct) return null;

        var avg1 = avgPrices[p1.Index];
        var avg2 = avgPrices[p2.Index];
        var dev1 = avg1 > 0 ? (p1.Price - avg1) / avg1 * 100 : 0;
        var dev2 = avg2 > 0 ? (p2.Price - avg2) / avg2 * 100 : 0;

        var vol1 = GetIntervalVolume(snapshots[p1.Index]);
        var vol2 = GetIntervalVolume(snapshots[p2.Index]);

        var isDeviationShrink = dev1 > 0 && dev2 < dev1 * _config.TopDivergenceDevShrinkRatio;
        if (!isDeviationShrink) return null;

        var isVolumeShrink = vol1 > 0 && vol2 < vol1 * _config.TopDivergenceVolShrinkRatio;
        var resonance = CheckOverboughtResonance(snapshots);

        var signal = new SellPointSignal
        {
            LevelName = "顶背离",
            CurrentPrice = currentPriceUsed,
            IsVolumeAmplified = false
        };
        signal.Set("firstHigh", p1.Price);
        signal.Set("secondHigh", p2.Price);
        signal.Set("firstDeviation", dev1);
        signal.Set("secondDeviation", dev2);
        signal.Set("volumeShrink", isVolumeShrink);
        signal.Set("techResonance", resonance);

        if (_config.EnablePatternSimilarity && _patternSimilarity != null)
        {
            var patternStart = Math.Max(0, p1.Index - 5);
            var candidatePrices = prices.GetRange(patternStart, total - patternStart);
            var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
            var keyPoints = new Dictionary<string, int>
            {
                ["peak1"] = p1.Index - patternStart,
                ["trough"] = (int)Math.Floor((p1.Index + p2.Index) / 2.0) - patternStart,
                ["peak2"] = p2.Index - patternStart,
                ["current"] = total - 1 - patternStart
            };
            var (similarity, details) = _patternSimilarity.CalculateSimilarity(
                candidatePrices, "top_divergence", keyPoints, candidateVolumes);
            if (similarity < _config.TopDivergenceSimilarityMin) return null;
            signal.Set("similarity", similarity);
            signal.Set("similarityDetails", details);
        }

        return signal;
    }

    /// <summary>
    /// 5. 量价背离检测
    /// </summary>
    public SellPointSignal? DetectVolumeDivergence(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10) return null;
        var total = snapshots.Count;

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0 && currentPrice > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var maxScan = Math.Min(total, 20);
        for (var segLen = Math.Min(maxScan, 14); segLen >= 10; segLen -= 2)
        {
            var segStart = total - segLen;
            if (segStart < 0) continue;
            var seg = snapshots.GetRange(segStart, segLen);
            var halfIdx = seg.Count / 2;
            if (halfIdx < 5) continue;

            var firstHalf = seg.GetRange(0, halfIdx);
            var secondHalf = seg.GetRange(halfIdx, seg.Count - halfIdx);
            var firstAvgPrice = firstHalf.Sum(s => s.Price) / firstHalf.Count;
            var secondAvgPrice = secondHalf.Sum(s => s.Price) / secondHalf.Count;
            if (secondAvgPrice <= firstAvgPrice * 1.003) continue;

            var current = seg[^1];
            var avgPrice = current.AvgPrice;
            if (avgPrice > 0)
            {
                var deviationFromAvg = (current.Price - avgPrice) / avgPrice * 100;
                if (deviationFromAvg < -1.5) continue;
            }

            var firstAvgVolume = firstHalf.Sum(s => GetIntervalVolume(s)) / firstHalf.Count;
            var secondAvgVolume = secondHalf.Sum(s => GetIntervalVolume(s)) / secondHalf.Count;
            if (firstAvgVolume == 0) continue;
            if (secondAvgVolume >= firstAvgVolume) continue;

            var shrinkRatio = 1 - secondAvgVolume / firstAvgVolume;
            if (shrinkRatio < _config.VolumeDivergenceShrinkRatio) continue;

            var signal = new SellPointSignal
            {
                LevelName = "量价背离",
                CurrentPrice = secondHalf[^1].Price,
                IsVolumeAmplified = false
            };
            signal.Set("priceTrend", (secondAvgPrice - firstAvgPrice) / firstAvgPrice * 100);
            signal.Set("volumeShrink", shrinkRatio);
            return signal;
        }
        return null;
    }

    /// <summary>
    /// 6. 关键位置跌破（MA5/MA10/MA30/动态支撑位）
    /// </summary>
    public SellPointSignal? DetectKeyLevelBreakdown(List<IntradaySnapshot> snapshots, double currentPrice, List<KLineData>? dailyKlines)
    {
        if (snapshots.Count < 30) return null;

        var useDailyMA = dailyKlines != null && dailyKlines.Count >= 5;
        if (!useDailyMA && !_hasWarnedNoDailyKline)
        {
            Log.Warning("[卖点检测] 日K线数据不足({Count})，降级为日内快照均价",
                dailyKlines?.Count ?? 0);
            _hasWarnedNoDailyKline = true;
        }

        var ma5 = useDailyMA ? CalculateDailyMA(dailyKlines!, 5) : CalculateMA(snapshots, 5);
        var ma10 = useDailyMA ? CalculateDailyMA(dailyKlines!, 10) : CalculateMA(snapshots, 10);
        var ma30 = useDailyMA ? CalculateDailyMA(dailyKlines!, 30) : CalculateMA(snapshots, 30);
        var prevPrice = snapshots[^2].Price;

        PrevDayData? prevDayData = null;
        if (dailyKlines != null && dailyKlines.Count > 0)
        {
            var lastKline = dailyKlines[^1];
            prevDayData = new PrevDayData
            {
                High = (double)lastKline.High,
                Low = (double)lastKline.Low,
                Close = (double)lastKline.Close
            };
        }

        // 跌破 MA5
        if (ma5.HasValue && ma5.Value > 0 && prevPrice >= ma5.Value && currentPrice < ma5.Value)
        {
            return CreateBreakSignal(SignalTypes.BreakMa5, "5日均价", ma5.Value, currentPrice, prevPrice, snapshots);
        }
        // 跌破 MA10
        if (ma10.HasValue && ma10.Value > 0 && prevPrice >= ma10.Value && currentPrice < ma10.Value)
        {
            return CreateBreakSignal(SignalTypes.BreakMa10, "10日均价", ma10.Value, currentPrice, prevPrice, snapshots);
        }
        // 跌破 MA30
        if (ma30.HasValue && ma30.Value > 0 && prevPrice >= ma30.Value && currentPrice < ma30.Value)
        {
            return CreateBreakSignal(SignalTypes.BreakMa30, "30日均价", ma30.Value, currentPrice, prevPrice, snapshots);
        }

        // 动态支撑位
        var maValues = new List<double>();
        if (ma5.HasValue && ma5.Value > 0) maValues.Add(ma5.Value);
        if (ma10.HasValue && ma10.Value > 0) maValues.Add(ma10.Value);
        if (ma30.HasValue && ma30.Value > 0) maValues.Add(ma30.Value);
        var support = CalculateDynamicSupport(snapshots, prevDayData);
        if (support.HasValue && support.Value > 0 && prevPrice >= support.Value && currentPrice < support.Value)
        {
            var maOverlap = maValues.Any(m => Math.Abs(m - support.Value) / m < 0.003);
            if (!maOverlap)
            {
                var breakdownPct = (support.Value - currentPrice) / support.Value * 100;
                if (breakdownPct <= _config.SupportBreakdownTolerance
                    && breakdownPct >= _config.SupportBreakdownMinPct)
                {
                    var signal = new SellPointSignal
                    {
                        Type = SignalTypes.BreakSupport,
                        LevelName = "动态支撑位",
                        LevelPrice = support.Value,
                        CurrentPrice = currentPrice,
                        IsStopLoss = true,
                        IsVolumeAmplified = CheckVolumeAmplified(snapshots)
                    };
                    signal.Set("prevPrice", prevPrice);
                    signal.Set("breakdownPct", breakdownPct);
                    return signal;
                }
            }
        }
        return null;
    }


    private SellPointSignal CreateBreakSignal(string type, string levelName, double levelPrice,
        double currentPrice, double prevPrice, List<IntradaySnapshot> snapshots)
    {
        var signal = new SellPointSignal
        {
            Type = type,
            LevelName = levelName,
            LevelPrice = levelPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = CheckVolumeAmplified(snapshots)
        };
        signal.Set("prevPrice", prevPrice);
        signal.Set("breakdownPct", (levelPrice - currentPrice) / levelPrice * 100);
        return signal;
    }

    /// <summary>
    /// 7. 双顶形态检测（含提前预警通道）
    /// </summary>
    public SellPointSignal? DetectDoubleTop(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var maxLookback = Math.Min(total, 80);
        var searchEnd = total - 1;
        var searchStart = Math.Max(2, total - maxLookback);

        var allPeaks = FindPeaksRobust(prices, 2, _config.DoubleTopMinProminence);
        var peaks = allPeaks
            .Where(pk => pk.Index >= Math.Max(1, searchStart) && pk.Index < searchEnd)
            .Select(pk => new { index = pk.Index, price = pk.Price, volume = volumes[pk.Index] })
            .ToList();
        if (peaks.Count < 2) return null;

        for (var j = peaks.Count - 1; j >= 1; j--)
        {
            var right = peaks[j];
            if (total - right.index > 40) break;

            for (var i = j - 1; i >= 0; i--)
            {
                var left = peaks[i];
                if (right.index - left.index < 5) continue;
                if (right.index - left.index > 60) break;

                var maxRightPrice = left.price * (1 + _config.DoubleTopRightMaxExceedPct / 100);
                if (right.price > maxRightPrice) continue;
                var heightDiffPct = Math.Abs(right.price - left.price) / left.price * 100;
                if (heightDiffPct > _config.DoubleTopTolerance) continue;
                if (right.price < left.price * 0.99) continue;

                // V型反弹检查
                var preWindow = _config.DoubleTopPreWindow;
                var preLeftPrices = prices.GetRange(Math.Max(0, left.index - preWindow), left.index + 1 - Math.Max(0, left.index - preWindow));
                if (preLeftPrices.Count < 10) continue;
                var preStart = preLeftPrices[0];
                var preMin = preLeftPrices.Min();
                if (preStart > 0)
                {
                    var preDropFromStart = (preStart - preMin) / preStart * 100;
                    var reboundFromStart = (left.price - preStart) / preStart * 100;
                    if (preDropFromStart > _config.DoubleTopPreVTrendMaxDrop
                        && reboundFromStart < _config.DoubleTopPreVTrendMaxDrop) continue;
                }
                var preRisePct = preMin > 0 ? (left.price - preMin) / preMin * 100 : 0;
                if (preRisePct < _config.TopPatternMinPreRisePct) continue;

                // 左顶后下跌
                var leftDropBars = _config.DoubleTopLeftDropAfterBars;
                var afterLeftEnd = Math.Min(total, left.index + leftDropBars + 1);
                var afterLeftPrices = prices.GetRange(left.index, afterLeftEnd - left.index);
                if (afterLeftPrices.Count < 2) continue;
                var afterLeftLow = afterLeftPrices.Min();
                var leftDropPct = (left.price - afterLeftLow) / left.price * 100;
                if (leftDropPct < _config.DoubleTopLeftDropAfterMin) continue;

                // 颈线
                var troughPrices = prices.GetRange(left.index, right.index + 1 - left.index);
                var troughPrice = troughPrices.Min();
                var neckToTopPct = (left.price - troughPrice) / left.price * 100;
                var minNeckDepth = GetMinNeckDepth(left.price);
                if (neckToTopPct < minNeckDepth) continue;

                // 成交量验证（波段口径）
                var (leftUpLegVol, rightUpLegVol) = CalculateLegVolumes(
                    prices, volumes, left.index, right.index);
                var volumeShrink = leftUpLegVol > 0 && rightUpLegVol > 0 && rightUpLegVol < leftUpLegVol * 0.8;
                var rightLeftVolumeRatio = leftUpLegVol > 0 ? (double?)rightUpLegVol / leftUpLegVol : null;
                if (leftUpLegVol > 0 && rightUpLegVol > leftUpLegVol * 1.2) continue;

                // 跌破颈线
                if (currentPrice >= troughPrice * 1.003) continue;

                var dropFromRight = (right.price - currentPrice) / right.price * 100;
                if (dropFromRight < _config.DoubleTopRightDropMin) continue;

                var signal = new SellPointSignal
                {
                    LevelName = "双顶形态",
                    LevelPrice = troughPrice,
                    CurrentPrice = currentPrice,
                    IsVolumeAmplified = false
                };
                signal.Set("leftPeak", left.price);
                signal.Set("rightPeak", right.price);
                signal.Set("troughPrice", troughPrice);
                signal.Set("neckToTopPct", neckToTopPct);
                signal.Set("dropFromRight", dropFromRight);
                signal.Set("leftDropPct", leftDropPct);
                signal.Set("volumeShrink", volumeShrink);
                signal.Set("rightLeftVolumeRatio", rightLeftVolumeRatio);

                if (_config.EnablePatternSimilarity && _patternSimilarity != null)
                {
                    var patternStart = Math.Max(0, left.index - 5);
                    var candidatePrices = prices.GetRange(patternStart, total - patternStart);
                    var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                    var keyPoints = new Dictionary<string, int>
                    {
                        ["leftPeak"] = left.index - patternStart,
                        ["neck"] = (int)Math.Floor((left.index + right.index) / 2.0) - patternStart,
                        ["rightPeak"] = right.index - patternStart,
                        ["breakdown"] = total - 1 - patternStart
                    };
                    var (sim, det) = _patternSimilarity.CalculateSimilarity(
                        candidatePrices, "double_top", keyPoints, candidateVolumes);
                    if (sim < _config.DoubleTopSimilarityMin) continue;
                    signal.Set("similarity", sim);
                    signal.Set("similarityDetails", det);
                }

                return signal;
            }
        }

        // 提前预警通道
        return DetectDoubleTopEarly(snapshots, prices, volumes, currentPrice, total, allPeaks);
    }

    /// <summary>
    /// 双顶提前预警：右顶缩量冲击前高失败
    /// </summary>
    private SellPointSignal? DetectDoubleTopEarly(
        List<IntradaySnapshot> snapshots, List<double> prices, List<double> volumes,
        double currentPrice, int total, List<PeakInfo> allPeaks)
    {
        if (!_config.EnableDoubleTopEarly) return null;

        var highs = snapshots.Select(s =>
        {
            var h = s.High;
            return double.IsFinite(h) && h > 0 ? h : s.Price;
        }).ToList();

        var candidates = allPeaks.Where(pk => pk.Index >= total - 60).ToList();
        for (var j = candidates.Count - 1; j >= 0; j--)
        {
            var left = candidates[j];
            var troughIdx = left.Index;
            for (var k = left.Index + 1; k < total; k++)
            {
                if (prices[k] < prices[troughIdx]) troughIdx = k;
            }
            if (troughIdx == total - 1) continue;
            var troughPrice = prices[troughIdx];

            var minNeckDepth = GetMinNeckDepth(left.Price);
            var neckToTopPct = (left.Price - troughPrice) / left.Price * 100;
            if (neckToTopPct < minNeckDepth) continue;

            // V型反弹检查
            var preWindow = _config.DoubleTopPreWindow;
            var preLeftPrices = prices.GetRange(Math.Max(0, left.Index - preWindow), left.Index + 1 - Math.Max(0, left.Index - preWindow));
            if (preLeftPrices.Count >= 10 && preLeftPrices[0] > 0)
            {
                var preStart = preLeftPrices[0];
                var preMin = preLeftPrices.Min();
                var preDropFromStart = (preStart - preMin) / preStart * 100;
                var reboundFromStart = (left.Price - preStart) / preStart * 100;
                if (preDropFromStart > _config.DoubleTopPreVTrendMaxDrop
                    && reboundFromStart < _config.DoubleTopPreVTrendMaxDrop) continue;
            }

            // 反弹段高点（区间高点口径）
            var reboundHighIdx = troughIdx;
            var reboundHigh = prices[troughIdx];
            for (var k = troughIdx + 1; k < total; k++)
            {
                if (highs[k] > reboundHigh) { reboundHigh = highs[k]; reboundHighIdx = k; }
            }

            var leftHigh = Math.Max(left.Price, highs[left.Index]);
            if (reboundHigh > leftHigh * (1 + _config.DoubleTopRightMaxExceedPct / 100)) continue;
            if (reboundHigh < leftHigh * (1 - _config.DoubleTopEarlyApproachPct / 100)) continue;
            if (reboundHighIdx < total - _config.DoubleTopEarlyMaxAgeBars) continue;

            var rejectPct = (reboundHigh - currentPrice) / reboundHigh * 100;
            if (rejectPct < _config.DoubleTopEarlyRejectPct) continue;

            // 量能背离
            var leftLegStart = Math.Max(0, left.Index - 12);
            var leftLegTroughIdx = leftLegStart;
            for (var k = leftLegStart; k <= left.Index; k++)
            {
                if (prices[k] < prices[leftLegTroughIdx]) leftLegTroughIdx = k;
            }
            var leftUpLegVol = 0.0;
            for (var k = leftLegTroughIdx; k <= left.Index; k++) leftUpLegVol += volumes[k];
            var reboundVol = 0.0;
            for (var k = troughIdx; k <= reboundHighIdx; k++) reboundVol += volumes[k];
            if (leftUpLegVol <= 0) continue;
            var rightLeftVolumeRatio = reboundVol / leftUpLegVol;
            if (rightLeftVolumeRatio > _config.DoubleTopEarlyVolRatioMax) continue;

            var signal = new SellPointSignal
            {
                LevelName = "双顶形态",
                LevelPrice = troughPrice,
                CurrentPrice = currentPrice,
                IsVolumeAmplified = false
            };
            signal.Set("early", true);
            signal.Set("leftPeak", left.Price);
            signal.Set("attemptHigh", reboundHigh);
            signal.Set("troughPrice", troughPrice);
            signal.Set("neckToTopPct", neckToTopPct);
            signal.Set("rejectPct", rejectPct);
            signal.Set("volumeShrink", true);
            signal.Set("rightLeftVolumeRatio", rightLeftVolumeRatio);

            if (_config.EnablePatternSimilarity && _patternSimilarity != null)
            {
                var patternStart = Math.Max(0, left.Index - 5);
                var candidatePrices = prices.GetRange(patternStart, total - patternStart);
                var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                var keyPoints = new Dictionary<string, int>
                {
                    ["leftPeak"] = left.Index - patternStart,
                    ["neck"] = troughIdx - patternStart,
                    ["rightPeak"] = reboundHighIdx - patternStart,
                    ["breakdown"] = total - 1 - patternStart
                };
                var (sim, det) = _patternSimilarity.CalculateSimilarity(
                    candidatePrices, "double_top", keyPoints, candidateVolumes);
                if (sim < _config.DoubleTopEarlySimilarityMin) continue;
                signal.Set("similarity", sim);
                signal.Set("similarityDetails", det);
            }

            return signal;
        }
        return null;
    }

    /// <summary>
    /// 8. 钓鱼线检测（急拉 + 缓跌 + 量能萎缩）
    /// </summary>
    public SellPointSignal? DetectFishingLine(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var scanStart = Math.Max(2, total - 30);
        SellPointSignal? bestResult = null;

        for (var p = total - 3; p >= scanStart; p--)
        {
            if (prices[p] <= prices[p + 1]) continue;

            var peakPrice = prices[p];
            var surgeStart = Math.Max(0, p - 12);
            var surgeLegPrices = prices.GetRange(surgeStart, p + 1 - surgeStart);
            var surgeStartPrice = surgeLegPrices.Min();
            if (surgeStartPrice <= 0 || peakPrice <= surgeStartPrice) continue;

            var surgePct = (peakPrice - surgeStartPrice) / surgeStartPrice * 100;
            if (surgePct < _config.FishingLineSurgePct) continue;
            if (surgeLegPrices.Count < 2) continue;
            var surgeSlope = surgePct / surgeLegPrices.Count;
            if (surgeSlope < _config.FishingLineSurgeSlope) continue;

            // 前置涨幅
            var preSurgePrices = prices.GetRange(Math.Max(0, surgeStart - 20), surgeStart + 1 - Math.Max(0, surgeStart - 20));
            var preSurgeLow = preSurgePrices.Min();
            var preRisePct = preSurgeLow > 0 ? (surgeStartPrice - preSurgeLow) / preSurgeLow * 100 : 0;
            if (preRisePct < _config.TopPatternMinPreRisePct) continue;

            // 均价线缺口
            var peakAvgPrice = snapshots[p].AvgPrice;
            if (peakAvgPrice > 0)
            {
                var peakDeviation = (peakPrice - peakAvgPrice) / peakAvgPrice * 100;
                if (peakDeviation < _config.AvgPriceDistancePct) continue;
            }

            // 回落段
            var downLegPrices = prices.GetRange(p + 1, total - p - 1);
            if (downLegPrices.Count < 2) continue;
            var downEndPrice = downLegPrices[^1];
            var downPct = (peakPrice - downEndPrice) / peakPrice * 100;
            var downSlope = downPct / downLegPrices.Count;
            if (downSlope >= surgeSlope * 0.3) continue;

            var pullbackRatio = downPct / surgePct;
            if (pullbackRatio < _config.FishingLinePullbackRatio) continue;

            // 缩量
            var surgeLegSnaps = snapshots.GetRange(surgeStart, p + 1 - surgeStart);
            var downLegSnaps = snapshots.GetRange(p + 1, total - p - 1);
            var surgeAvgVolume = surgeLegSnaps.Sum(s => GetIntervalVolume(s)) / surgeLegSnaps.Count;
            var downAvgVolume = downLegSnaps.Sum(s => GetIntervalVolume(s)) / downLegSnaps.Count;
            var isDownVolumeShrink = surgeAvgVolume > 0 && downAvgVolume < surgeAvgVolume * _config.FishingLineDownVolShrink;
            var volumeDataAvailable = surgeAvgVolume > 0 && downAvgVolume > 0;
            if (volumeDataAvailable && !isDownVolumeShrink) continue;

            if (currentPrice >= peakPrice * 0.998) continue;
            var totalDropFromPeak = (peakPrice - currentPrice) / peakPrice * 100;
            if (totalDropFromPeak < surgePct * 0.4) continue;

            bestResult = new SellPointSignal
            {
                LevelName = "钓鱼线",
                LevelPrice = peakPrice,
                CurrentPrice = currentPrice,
                IsVolumeAmplified = false
            };
            bestResult.Set("peakPrice", peakPrice);
            bestResult.Set("surgePct", surgePct);
            bestResult.Set("downPct", downPct);
            bestResult.Set("pullbackRatio", pullbackRatio * 100);
            bestResult.Set("surgeSlope", surgeSlope);
            bestResult.Set("downSlope", downSlope);
            bestResult.Set("isDownVolumeShrink", isDownVolumeShrink);

            if (_config.EnablePatternSimilarity && _patternSimilarity != null)
            {
                var patternStart = Math.Max(0, surgeStart - 5);
                var candidatePrices = prices.GetRange(patternStart, total - patternStart);
                var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                var keyPoints = new Dictionary<string, int>
                {
                    ["surgeStart"] = surgeStart - patternStart,
                    ["peak"] = p - patternStart,
                    ["downEnd"] = total - 1 - patternStart
                };
                var (sim, det) = _patternSimilarity.CalculateSimilarity(
                    candidatePrices, "fishing_line", keyPoints, candidateVolumes);
                if (sim < _config.FishingLineSimilarityMin) continue;
                bestResult.Set("similarity", sim);
                bestResult.Set("similarityDetails", det);
            }

            break;
        }

        return bestResult;
    }

    /// <summary>
    /// 9. 三次上攻不创新高
    /// </summary>
    public SellPointSignal? DetectTripleTop(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 15) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var maxLookback = Math.Min(total, 80);
        var searchEnd = total - 1;
        var searchStart = Math.Max(2, total - maxLookback);

        var allPeaks = FindPeaksRobust(prices, 2, _config.DoubleTopMinProminence);
        var peaks = allPeaks
            .Where(pk => pk.Index >= Math.Max(1, searchStart) && pk.Index < searchEnd)
            .Select(pk => new { index = pk.Index, price = pk.Price, volume = volumes[pk.Index] })
            .ToList();
        if (peaks.Count < 3) return null;

        for (var k = peaks.Count - 1; k >= 2; k--)
        {
            var p3 = peaks[k];
            if (total - p3.index > 25) break;

            for (var j = k - 1; j >= 1; j--)
            {
                var p2 = peaks[j];
                if (p3.index - p2.index < 5) continue;
                if (p3.index - p2.index > 30) break;

                for (var i = j - 1; i >= 0; i--)
                {
                    var p1 = peaks[i];
                    if (p2.index - p1.index < 5) continue;
                    if (p2.index - p1.index > 30) break;

                    var maxPrice = Math.Max(p1.price, Math.Max(p2.price, p3.price));
                    var minPrice = Math.Min(p1.price, Math.Min(p2.price, p3.price));
                    var deviation = (maxPrice - minPrice) / maxPrice * 100;
                    if (deviation > _config.TripleTopTolerance) continue;

                    // V型反弹检查
                    var preWindow = _config.DoubleTopPreWindow;
                    var preP1Prices = prices.GetRange(Math.Max(0, p1.index - preWindow), p1.index + 1 - Math.Max(0, p1.index - preWindow));
                    if (preP1Prices.Count < 10) continue;
                    var preStart = preP1Prices[0];
                    var preMin = preP1Prices.Min();
                    if (preStart > 0)
                    {
                        var preDropFromStart = (preStart - preMin) / preStart * 100;
                        var reboundFromStart = (p1.price - preStart) / preStart * 100;
                        if (preDropFromStart > _config.DoubleTopPreVTrendMaxDrop
                            && reboundFromStart < _config.DoubleTopPreVTrendMaxDrop) continue;
                    }
                    var preRisePct = preMin > 0 ? (p1.price - preMin) / preMin * 100 : 0;
                    if (preRisePct < _config.TopPatternMinPreRisePct) continue;

                    // 第一顶后下跌
                    var leftDropBars = _config.DoubleTopLeftDropAfterBars;
                    var afterP1End = Math.Min(total, p1.index + leftDropBars + 1);
                    var afterP1Prices = prices.GetRange(p1.index, afterP1End - p1.index);
                    if (afterP1Prices.Count < 2) continue;
                    var afterP1Low = afterP1Prices.Min();
                    var p1DropPct = (p1.price - afterP1Low) / p1.price * 100;
                    if (p1DropPct < _config.DoubleTopLeftDropAfterMin) continue;

                    // 谷底深度
                    var trough12 = prices.GetRange(p1.index, p2.index + 1 - p1.index).Min();
                    var depth12 = (p1.price - trough12) / p1.price * 100;
                    var trough23 = prices.GetRange(p2.index, p3.index + 1 - p2.index).Min();
                    var depth23 = (p2.price - trough23) / p2.price * 100;
                    var minDepth = GetMinNeckDepth(p1.price);
                    if (depth12 < minDepth || depth23 < minDepth) continue;

                    // 第三顶后回落
                    var afterP3 = prices.GetRange(p3.index + 1, total - p3.index - 1);
                    if (afterP3.Count == 0) continue;
                    var lowAfterP3 = afterP3.Min();
                    var pullback = (p3.price - lowAfterP3) / p3.price * 100;
                    if (pullback < _config.TripleTopPullback) continue;

                    // 成交量验证
                    var (leg1Vol, leg3Vol) = CalculateTripleLegVolumes(prices, volumes, p1.index, p2.index, p3.index);
                    if (leg1Vol > 0 && leg3Vol > leg1Vol * 1.2) continue;
                    var isVolumeAmplified = leg3Vol > 0 && leg3Vol > leg1Vol * 0.8;
                    var volumeShrink = leg1Vol > 0 && leg3Vol > 0 && leg3Vol < leg1Vol * 0.8;
                    var rightLeftVolumeRatio = leg1Vol > 0 ? (double?)leg3Vol / leg1Vol : null;

                    if (currentPrice >= p3.price * 0.997) continue;
                    var dropFromP3 = (p3.price - currentPrice) / p3.price * 100;
                    if (dropFromP3 < _config.DoubleTopRightDropMin) continue;

                    var signal = new SellPointSignal
                    {
                        LevelName = "三次上攻不创新高",
                        LevelPrice = p3.price,
                        CurrentPrice = currentPrice,
                        IsVolumeAmplified = isVolumeAmplified
                    };
                    signal.Set("peaks", new[] { p1.price, p2.price, p3.price });
                    signal.Set("deviation", deviation);
                    signal.Set("pullback", pullback);
                    signal.Set("depth12", depth12);
                    signal.Set("depth23", depth23);
                    signal.Set("dropFromP3", dropFromP3);
                    signal.Set("p1DropPct", p1DropPct);
                    signal.Set("volumeShrink", volumeShrink);
                    signal.Set("rightLeftVolumeRatio", rightLeftVolumeRatio);

                    if (_config.EnablePatternSimilarity && _patternSimilarity != null)
                    {
                        var patternStart = Math.Max(0, p1.index - 5);
                        var candidatePrices = prices.GetRange(patternStart, total - patternStart);
                        var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                        var keyPoints = new Dictionary<string, int>
                        {
                            ["peak1"] = p1.index - patternStart,
                            ["trough1"] = (int)Math.Floor((p1.index + p2.index) / 2.0) - patternStart,
                            ["peak2"] = p2.index - patternStart,
                            ["trough2"] = (int)Math.Floor((p2.index + p3.index) / 2.0) - patternStart,
                            ["peak3"] = p3.index - patternStart,
                            ["breakdown"] = total - 1 - patternStart
                        };
                        var (sim, det) = _patternSimilarity.CalculateSimilarity(
                            candidatePrices, "triple_top", keyPoints, candidateVolumes);
                        if (sim < _config.TripleTopSimilarityMin) continue;
                        signal.Set("similarity", sim);
                        signal.Set("similarityDetails", det);
                    }

                    return signal;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 10. 跌破平台/箱体
    /// </summary>
    public SellPointSignal? DetectPlatformBreakdown(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < _config.PlatformCandles + 5) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();

        var ctx = PrepareAnalyzeCtx(snapshots);
        if (ctx.DayHigh <= ctx.DayLow) return null;
        var dayRange = ctx.DayHigh - ctx.DayLow;
        var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
        if (currentPosition < _config.TopPatternMinPosition) return null;

        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        // 平台前不能是连续下跌
        var prePlatformStart = Math.Max(0, total - _config.PlatformCandles - 10);
        var prePlatformEnd = total - _config.PlatformCandles;
        if (prePlatformEnd > prePlatformStart)
        {
            var prePrices = prices.GetRange(prePlatformStart, prePlatformEnd - prePlatformStart);
            var preTrend = (prePrices[^1] - prePrices[0]) / prePrices[0] * 100;
            if (preTrend < -1) return null;
        }

        var minCandles = Math.Min(_config.PlatformCandles, total - 3);
        var maxBack = Math.Min(total - minCandles - 3, 240);

        for (var end = total - 3; end >= 0 && (total - 3 - end) <= maxBack; end--)
        {
            var start = Math.Max(0, end - minCandles + 1);
            var count = end - start + 1;
            if (count < minCandles) continue;
            var seg = prices.GetRange(start, count);
            var segMax = seg.Max();
            var segMin = seg.Min();
            var mid = (segMax + segMin) / 2;
            if (mid <= 0) continue;
            var amp = (segMax - segMin) / mid * 100;
            if (amp > _config.PlatformAmplitude) continue;

            var tail = prices.GetRange(end + 1, total - end - 1);
            if (tail.Count < 3) continue;
            var tailMin = tail.Min();
            if (tailMin > segMin) continue;

            var breakdownPct = (segMin - currentPrice) / segMin * 100;
            if (breakdownPct < _config.PlatformBreakdownPct) continue;

            var last3Low = prices.Skip(total - 3).Take(3).Min();
            if (currentPrice > last3Low) continue;

            var signal = new SellPointSignal
            {
                LevelName = "跌破平台",
                LevelPrice = segMin,
                CurrentPrice = currentPrice,
                IsVolumeAmplified = CheckVolumeAmplified(snapshots)
            };
            signal.Set("platformMax", segMax);
            signal.Set("platformMin", segMin);
            signal.Set("amplitude", amp);
            signal.Set("breakdownPct", breakdownPct);

            if (_config.EnablePatternSimilarity && _patternSimilarity != null)
            {
                var patternStart = Math.Max(0, start - 2);
                var candidatePrices = prices.GetRange(patternStart, total - patternStart);
                var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
                var keyPoints = new Dictionary<string, int>
                {
                    ["platformStart"] = start - patternStart,
                    ["platformEnd"] = end - patternStart,
                    ["breakdown"] = Math.Min(end + 1, total - 1) - patternStart
                };
                var (sim, det) = _patternSimilarity.CalculateSimilarity(
                    candidatePrices, "platform_break", keyPoints, candidateVolumes);
                if (sim < _config.PlatformBreakSimilarityMin) continue;
                signal.Set("similarity", sim);
                signal.Set("similarityDetails", det);
            }

            return signal;
        }
        return null;
    }

    /// <summary>
    /// 11. 高乖离回落
    /// </summary>
    public SellPointSignal? DetectHighDeviationPullback(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 5) return null;
        var total = snapshots.Count;
        var prices = snapshots.Select(s => s.Price).ToList();
        var volumes = snapshots.Select(s => GetIntervalVolume(s)).ToList();

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var scanStart = Math.Max(0, total - 20);
        var peakPrice = 0.0;
        var peakAvgPrice = 0.0;
        var peakIdx = -1;

        for (var i = total - 1; i >= scanStart; i--)
        {
            var s = snapshots[i];
            if (s.AvgPrice <= 0) continue;
            if (s.Price > peakPrice)
            {
                peakPrice = s.Price;
                peakAvgPrice = s.AvgPrice;
                peakIdx = i;
            }
        }
        if (peakPrice <= 0 || peakAvgPrice <= 0 || peakIdx < 0) return null;

        var deviation = (peakPrice - peakAvgPrice) / peakAvgPrice * 100;
        if (deviation < _config.HighDeviationPct) return null;

        var pullback = (peakPrice - currentPrice) / peakPrice * 100;
        if (pullback < _config.HighDeviationPullback) return null;

        var signal = new SellPointSignal
        {
            LevelName = "高乖离回落",
            LevelPrice = peakPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = false
        };
        signal.Set("peakPrice", peakPrice);
        signal.Set("peakAvgPrice", peakAvgPrice);
        signal.Set("deviation", deviation);
        signal.Set("pullback", pullback);

        if (_config.EnablePatternSimilarity && _patternSimilarity != null)
        {
            var patternStart = Math.Max(0, peakIdx - 10);
            var candidatePrices = prices.GetRange(patternStart, total - patternStart);
            var candidateVolumes = volumes.GetRange(patternStart, total - patternStart);
            var keyPoints = new Dictionary<string, int>
            {
                ["base"] = Math.Max(0, peakIdx - 8) - patternStart,
                ["peak"] = peakIdx - patternStart,
                ["pullback"] = total - 1 - patternStart
            };
            var (sim, det) = _patternSimilarity.CalculateSimilarity(
                candidatePrices, "high_deviation_pullback", keyPoints, candidateVolumes);
            if (sim < _config.HighDeviationPullbackSimilarityMin) return null;
            signal.Set("similarity", sim);
            signal.Set("similarityDetails", det);
        }

        return signal;
    }

    /// <summary>
    /// 12. 跌破分时均价线（三形态识别 + 通用近点特征）
    /// </summary>
    public SellPointSignal? DetectVWAPBreakdown(List<IntradaySnapshot> snapshots, double currentPrice, PlanState planState)
    {
        if (planState == null || planState.VwapBreakdownSnapshotIndex < 0) return null;
        if (planState.VwapBreakdownSignaled) return null;

        var current = snapshots[^1];
        var avgPrice = current.AvgPrice;
        if (avgPrice <= 0) return null;
        if (currentPrice >= avgPrice) return null;

        var breakdownIdx = planState.VwapBreakdownSnapshotIndex;
        var elapsedSnapshots = (snapshots.Count - 1) - breakdownIdx;
        if (elapsedSnapshots < _config.VwapBreakdownConfirm) return null;
        if (elapsedSnapshots > _config.VwapBreakdownMaxElapsed) return null;

        var breakdownPct = (avgPrice - currentPrice) / avgPrice * 100;

        // 通用近点特征
        var declineLookback = _config.VwapBreakdownDeclineLookback;
        var minDecline = _config.VwapBreakdownMinDecline;
        var declineStart = Math.Max(0, breakdownIdx - declineLookback);

        var nearPointHighPrice = 0.0;
        var nearPointHighIdx = -1;
        for (var i = declineStart; i <= breakdownIdx; i++)
        {
            var p = snapshots[i].Price;
            if (p > nearPointHighPrice)
            {
                nearPointHighPrice = p;
                nearPointHighIdx = i;
            }
        }
        if (nearPointHighPrice <= 0 || nearPointHighIdx < 0) return null;
        var declinePct = (nearPointHighPrice - currentPrice) / nearPointHighPrice * 100;
        if (declinePct < minDecline) return null;

        // 形态A：拉升后跌破
        var rallyLookback = _config.VwapBreakdownRallyLookback;
        var rallyMinAbove = _config.VwapBreakdownRallyMinAbove;
        var rallyScanStart = Math.Max(0, breakdownIdx - rallyLookback);
        var rallyAboveMaxPct = 0.0;
        var rallyAboveSnapshotCount = 0;
        var rallyPeakPrice = 0.0;

        for (var i = rallyScanStart; i <= breakdownIdx; i++)
        {
            var sAvg = snapshots[i].AvgPrice;
            var sPrice = snapshots[i].Price;
            if (sAvg <= 0 || sPrice <= 0) continue;
            if (sPrice > sAvg)
            {
                rallyAboveSnapshotCount++;
                var abovePct = (sPrice - sAvg) / sAvg * 100;
                if (abovePct > rallyAboveMaxPct) rallyAboveMaxPct = abovePct;
                if (sPrice > rallyPeakPrice) rallyPeakPrice = sPrice;
            }
        }

        var isPatternA = rallyAboveMaxPct >= rallyMinAbove;
        string? patternName = null;
        Dictionary<string, object?> patternDetail = new();

        if (isPatternA)
        {
            patternName = rallyAboveSnapshotCount >= 15 ? "梯型跌破" : "倒V型跌破";
            patternDetail["rallyAboveMaxPct"] = rallyAboveMaxPct;
            patternDetail["rallyAboveSnapshotCount"] = rallyAboveSnapshotCount;
            patternDetail["rallyLookbackSnapshots"] = breakdownIdx - rallyScanStart + 1;
            patternDetail["rallyPeakPrice"] = rallyPeakPrice;
        }
        else
        {
            // 形态B：震荡跌破型
            var oscLookback = _config.VwapBreakdownOscLookback;
            var oscRange = _config.VwapBreakdownOscRange;
            var oscAboveMin = _config.VwapBreakdownOscAboveMin;
            var oscCrossMin = _config.VwapBreakdownOscCrossMin;
            var oscSlopeMax = _config.VwapBreakdownOscSlopeMax;

            var oscScanStart = Math.Max(0, breakdownIdx - oscLookback);
            var oscWindow = new List<(int idx, double avg, double price, DateTime snapshotAt, double devPct)>();
            for (var i = oscScanStart; i <= breakdownIdx; i++)
            {
                var sAvg = snapshots[i].AvgPrice;
                var sPrice = snapshots[i].Price;
                if (sAvg <= 0 || sPrice <= 0) continue;
                oscWindow.Add((i, sAvg, sPrice, snapshots[i].SnapshotAt, (sPrice - sAvg) / sAvg * 100));
            }
            if (oscWindow.Count < 10) return null;

            var maxAbsDev = 0.0;
            var aboveCount = 0;
            foreach (var w in oscWindow)
            {
                var abs = Math.Abs(w.devPct);
                if (abs > maxAbsDev) maxAbsDev = abs;
                if (w.devPct > 0) aboveCount++;
            }
            if (maxAbsDev > oscRange) return null;
            if (aboveCount < oscAboveMin) return null;

            var crossesUp = 0;
            var crossesDown = 0;
            var prevDev = oscWindow[0].devPct;
            for (var i = 1; i < oscWindow.Count; i++)
            {
                var dev = oscWindow[i].devPct;
                if (prevDev <= 0 && dev > 0) crossesUp++;
                if (prevDev >= 0 && dev < 0) crossesDown++;
                prevDev = dev;
            }
            if (crossesUp < oscCrossMin || crossesDown < oscCrossMin) return null;

            var avgPrices = oscWindow.Select(w => w.avg).ToList();
            var timestamps = oscWindow.Select(w => w.snapshotAt).ToList();
            var startAvg = avgPrices[0];
            if (startAvg <= 0) return null;
            var rawSlope = CalculateSlopeByTime(avgPrices, timestamps);
            var vwapSlopePctPerMin = rawSlope / startAvg * 100;
            if (vwapSlopePctPerMin > oscSlopeMax) return null;

            patternName = "震荡跌破";
            patternDetail["oscWindowSize"] = oscWindow.Count;
            patternDetail["maxAbsDev"] = maxAbsDev;
            patternDetail["aboveCount"] = aboveCount;
            patternDetail["crossesUp"] = crossesUp;
            patternDetail["crossesDown"] = crossesDown;
            patternDetail["vwapSlopePctPerMin"] = vwapSlopePctPerMin;
        }

        planState.VwapBreakdownSignaled = true;

        var signal = new SellPointSignal
        {
            LevelName = "跌破均价线",
            LevelPrice = avgPrice,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = CheckVolumeAmplified(snapshots)
        };
        signal.Set("avgPrice", avgPrice);
        signal.Set("breakdownPct", breakdownPct);
        signal.Set("elapsedSnapshots", elapsedSnapshots);
        signal.Set("declinePct", declinePct);
        signal.Set("nearPointHighPrice", nearPointHighPrice);
        signal.Set("nearPointHighIdx", nearPointHighIdx);
        signal.Set("patternName", patternName);
        signal.Set("patternDetail", patternDetail);
        return signal;
    }

    /// <summary>
    /// 13. 均线挡道（上攻受阻）
    /// </summary>
    public SellPointSignal? DetectVWAPRejection(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 6) return null;
        var total = snapshots.Count;

        var ctx = PrepareAnalyzeCtx(snapshots);
        var dayRange = ctx.DayHigh - ctx.DayLow;
        if (dayRange > 0)
        {
            var currentPosition = (currentPrice - ctx.DayLow) / dayRange;
            if (currentPosition < _config.TopPatternMinPosition) return null;
        }
        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.TopPatternMaxVwapSlope) return null;

        var recent20 = snapshots.Skip(total - Math.Min(20, total)).ToList();
        var prices20 = recent20.Select(s => s.Price).ToList();
        var priceMin = prices20.Min();
        var priceMax = prices20.Max();
        var priceMid = (priceMin + priceMax) / 2;

        var current = snapshots[total - 1];
        var lastAvg = current.AvgPrice;

        if (lastAvg > 0 && Math.Abs(lastAvg - priceMid) / priceMid > 0.08)
        {
            Log.Warning("[VWAP_REJECTION] avgPrice数据异常: lastAvg={LastAvg:F2}, priceMid={PriceMid:F2}",
                lastAvg, priceMid);
            return null;
        }

        if (lastAvg > 0)
        {
            var currentGap = (lastAvg - currentPrice) / lastAvg * 100;
            if (currentGap > 1.5) return null;
        }

        var scanStart = Math.Max(0, total - 15);
        var touchIndex = -1;

        for (var i = total - 2; i >= scanStart; i--)
        {
            var s = snapshots[i];
            var avg = s.AvgPrice;
            if (avg <= 0) continue;
            if (Math.Abs(avg - priceMid) / priceMid > 0.05) continue;

            var gap = (avg - s.Price) / avg * 100;
            if (gap > 0 && gap < _config.VwapRejectionGap)
            {
                touchIndex = i;
                break;
            }
        }
        if (touchIndex < 0) return null;

        var afterTouchLen = total - 1 - touchIndex;
        if (afterTouchLen < _config.VwapRejectionConfirm) return null;

        if (lastAvg <= 0) return null;
        if (currentPrice >= lastAvg) return null;

        var touchPrice = snapshots[touchIndex].Price;
        var pullback = (touchPrice - currentPrice) / touchPrice * 100;
        if (pullback < 0.5) return null;

        var signal = new SellPointSignal
        {
            LevelName = "均线挡道",
            LevelPrice = lastAvg,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = false
        };
        signal.Set("touchPrice", touchPrice);
        signal.Set("touchAvgPrice", snapshots[touchIndex].AvgPrice);
        signal.Set("pullback", pullback);
        return signal;
    }

    /// <summary>
    /// 14. 均价线拐头向下
    /// </summary>
    public SellPointSignal? DetectVWAPSlopeDown(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < _config.VwapSlopeDownCandles + 3) return null;

        var windowSize = Math.Max(_config.VwapSlopeDownCandles + 3, 8);
        var recent = snapshots.Skip(snapshots.Count - Math.Min(windowSize, snapshots.Count)).ToList();

        var recentValid = recent.Where(s => s.AvgPrice > 0).ToList();
        if (recentValid.Count < _config.VwapSlopeDownCandles) return null;

        var slice = recentValid.Skip(recentValid.Count - _config.VwapSlopeDownCandles).ToList();
        var slicePrices = slice.Select(s => s.AvgPrice).ToList();
        var sliceTimestamps = slice.Select(s => s.SnapshotAt).ToList();
        var startAvg = slicePrices[0];
        if (startAvg <= 0) return null;

        var rawSlope = CalculateSlopeByTime(slicePrices, sliceTimestamps);
        var slope = rawSlope / startAvg * 100;
        if (slope >= _config.VwapSlopeDownThreshold) return null;

        var currentAvg = recentValid.Count > 0 ? recentValid[^1].AvgPrice : 0;
        if (currentAvg <= 0 || currentPrice >= currentAvg) return null;

        var signal = new SellPointSignal
        {
            LevelName = "均价线拐头向下",
            LevelPrice = currentAvg,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = false
        };
        signal.Set("slope", slope);
        signal.Set("currentAvg", currentAvg);
        return signal;
    }

    /// <summary>
    /// 15. 尾盘资金出逃
    /// </summary>
    public SellPointSignal? DetectLateSessionExit(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 8) return null;

        var current = snapshots[^1];
        var (hours, minutes) = GetHourMin(current.SnapshotAt);
        var parts = _config.LateSessionStart.Split(':');
        var startH = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var startM = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (hours < startH || (hours == startH && minutes < startM)) return null;

        if (!current.VolumeReliable) return null;

        var previous = snapshots.Skip(snapshots.Count - 6).Take(5).ToList();
        var avgVolume = previous.Sum(s => GetIntervalVolume(s)) / previous.Count;
        var currentVol = GetIntervalVolume(current);
        if (currentVol == 0 || avgVolume == 0) return null;
        if (currentVol < avgVolume * _config.LateSessionVolumeMultiple) return null;

        var recentPrices = snapshots.Skip(snapshots.Count - 10).Take(10).Select(s => s.Price).ToList();
        var recentHigh = recentPrices.Max();
        var breakdownPct = (recentHigh - currentPrice) / recentHigh * 100;
        if (breakdownPct < _config.LateSessionBreakdownPct) return null;

        var signal = new SellPointSignal
        {
            LevelName = "尾盘资金出逃",
            LevelPrice = recentHigh,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = true
        };
        signal.Set("currentVolume", currentVol);
        signal.Set("avgVolume", avgVolume);
        signal.Set("volumeMultiple", currentVol / avgVolume);
        signal.Set("breakdownPct", breakdownPct);
        return signal;
    }

    /// <summary>
    /// 16. 缩量均线反弹失败（止损式卖点）
    /// </summary>
    public SellPointSignal? DetectWeakReboundFailure(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < 10) return null;
        var total = snapshots.Count;

        var current = snapshots[total - 1];
        var currentAvg = current.AvgPrice;
        if (currentAvg <= 0) return null;
        if (currentPrice >= currentAvg) return null;

        // 最近 N 根在均线下方
        var belowCount = snapshots.Skip(total - _config.WeakReboundBelowConfirm)
            .Count(s => s.AvgPrice > 0 && s.Price < s.AvgPrice);
        if (belowCount < _config.WeakReboundBelowConfirm) return null;

        // 回溯反弹高点
        var scanStart = Math.Max(0, total - _config.WeakReboundMaxScan);
        int? reboundPeakIdx = null;
        double reboundPeakPrice = 0, reboundPeakAvg = 0, reboundPeakGap = 0;

        for (var i = total - 2; i >= scanStart; i--)
        {
            var s = snapshots[i];
            var avg = s.AvgPrice;
            if (avg <= 0) continue;

            var gap = (s.Price - avg) / avg * 100;
            if (gap > _config.WeakReboundGapMin && gap < _config.WeakReboundGapMax)
            {
                reboundPeakIdx = i;
                reboundPeakPrice = s.Price;
                reboundPeakAvg = avg;
                reboundPeakGap = gap;
                break;
            }
        }
        if (reboundPeakIdx == null) return null;

        var afterLen = total - 1 - reboundPeakIdx.Value;
        if (afterLen < 3) return null;

        var pullback = (reboundPeakPrice - currentPrice) / reboundPeakPrice * 100;
        if (pullback < _config.WeakReboundPullbackPct) return null;

        // 缩量
        var reboundWindowStart = Math.Max(0, reboundPeakIdx.Value - 2);
        var reboundWindowEnd = Math.Min(total, reboundPeakIdx.Value + 3);
        var reboundWindow = snapshots.GetRange(reboundWindowStart, reboundWindowEnd - reboundWindowStart);
        var reboundAvgVol = reboundWindow.Sum(s => GetIntervalVolume(s)) / reboundWindow.Count;

        var beforeWindowStart = Math.Max(0, reboundPeakIdx.Value - 12);
        var beforeWindowEnd = Math.Max(0, reboundPeakIdx.Value - 2);
        var beforeWindow = beforeWindowEnd > beforeWindowStart
            ? snapshots.GetRange(beforeWindowStart, beforeWindowEnd - beforeWindowStart)
            : new List<IntradaySnapshot>();
        var beforeAvgVol = beforeWindow.Count > 0
            ? beforeWindow.Sum(s => GetIntervalVolume(s)) / beforeWindow.Count
            : 0;

        var isVolumeShrink = beforeAvgVol > 0 && reboundAvgVol < beforeAvgVol * _config.WeakReboundVolShrink;
        if (!isVolumeShrink) return null;

        var vwapSlope = CalculateVWAPSlope(snapshots);
        if (vwapSlope > _config.WeakReboundVwapSlopeMax) return null;

        var signal = new SellPointSignal
        {
            LevelName = "缩量均线反弹失败",
            LevelPrice = reboundPeakAvg,
            CurrentPrice = currentPrice,
            IsVolumeAmplified = false,
            IsStopLoss = true
        };
        signal.Set("reboundPrice", reboundPeakPrice);
        signal.Set("reboundGap", reboundPeakGap);
        signal.Set("pullback", pullback);
        signal.Set("volumeShrinkRatio", beforeAvgVol > 0 ? reboundAvgVol / beforeAvgVol : 0);
        signal.Set("vwapSlope", vwapSlope);
        return signal;
    }

    /// <summary>
    /// 16b. 大跌反抽卖点（深跌后反弹衰竭）
    /// </summary>
    public SellPointSignal? DetectDeepDropRebound(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        if (snapshots.Count < _config.DeepDropMinSnapshots) return null;
        var total = snapshots.Count;

        var basePrice = snapshots[total - 1].PreClose > 0
            ? snapshots[total - 1].PreClose
            : (snapshots[0].PreClose > 0 ? snapshots[0].PreClose : 0);
        if (basePrice <= 0) return null;

        // 日内最低点
        var lowIdx = -1;
        var lowPrice = double.MaxValue;
        for (var i = 0; i < total; i++)
        {
            if (snapshots[i].Price < lowPrice)
            {
                lowPrice = snapshots[i].Price;
                lowIdx = i;
            }
        }
        if (lowIdx < 0) return null;

        var dropPct = (lowPrice - basePrice) / basePrice * 100;
        if (dropPct > -_config.DeepDropMinPct) return null;

        // 反弹高点
        var reboundIdx = -1;
        var reboundHigh = double.MinValue;
        for (var i = lowIdx + 1; i < total; i++)
        {
            if (snapshots[i].Price > reboundHigh)
            {
                reboundHigh = snapshots[i].Price;
                reboundIdx = i;
            }
        }
        if (reboundIdx < 0 || reboundHigh <= lowPrice) return null;

        var reboundPct = (reboundHigh - lowPrice) / lowPrice * 100;
        if (reboundPct < _config.DeepDropReboundMinPct) return null;

        // 反抽过均线
        var reboundAvg = snapshots[reboundIdx].AvgPrice;
        if (reboundAvg > 0 && reboundHigh < reboundAvg * (1 - _config.DeepDropAboveVwapTol / 100)) return null;

        // 触及平台
        var platform = FindPlatformBefore(snapshots, reboundIdx);
        string? touchedPlatform = null;
        if (platform != null)
        {
            var tol = _config.DeepDropTouchTolerance;
            var nearTop = Math.Abs(reboundHigh - platform.Top) / platform.Top * 100 <= tol;
            var nearBottom = Math.Abs(reboundHigh - platform.Bottom) / platform.Bottom * 100 <= tol;
            var inside = reboundHigh >= platform.Bottom && reboundHigh <= platform.Top;
            if (!nearTop && !nearBottom && !inside) return null;
            touchedPlatform = nearTop ? "top" : (nearBottom ? "bottom" : "inside");
        }

        // 末端缩量
        bool? isVolumeShrink = null;
        if (snapshots[total - 1].VolumeReliable)
        {
            var tailStart = Math.Max(lowIdx + 1, reboundIdx - 7);
            var tailEnd = Math.Min(reboundIdx + 1, total);
            var tailSeg = snapshots.GetRange(tailStart, tailEnd - tailStart);
            var prevStart = Math.Max(lowIdx + 1, tailStart - 12);
            var prevSeg = snapshots.GetRange(prevStart, tailStart - prevStart);
            if (tailSeg.Count >= 4 && prevSeg.Count >= 6)
            {
                var tailAvg = tailSeg.Sum(s => GetIntervalVolume(s)) / tailSeg.Count;
                var prevAvg = prevSeg.Sum(s => GetIntervalVolume(s)) / prevSeg.Count;
                if (prevAvg > 0)
                {
                    isVolumeShrink = tailAvg < prevAvg * _config.DeepDropVolShrink;
                    if (isVolumeShrink == false) return null;
                }
            }
        }

        // 回落确认
        var afterLen = total - 1 - reboundIdx;
        if (afterLen < 3) return null;
        if (afterLen > _config.DeepDropMaxElapsed) return null;

        var pullbackPct = (reboundHigh - currentPrice) / reboundHigh * 100;
        if (pullbackPct < _config.DeepDropPullbackPct) return null;

        var signal = new SellPointSignal
        {
            LevelName = "大跌反抽卖点",
            LevelPrice = reboundHigh,
            CurrentPrice = currentPrice,
            IsStopLoss = true
        };
        signal.Set("dropPct", dropPct);
        signal.Set("reboundPct", reboundPct);
        signal.Set("pullbackPct", pullbackPct);
        signal.Set("lowPrice", lowPrice);
        signal.Set("reboundAboveVwap", reboundAvg > 0);
        signal.Set("touchedPlatform", touchedPlatform);
        signal.Set("platformTop", platform?.Top);
        signal.Set("platformBottom", platform?.Bottom);
        signal.Set("isVolumeShrink", isVolumeShrink);
        return signal;
    }

    /// <summary>
    /// 17. ATR 止损止盈检测
    /// </summary>
    public SellPointSignal? DetectATRStopLoss(List<IntradaySnapshot> snapshots, double currentPrice, TradingPlanInfo? plan)
    {
        if (plan == null || plan.EntryPrice <= 0 || snapshots.Count < 10) return null;

        var entryPrice = plan.EntryPrice;
        var atr = CalculateATR(snapshots);
        if (atr <= 0) return null;

        var stopLossLine = entryPrice - 2 * atr;
        var takeProfitLine = entryPrice + 4 * atr;

        var prices = snapshots.Select(s => s.Price).Where(v => double.IsFinite(v) && v > 0).ToList();
        var maxPrice = Math.Max(entryPrice, prices.Count > 0 ? prices.Max() : entryPrice);
        var trailingStopLine = maxPrice - 2 * atr;

        // ATR 止损
        if (currentPrice <= stopLossLine)
        {
            var signal = new SellPointSignal
            {
                Type = SignalTypes.AtrStopLoss,
                LevelName = "ATR止损",
                CurrentPrice = currentPrice,
                LevelPrice = stopLossLine,
                IsStopLoss = true
            };
            signal.Set("atr", atr);
            signal.Set("entryPrice", entryPrice);
            signal.Set("stopLossLine", stopLossLine);
            signal.Set("breakdownPct", stopLossLine > 0 ? (stopLossLine - currentPrice) / stopLossLine * 100 : 0);
            return signal;
        }

        // Trailing Stop
        if (currentPrice < maxPrice && currentPrice <= trailingStopLine && maxPrice > entryPrice)
        {
            var profitFromHigh = (maxPrice - currentPrice) / maxPrice * 100;
            if (profitFromHigh > 0.5)
            {
                var signal = new SellPointSignal
                {
                    Type = SignalTypes.AtrTrailingStop,
                    LevelName = "追踪止损",
                    CurrentPrice = currentPrice,
                    LevelPrice = trailingStopLine,
                    IsStopLoss = true
                };
                signal.Set("atr", atr);
                signal.Set("entryPrice", entryPrice);
                signal.Set("maxPrice", maxPrice);
                signal.Set("pullbackPct", profitFromHigh);
                return signal;
            }
        }

        // ATR 止盈
        if (currentPrice >= takeProfitLine)
        {
            var signal = new SellPointSignal
            {
                Type = SignalTypes.AtrTakeProfit,
                LevelName = "ATR止盈",
                CurrentPrice = currentPrice,
                LevelPrice = takeProfitLine
            };
            signal.Set("atr", atr);
            signal.Set("entryPrice", entryPrice);
            signal.Set("takeProfitLine", takeProfitLine);
            signal.Set("profitPct", (currentPrice - entryPrice) / entryPrice * 100);
            return signal;
        }

        // 时间止损
        if (plan.CreatedAt.HasValue)
        {
            var lastSnap = snapshots[^1];
            var refTime = lastSnap.SnapshotAt;
            var holdingMs = (refTime - plan.CreatedAt.Value).TotalMilliseconds;
            var holdingMin = holdingMs / (60 * 1000);
            if (holdingMin >= 60)
            {
                var profitPct = (currentPrice - entryPrice) / entryPrice * 100;
                if (profitPct < 1.0)
                {
                    var signal = new SellPointSignal
                    {
                        Type = SignalTypes.TimeStop,
                        LevelName = "时间止损",
                        CurrentPrice = currentPrice,
                        IsStopLoss = true
                    };
                    signal.Set("entryPrice", entryPrice);
                    signal.Set("holdingMin", JsMath.JsRound(holdingMin));
                    signal.Set("profitPct", profitPct);
                    return signal;
                }
            }
        }

        return null;
    }

    // ==================== 技术指标计算 ====================

    /// <summary>
    /// 计算动态支撑位（Pivot Low 聚类 + 成交量密集区 + Pivot Point）
    /// </summary>
    public double? CalculateDynamicSupport(List<IntradaySnapshot> snapshots, PrevDayData? prevDay)
    {
        if (snapshots.Count < 30) return null;

        var windowSize = Math.Min(120, snapshots.Count);
        var recent = snapshots.Skip(snapshots.Count - windowSize).ToList();
        var currentPrice = recent[^1].Price;

        var prices = recent.Select(s => s.Price).ToList();
        var lows = recent.Select(s => s.Low > 0 ? s.Low : s.Price).ToList();
        var vols = recent.Select(s => GetIntervalVolume(s)).ToList();

        // Pivot Low 聚类
        var pivotLows = new List<(double price, double volume, int index)>();
        var pivotSpan = 3;
        for (var i = pivotSpan; i < recent.Count - pivotSpan; i++)
        {
            var low = lows[i];
            if (low <= 0) continue;
            var isPivot = true;
            for (var j = i - pivotSpan; j <= i + pivotSpan; j++)
            {
                if (j == i) continue;
                if (lows[j] < low) { isPivot = false; break; }
            }
            if (isPivot)
            {
                var surroundingVol = recent.Skip(Math.Max(0, i - pivotSpan)).Take(pivotSpan * 2 + 1)
                    .Sum(s => GetIntervalVolume(s));
                pivotLows.Add((low, surroundingVol, i));
            }
        }

        const double pivotClusterPct = 0.005;
        var clusters = new List<List<(double price, double volume)>>();
        var clusterAvgs = new List<double>();
        var clusterVols = new List<double>();
        foreach (var pl in pivotLows)
        {
            var found = -1;
            for (var c = 0; c < clusterAvgs.Count; c++)
            {
                if (Math.Abs(pl.price - clusterAvgs[c]) / clusterAvgs[c < pivotClusterPct ? 0 : c] < pivotClusterPct)
                {
                    found = c;
                    break;
                }
            }
            // Simplified clustering
            found = -1;
            for (var c = 0; c < clusters.Count; c++)
            {
                if (clusterAvgs[c] > 0 && Math.Abs(pl.price - clusterAvgs[c]) / clusterAvgs[c] < pivotClusterPct)
                {
                    found = c;
                    break;
                }
            }
            if (found >= 0)
            {
                clusters[found].Add((pl.price, pl.volume));
                clusterAvgs[found] = clusters[found].Sum(x => x.price) / clusters[found].Count;
                clusterVols[found] += pl.volume;
            }
            else
            {
                clusters.Add(new List<(double, double)> { (pl.price, pl.volume) });
                clusterAvgs.Add(pl.price);
                clusterVols.Add(pl.volume);
            }
        }

        var pivotSupportCandidates = clusters
            .Select((c, i) => new { avg = clusterAvgs[i], count = c.Count, vol = clusterVols[i] })
            .Where(x => x.count >= 2 && x.avg < currentPrice && x.avg > 0)
            .OrderBy(x =>
            {
                var dist = currentPrice - x.avg;
                return dist;
            })
            .ToList();

        // 成交量密集区
        var minPrice = prices.Min();
        var maxPrice = prices.Max();
        var priceRange = maxPrice - minPrice;
        if (priceRange <= 0) return null;

        var bucketCount = Math.Max(10, (int)Math.Floor(priceRange * 10));
        var bucketSize = priceRange / bucketCount;
        var volBuckets = new double[bucketCount];
        for (var i = 0; i < prices.Count; i++)
        {
            var idx = Math.Min(bucketCount - 1, Math.Max(0, (int)Math.Floor((prices[i] - minPrice) / bucketSize)));
            volBuckets[idx] += vols[i];
        }

        var avgVolume = vols.Sum() / Math.Max(vols.Count, 1);
        const double minVolRatio = 1.2;
        var volumeClusterCandidates = new List<(double price, double volume, double strength)>();
        for (var i = 0; i < bucketCount; i++)
        {
            if (volBuckets[i] > avgVolume * minVolRatio)
            {
                var clusterPrice = minPrice + (i + 0.5) * bucketSize;
                if (clusterPrice < currentPrice && clusterPrice > 0)
                {
                    volumeClusterCandidates.Add((clusterPrice, volBuckets[i], volBuckets[i] / avgVolume));
                }
            }
        }

        // Pivot Point
        double? pivotPointS1 = null;
        if (prevDay != null && prevDay.High > 0 && prevDay.Low > 0 && prevDay.Close > 0)
        {
            var p = (prevDay.High + prevDay.Low + 2 * prevDay.Close) / 4;
            pivotPointS1 = 2 * p - prevDay.High;
            if (pivotPointS1 >= currentPrice || pivotPointS1 <= 0)
                pivotPointS1 = null;
        }

        // 综合评分
        var allCandidates = new List<(double value, string source, int score)>();
        foreach (var c in pivotSupportCandidates.Take(3))
            allCandidates.Add((c.avg, "pivot_low", c.count * 2));
        foreach (var c in volumeClusterCandidates.Take(3))
            allCandidates.Add((c.price, "volume_cluster", (int)Math.Floor(c.strength * 1.5)));
        if (pivotPointS1.HasValue)
            allCandidates.Add((pivotPointS1.Value, "pivot_point", 2));

        if (allCandidates.Count == 0) return null;

        // 共振验证
        const double resonancePct = 0.008;
        var resonanceClusters = new List<List<(double value, string source, int score)>>();
        var resonanceAvgs = new List<double>();
        var resonanceScores = new List<int>();

        foreach (var c in allCandidates)
        {
            var found = -1;
            for (var r = 0; r < resonanceAvgs.Count; r++)
            {
                if (resonanceAvgs[r] > 0 && Math.Abs(c.value - resonanceAvgs[r]) / resonanceAvgs[r] < resonancePct)
                {
                    found = r;
                    break;
                }
            }
            if (found >= 0)
            {
                resonanceClusters[found].Add(c);
                resonanceAvgs[found] = resonanceClusters[found].Sum(x => x.value) / resonanceClusters[found].Count;
                resonanceScores[found] += c.score;
            }
            else
            {
                resonanceClusters.Add(new List<(double, string, int)> { c });
                resonanceAvgs.Add(c.value);
                resonanceScores.Add(c.score);
            }
        }

        // 排序：共振来源数 > 得分 > 距离
        var indexed = resonanceClusters.Select((c, i) => new
        {
            avg = resonanceAvgs[i],
            sourceCount = c.Select(x => x.source).Distinct().Count(),
            totalScore = resonanceScores[i]
        }).OrderByDescending(x => x.sourceCount)
          .ThenByDescending(x => x.totalScore)
          .ThenBy(x => currentPrice - x.avg)
          .ToList();

        var best = indexed.FirstOrDefault();
        if (best == null) return null;

        var distancePct = (currentPrice - best.avg) / currentPrice * 100;
        if (distancePct < _config.SupportMinDistancePct) return null;

        if (best.sourceCount >= 2) return best.avg;
        if (best.totalScore >= 6) return best.avg;
        return null;
    }

    /// <summary>
    /// 计算RSI（Wilder's RSI）
    /// </summary>
}
