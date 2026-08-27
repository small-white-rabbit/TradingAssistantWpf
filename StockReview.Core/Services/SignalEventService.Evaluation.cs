using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

public partial class SignalEventService
{

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

}
