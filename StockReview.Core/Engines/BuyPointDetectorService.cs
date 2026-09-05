using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace StockReview.Core.Engines;

/// <summary>
/// 分时买点识别器
/// 5 个日内买点信号 + 三关前置过滤 + 评分系统
///   - VWAP_DIP         均价线回踩
///   - W_BOTTOM         分时W底突破
///   - PANIC_BUY        急跌缩量
///   - TAIL_BUY         尾盘回补
///   - REVERSAL_KLINE   分时反转K线（Pinbar/锤头/吞没）
/// </summary>
public class BuyPointDetectorService
{
    // ============ 信号类型 ============
    public const string VWAP_DIP = "vwap_dip";
    public const string W_BOTTOM = "w_bottom";
    public const string PANIC_BUY = "panic_buy";
    public const string TAIL_BUY = "tail_buy";
    public const string REVERSAL_KLINE = "reversal_kline";

    public static readonly Dictionary<string, string> SignalLabels = new()
    {
        [VWAP_DIP] = "均价线回踩",
        [W_BOTTOM] = "分时W底",
        [PANIC_BUY] = "急跌缩量",
        [TAIL_BUY] = "尾盘回补",
        [REVERSAL_KLINE] = "反转K线"
    };

    // ============ 默认配置 ============
    public BuyConfig Config { get; set; } = new();

    private const int MaxIntradaySnapshots = 480;
    // HostedService 并发分析多只股票，必须用并发字典（普通 Dictionary 并发写会抛异常/读到中间态）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlanState> _planStates = new();

    // 上次过滤诊断信息
    public Dictionary<string, object?> LastFilterInfo { get; private set; } = new();

    public BuyPointDetectorService()
    {
        Log.Information("[BuyPointDetector] 买点检测器初始化完成");
    }

    public void UpdateConfig(Dictionary<string, object> updates)
    {
        // 按属性名（不区分大小写）反射映射到 BuyConfig，未匹配项告警
        if (updates == null || updates.Count == 0) return;
        var props = typeof(BuyConfig).GetProperties();
        var applied = 0;
        foreach (var kv in updates)
        {
            if (kv.Value == null) continue;
            var prop = props.FirstOrDefault(p =>
                string.Equals(p.Name, kv.Key, StringComparison.OrdinalIgnoreCase) && p.CanWrite);
            if (prop == null) continue;
            try
            {
                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                var value = Convert.ChangeType(kv.Value, targetType,
                    System.Globalization.CultureInfo.InvariantCulture);
                prop.SetValue(Config, value);
                applied++;
            }
            catch (Exception ex)
            {
                Log.Warning("[BuyPointDetector] 配置项 {Key}={Value} 转换失败: {Msg}",
                    kv.Key, kv.Value, ex.Message);
            }
        }
        Log.Information("[BuyPointDetector] 配置更新：{Applied}/{Total} 项生效", applied, updates.Count);
    }

    // ============ 快照预处理 ============

    /// <summary>
    /// 裁剪日内全量快照（最多保留480根）
    /// </summary>
    public List<IntradaySnapshot> NormalizeIntraday(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return new();
        if (snapshots.Count <= MaxIntradaySnapshots) return snapshots;
        return snapshots.Skip(snapshots.Count - MaxIntradaySnapshots).ToList();
    }

    /// <summary>
    /// 成交量语义标准化：从 cumulativeVolume 推导 intervalVolume
    /// </summary>
    public List<IntradaySnapshot> NormalizeVolumes(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return snapshots ?? new();
        for (var i = snapshots.Count - 1; i > 0; i--)
        {
            var curr = snapshots[i];
            var prev = snapshots[i - 1];
            if (curr.IntervalVolume.GetValueOrDefault() != 0) continue;
            if (curr.CumulativeVolume != 0 && prev.CumulativeVolume != 0)
            {
                curr.IntervalVolume = Math.Max(0, curr.CumulativeVolume - prev.CumulativeVolume);
            }
        }
        if (snapshots[0].IntervalVolume.GetValueOrDefault() == 0 && snapshots[0].Volume != 0)
        {
            snapshots[0].IntervalVolume = snapshots[0].Volume;
        }
        return snapshots;
    }

    /// <summary>
    /// 读取区间成交量
    /// </summary>
    private static double GetIntervalVolume(IntradaySnapshot s)
    {
        if (s == null) return 0;
        return s.IntervalVolume.GetValueOrDefault() != 0 ? s.IntervalVolume!.Value : s.Volume;
    }

    /// <summary>
    /// 分时均价修复：填充缺失值 + 中值滤波
    /// </summary>
    public void RepairAvgPrice(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return;
        var lastValidAvg = 0.0;
        foreach (var s in snapshots)
        {
            if (s.AvgPrice > 0) lastValidAvg = s.AvgPrice;
            else if (lastValidAvg > 0) s.AvgPrice = lastValidAvg;
        }
        for (var i = 1; i < snapshots.Count - 1; i++)
        {
            var curr = snapshots[i].AvgPrice;
            var prev = snapshots[i - 1].AvgPrice;
            var next = snapshots[i + 1].AvgPrice;
            if (curr <= 0 || prev <= 0 || next <= 0) continue;
            if (Math.Abs(curr - prev) / prev > 0.05 && Math.Abs(curr - next) / next > 0.05)
            {
                snapshots[i].AvgPrice = (prev + next) / 2;
            }
        }
    }

    // ============ 工具方法 ============

    private static string GetTimeStr(IntradaySnapshot snapshot)
    {
        if (snapshot == null) return "";
        var tz = StockReview.Core.Services.CnTimeZone.Get;
        var dt = TimeZoneInfo.ConvertTimeFromUtc(snapshot.SnapshotAt.ToUniversalTime(), tz);
        return $"{dt.Hour:D2}:{dt.Minute:D2}";
    }

    private bool IsLateSession(string timeStr) => !string.IsNullOrEmpty(timeStr) && string.Compare(timeStr, Config.TailBuyStartTime) >= 0;

    /// <summary>
    /// 计算日内位置 (0=最低, 1=最高)
    /// </summary>
    private static double CalcPosition(List<IntradaySnapshot> snapshots, double currentPrice, double preClose = 0)
    {
        if (snapshots == null || snapshots.Count == 0) return 0.5;
        var high = double.MinValue;
        var low = double.MaxValue;
        foreach (var s in snapshots)
        {
            var h = s.High != 0 ? s.High : s.Price;
            var l = s.Low != 0 ? s.Low : s.Price;
            if (h > high) high = h;
            if (l < low) low = l;
        }
        if (preClose > 0)
        {
            if (preClose > high) high = preClose;
            if (preClose < low) low = preClose;
        }
        if (high == low) return 0.5;
        return (currentPrice - low) / (high - low);
    }

    /// <summary>
    /// 计算日内振幅 %
    /// </summary>
    private static double CalcAmplitude(List<IntradaySnapshot> snapshots, double preClose = 0)
    {
        if (snapshots == null || snapshots.Count == 0) return 0;
        var high = double.MinValue;
        var low = double.MaxValue;
        foreach (var s in snapshots)
        {
            var h = s.High != 0 ? s.High : s.Price;
            var l = s.Low != 0 ? s.Low : s.Price;
            if (h > high) high = h;
            if (l < low) low = l;
        }
        if (preClose > 0)
        {
            if (preClose > high) high = preClose;
            if (preClose < low) low = preClose;
        }
        if (low == 0) return 0;
        return ((high - low) / low) * 100;
    }

    /// <summary>
    /// 计算区间均量
    /// </summary>
    private static double AvgVolume(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return 0;
        return snapshots.Average(s => GetIntervalVolume(s));
    }

    // ============ 三关辅助 ============

    /// <summary>
    /// 基于日K线收盘价算 MA
    /// </summary>
    public static double[]? CalculateDailyMA(List<DailyKline>? dailyKlines, int period = 5)
    {
        if (dailyKlines == null || dailyKlines.Count < period) return null;
        var result = new double[dailyKlines.Count];
        var sum = 0.0;
        for (var i = 0; i < dailyKlines.Count; i++)
        {
            sum += dailyKlines[i].Close;
            if (i >= period) sum -= dailyKlines[i - period].Close;
            result[i] = i >= period - 1 ? sum / period : 0;
        }
        return result;
    }

    /// <summary>
    /// 日K线 MA5 方向判定: 'up' / 'flat' / 'down'
    /// </summary>
    public string CheckDailyMA5Direction(List<DailyKline>? dailyKlines)
    {
        var tol = Config.Ma5FlatTolerance / 100.0;
        if (dailyKlines == null || dailyKlines.Count < 8) return "flat";
        var ma = CalculateDailyMA(dailyKlines, 5);
        if (ma == null) return "flat";
        var lastMa = ma[^1];
        var refMa = ma[^4];
        if (lastMa == 0 || refMa == 0) return "flat";
        var diff = (lastMa - refMa) / refMa;
        if (Math.Abs(diff) < tol) return "flat";
        return diff > 0 ? "up" : "down";
    }

    /// <summary>
    /// 分时均价线（近10根）的归一化斜率
    /// </summary>
    public static double CalcVWAPSlopeRecent(List<IntradaySnapshot> snapshots)
    {
        var n = Math.Min(10, snapshots.Count);
        if (n < 4) return 0;
        var arr = snapshots.Skip(snapshots.Count - n).ToList();
        var sumX = 0.0; var sumY = 0.0; var sumXY = 0.0; var sumX2 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var y = arr[i].AvgPrice != 0 ? arr[i].AvgPrice : arr[i].Price;
            if (y == 0) return 0;
            sumX += i; sumY += y; sumXY += i * y; sumX2 += i * i;
        }
        var denom = n * sumX2 - sumX * sumX;
        if (denom == 0) return 0;
        var slope = (n * sumXY - sumX * sumY) / denom;
        var avgY = sumY / n;
        return avgY > 0 ? slope / avgY : 0;
    }

    /// <summary>
    /// 最近 lookback 根K线的区间振幅 %（用 close 序列）
    /// </summary>
    public static double CalcLookbackAmplitude(List<IntradaySnapshot> snapshots, int lookback = 30)
    {
        if (snapshots == null || snapshots.Count == 0) return 0;
        var slice = snapshots.Skip(Math.Max(0, snapshots.Count - lookback)).ToList();
        var hi = double.MinValue; var lo = double.MaxValue;
        foreach (var s in slice)
        {
            var c = s.Price;
            if (c > hi) hi = c;
            if (c < lo) lo = c;
        }
        if (lo == 0 || hi == lo) return 0;
        return ((hi - lo) / lo) * 100;
    }

    // ============ 主分析入口 ============

    /// <summary>
    /// 主分析入口 - 对应 JS analyze()
    /// </summary>
    public List<BuySignalResult> Analyze(
        string planId,
        double currentPrice,
        double preClose,
        List<IntradaySnapshot> snapshots,
        List<DailyKline>? dailyKlines = null)
    {
        if (snapshots == null || snapshots.Count < 5) return new();

        // 排序：旧→新
        var sorted = snapshots.OrderBy(s => s.SnapshotAt).ToList();
        sorted = NormalizeIntraday(sorted);
        NormalizeVolumes(sorted);
        RepairAvgPrice(sorted);

        var lastSnap = sorted[^1];
        var timeStr = GetTimeStr(lastSnap);
        var pc = preClose > 0 ? preClose : (lastSnap.PreClose > 0 ? lastSnap.PreClose : currentPrice);

        // 日内振幅过滤
        var amplitude = CalcAmplitude(sorted, pc);
        if (amplitude < Config.MinAmplitude)
        {
            LastFilterInfo = new() { ["reason"] = "振幅不足", ["amplitude"] = $"{amplitude:F3}%" };
            return new();
        }

        // 状态（GetOrAdd 原子化，避免并发初始化竞态）
        var planState = _planStates.GetOrAdd(planId, _ => new PlanState());

        var isLate = IsLateSession(timeStr);
        var position = CalcPosition(sorted, currentPrice, pc);

        // ===== 三关前置过滤 =====
        // 关1：趋势方向一致性（日线MA5 + 分时均价线斜率）
        var trendGatePass = true;
        string? ma5Dir = null;
        double? vwapSlope = null;
        if (!isLate)
        {
            ma5Dir = dailyKlines != null ? CheckDailyMA5Direction(dailyKlines) : "flat";
            vwapSlope = CalcVWAPSlopeRecent(sorted);
            if (ma5Dir == "down" || vwapSlope < Config.VwapSlopeMin)
                trendGatePass = false;
        }

        // 关2-1：前 N 根区间振幅
        var ampBars = Math.Min(30, sorted.Count);
        var lookbackAmp = CalcLookbackAmplitude(sorted, ampBars);
        var ampGatePass = ampBars < 10 || lookbackAmp >= Config.LookbackAmpMin;

        // 趋势关改为软扣分
        var trendPenalty = trendGatePass ? 1.0 : 0.7;

        LastFilterInfo = new()
        {
            ["trendGate"] = trendGatePass ? "通过" : "不通过",
            ["ampGate"] = ampGatePass ? "通过" : "不通过",
            ["lookbackAmp"] = $"{lookbackAmp:F3}%",
            ["vwapSlope"] = vwapSlope?.ToString("F5") ?? "N/A",
            ["ma5Dir"] = ma5Dir ?? "N/A(无日K)",
            ["amplitude"] = $"{amplitude:F3}%",
            ["position"] = position.ToString("F2"),
            ["totalSignals"] = 0
        };

        var signalDefs = new List<SignalDef>();

        // 1. VWAP_DIP
        if (!isLate && ampGatePass)
        {
            var dip = DetectVwapDip(sorted, currentPrice);
            if (dip != null && dip.DeviationPct >= Config.MorphDepthMin)
            {
                signalDefs.Add(new() { Type = VWAP_DIP, MorphDepth = dip.DeviationPct, Penalty = trendPenalty,
                    Details = dip });
            }
        }

        // 2. W_BOTTOM
        if (!isLate && ampGatePass)
        {
            var wb = DetectWBottom(sorted, currentPrice);
            if (wb != null && wb.NeckDepthPct >= Config.MorphDepthMin)
            {
                signalDefs.Add(new() { Type = W_BOTTOM, MorphDepth = wb.NeckDepthPct, Penalty = trendPenalty,
                    Details = wb });
            }
        }

        // 3. PANIC_BUY
        if (!isLate && ampGatePass)
        {
            var pb = DetectPanicBuy(sorted, currentPrice);
            if (pb != null && pb.DropPct >= Config.MorphDepthMin)
            {
                signalDefs.Add(new() { Type = PANIC_BUY, MorphDepth = pb.DropPct, Penalty = trendPenalty,
                    Details = pb });
            }
        }

        // 4. TAIL_BUY
        if (isLate)
        {
            var lateAmp = CalcLookbackAmplitude(sorted, 20);
            if (lateAmp >= 0.15 && lateAmp <= Config.TailBuyAmplitude * 2.5)
            {
                var tb = DetectTailBuy(sorted, currentPrice, position);
                if (tb != null)
                    signalDefs.Add(new() { Type = TAIL_BUY, MorphDepth = 0.5, Details = tb });
            }
        }

        // 5. REVERSAL_KLINE
        if (!isLate && ampGatePass)
        {
            var rk = DetectReversalKline(sorted, currentPrice, position);
            if (rk != null)
            {
                var morphDepth = Math.Max(
                    rk.LowerShadow / Math.Max(1, currentPrice) * 100,
                    rk.BodySize / Math.Max(1, currentPrice) * 100);
                if (morphDepth >= Config.MorphDepthMin * 0.8)
                {
                    signalDefs.Add(new() { Type = REVERSAL_KLINE, MorphDepth = morphDepth, Penalty = trendPenalty,
                        Details = rk });
                }
            }
        }

        // 关3：共振倍率 + 评分
        var totalSignals = signalDefs.Count;
        LastFilterInfo["totalSignals"] = totalSignals;
        LastFilterInfo["signalTypes"] = signalDefs.Select(s => s.Type).ToList();

        var signals = new List<BuySignalResult>();
        foreach (var def in signalDefs)
        {
            var raw = CalcScore(def.Type, position, isLate, def.Details);
            double mul;
            if (totalSignals >= 2) mul = Config.ResonanceMultiSignalMul;
            else if (def.Type == REVERSAL_KLINE) mul = Config.ResonanceReversalKlineMul;
            else mul = Config.ResonanceSingleMultiplier;

            var penalty = def.Penalty ?? 1.0;
            var score = (int)JsMath.JsRound(raw * mul * penalty);

            if (score >= Config.MinScore)
            {
                signals.Add(new()
                {
                    Type = def.Type,
                    Label = SignalLabels.GetValueOrDefault(def.Type, def.Type),
                    Score = score,
                    Price = currentPrice,
                    Time = lastSnap.SnapshotAt,
                    MorphDepth = def.MorphDepth,
                    Details = def.Details
                });
            }
        }

        LastFilterInfo["passedScore"] = signals.Count;
        LastFilterInfo["minScore"] = Config.MinScore;
        if (totalSignals > 0 && signals.Count == 0)
            LastFilterInfo["reason"] = "评分不足";

        signals.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (signals.Count > 0)
            planState.LastBuySignalTime = lastSnap.SnapshotAt;

        return signals;
    }

    // ============ 信号1: VWAP_DIP 均价线回踩 ============
    public VwapDipResult? DetectVwapDip(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        var n = snapshots.Count;
        if (n < 5) return null;

        var avgPrice = snapshots[n - 1].AvgPrice;
        if (avgPrice <= 0) return null;

        var deviationPct = ((avgPrice - currentPrice) / avgPrice) * 100;
        if (deviationPct < Config.DipToVwapMin) return null;
        if (deviationPct > Config.DipToVwapThreshold) return null;

        var recent = snapshots.Skip(Math.Max(0, n - 3)).ToList();
        var belowCount = recent.Count(s => s.Price < s.AvgPrice);
        if (belowCount < Config.DipBelowConfirm) return null;

        var prev10 = snapshots.Skip(Math.Max(0, n - 11)).Take(Math.Max(0, n - 1 - Math.Max(0, n - 11))).ToList();
        var prevAvgVol = AvgVolume(prev10);
        var currVol = GetIntervalVolume(snapshots[n - 1]);
        if (prevAvgVol > 0 && currVol > prevAvgVol * Config.DipVolumeShrink) return null;

        if (currentPrice <= snapshots[n - 2].Price) return null;

        return new()
        {
            AvgPrice = avgPrice,
            DeviationPct = deviationPct,
            VolumeRatio = prevAvgVol > 0 ? currVol / prevAvgVol : 0
        };
    }

    // ============ 信号2: W_BOTTOM 分时W底 ============
    public WBottomResult? DetectWBottom(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        var n = snapshots.Count;
        if (n < Config.WBottomMinSpan + 3) return null;

        var window = snapshots.Skip(Math.Max(0, n - Config.WBottomMaxSpan - 5)).ToList();
        var wLen = window.Count;

        // 找所有局部低点
        var lows = new List<(int Index, double Price, double Vol)>();
        for (var i = 1; i < wLen - 1; i++)
        {
            var pi = window[i].Price;
            if (pi < window[i - 1].Price && pi < window[i + 1].Price)
                lows.Add((i, pi, GetIntervalVolume(window[i])));
        }
        if (lows.Count < 2) return null;

        var left = lows[^2];
        var right = lows[^1];
        var span = right.Index - left.Index;
        if (span < Config.WBottomMinSpan) return null;

        var heightDiffPct = Math.Abs(right.Price - left.Price) / Math.Min(left.Price, right.Price) * 100;
        if (heightDiffPct > Config.WBottomHeightDiff) return null;

        // 颈线 = 两底之间的高点
        var neckPrice = 0.0;
        for (var i = left.Index + 1; i < right.Index; i++)
            if (window[i].Price > neckPrice) neckPrice = window[i].Price;
        if (neckPrice <= 0) return null;

        var minBottom = Math.Min(left.Price, right.Price);
        var neckDepthPct = ((neckPrice - minBottom) / minBottom) * 100;
        if (neckDepthPct < Config.WBottomNeckMinPct) return null;

        // 右底缩量
        if (left.Vol > 0 && right.Vol > left.Vol * Config.WBottomRightVolShrink) return null;

        // 当前价突破颈线
        if (currentPrice <= neckPrice) return null;

        return new()
        {
            LeftBottomPrice = left.Price,
            RightBottomPrice = right.Price,
            NeckPrice = neckPrice,
            NeckDepthPct = neckDepthPct,
            HeightDiffPct = heightDiffPct
        };
    }

    // ============ 信号3: PANIC_BUY 急跌缩量 ============
    public PanicBuyResult? DetectPanicBuy(List<IntradaySnapshot> snapshots, double currentPrice)
    {
        var n = snapshots.Count;
        if (n < Config.PanicDropSpan + 1) return null;

        var span = Config.PanicDropSpan;
        var startPrice = snapshots[n - 1 - span].Price;
        var dropPct = ((startPrice - currentPrice) / startPrice) * 100;
        if (dropPct < Config.PanicDropThreshold) return null;

        // 最近3根缩量
        var recent3 = snapshots.Skip(Math.Max(0, n - 3)).ToList();
        var prevSeg = snapshots.Skip(Math.Max(0, n - 1 - span)).Take(Math.Max(0, n - 3 - Math.Max(0, n - 1 - span))).ToList();
        var recentAvg = AvgVolume(recent3);
        var prevAvg = AvgVolume(prevSeg);
        if (prevAvg > 0 && recentAvg > prevAvg * Config.PanicVolumeShrink) return null;

        // 当前根下影线明显
        var curr = snapshots[n - 1];
        var open = curr.Open != 0 ? curr.Open : curr.Price;
        var close = curr.Price;
        var low = curr.Low != 0 ? curr.Low : close;
        var body = Math.Abs(close - open);
        var lowerShadow = Math.Min(open, close) - low;
        if (body <= 0 || lowerShadow < body * Config.PanicLowerShadowRatio) return null;

        return new()
        {
            StartPrice = startPrice,
            DropPct = dropPct,
            RecentAvgVol = recentAvg,
            PrevAvgVol = prevAvg,
            VolumeRatio = prevAvg > 0 ? recentAvg / prevAvg : 0
        };
    }

    // ============ 信号4: TAIL_BUY 尾盘回补 ============
    public TailBuyResult? DetectTailBuy(List<IntradaySnapshot> snapshots, double currentPrice, double position)
    {
        if (position > Config.TailBuyPosition) return null;

        var n = snapshots.Count;
        if (n < 10) return null;

        // 最近5根横盘
        var recent5 = snapshots.Skip(n - 5).ToList();
        var hi = double.MinValue; var lo = double.MaxValue;
        foreach (var s in recent5)
        {
            var h = s.High != 0 ? s.High : s.Price;
            var l = s.Low != 0 ? s.Low : s.Price;
            if (h > hi) hi = h;
            if (l < lo) lo = l;
        }
        if (lo <= 0) return null;
        var amplitudePct = ((hi - lo) / lo) * 100;
        if (amplitudePct > Config.TailBuyAmplitude) return null;

        // 缩量
        var recent5Avg = AvgVolume(recent5);
        var prev5 = snapshots.Skip(Math.Max(0, n - 10)).Take(Math.Max(0, n - 5 - Math.Max(0, n - 10))).ToList();
        var prev5Avg = AvgVolume(prev5);
        if (prev5Avg > 0 && recent5Avg > prev5Avg * Config.TailBuyVolShrink) return null;

        return new()
        {
            Position = position,
            AmplitudePct = amplitudePct,
            RecentAvgVol = recent5Avg,
            PrevAvgVol = prev5Avg,
            VolumeRatio = prev5Avg > 0 ? recent5Avg / prev5Avg : 0
        };
    }

    // ============ 信号5: REVERSAL_KLINE 分时反转K线 ============
    public ReversalKlineResult? DetectReversalKline(List<IntradaySnapshot> snapshots, double currentPrice, double position)
    {
        if (position > Config.ReversalPositionMax) return null;

        var n = snapshots.Count;
        if (n < 6) return null;

        var curr = snapshots[n - 1];
        var prev = snapshots[n - 2];

        var cOpen = curr.Open != 0 ? curr.Open : curr.Price;
        var cClose = curr.Price;
        var cHigh = curr.High != 0 ? curr.High : cClose;
        var cLow = curr.Low != 0 ? curr.Low : cClose;
        var cBody = Math.Abs(cClose - cOpen);
        var cLowerShadow = Math.Min(cOpen, cClose) - cLow;
        var cUpperShadow = cHigh - Math.Max(cOpen, cClose);

        var pOpen = prev.Open != 0 ? prev.Open : prev.Price;
        var pClose = prev.Price;

        string? pattern = null;

        // Pinbar
        if (cBody > 0 && cLowerShadow >= cBody * Config.ReversalPinbarShadowRatio && cUpperShadow <= cBody * 0.5)
            pattern = "pinbar";
        // 锤头线
        else if (cBody > 0 && cLowerShadow >= cBody * Config.ReversalHammerShadowRatio)
            pattern = "hammer";
        // 看涨吞没
        else if (cClose > cOpen && pClose < pOpen && cClose >= pOpen && cOpen <= pClose)
        {
            var currBody = cClose - cOpen;
            var prevBody = pOpen - pClose;
            if (currBody >= prevBody * Config.ReversalEngulfRatio)
                pattern = "engulfing";
        }

        if (pattern == null) return null;

        // 缩量
        var prev5 = snapshots.Skip(Math.Max(0, n - 6)).Take(Math.Max(0, n - 1 - Math.Max(0, n - 6))).ToList();
        var prev5Avg = AvgVolume(prev5);
        var currVol = GetIntervalVolume(curr);
        if (prev5Avg > 0 && currVol > prev5Avg) return null;

        return new()
        {
            Pattern = pattern,
            Position = position,
            BodySize = cBody,
            LowerShadow = cLowerShadow,
            UpperShadow = cUpperShadow,
            VolumeRatio = prev5Avg > 0 ? currVol / prev5Avg : 0
        };
    }

    // ============ 评分系统 ============
    public int CalcScore(string type, double position, bool isLateSession, object? details)
    {
        var baseScore = 50;
        var weight = Config.SignalWeights.GetValueOrDefault(type, 15);
        var score = baseScore + weight;

        // 位置系数
        if (position < 0.3) score += 15;
        else if (position > 0.8) score -= 20;

        // 时段调整
        if (isLateSession && type != TAIL_BUY) score -= 15;

        // 信号强度加成
        switch (type)
        {
            case var t when t == VWAP_DIP && details is VwapDipResult d1 && d1.DeviationPct > 0.4:
                score += 5; break;
            case var t when t == PANIC_BUY && details is PanicBuyResult d2 && d2.DropPct > 2:
                score += 5; break;
            case var t when t == W_BOTTOM && details is WBottomResult d3 && d3.NeckDepthPct > 0.8:
                score += 5; break;
            case var t when t == REVERSAL_KLINE && details is ReversalKlineResult d4 && d4.Pattern == "pinbar":
                score += 5; break;
        }

        return (int)JsMath.JsRound((double)score);
    }
}

// ============ 配置类 ============

public class BuyConfig
{
    // 通用
    public double MinAmplitude { get; set; } = 0.4;
    public string TailBuyStartTime { get; set; } = "14:30";
    public int MinScore { get; set; } = 60;

    // 关1：趋势方向
    public double Ma5FlatTolerance { get; set; } = 1.0;
    public double VwapSlopeMin { get; set; } = -0.003;

    // 关2：振幅
    public double LookbackAmpMin { get; set; } = 0.3;
    public double MorphDepthMin { get; set; } = 0.1;

    // VWAP_DIP
    public double DipToVwapMin { get; set; } = 0.08;
    public double DipToVwapThreshold { get; set; } = 0.5;
    public double DipVolumeShrink { get; set; } = 0.8;
    public int DipBelowConfirm { get; set; } = 2;

    // W_BOTTOM
    public double WBottomHeightDiff { get; set; } = 0.7;
    public double WBottomNeckMinPct { get; set; } = 0.2;
    public int WBottomMinSpan { get; set; } = 8;
    public int WBottomMaxSpan { get; set; } = 30;
    public double WBottomRightVolShrink { get; set; } = 0.85;

    // PANIC_BUY
    public double PanicDropThreshold { get; set; } = 1.0;
    public int PanicDropSpan { get; set; } = 4;
    public double PanicVolumeShrink { get; set; } = 0.9;
    public double PanicLowerShadowRatio { get; set; } = 0.35;

    // TAIL_BUY
    public double TailBuyPosition { get; set; } = 0.4;
    public double TailBuyAmplitude { get; set; } = 0.8;
    public double TailBuyVolShrink { get; set; } = 0.7;

    // REVERSAL_KLINE
    public double ReversalPinbarShadowRatio { get; set; } = 1.5;
    public double ReversalEngulfRatio { get; set; } = 1.2;
    public double ReversalPositionMax { get; set; } = 0.5;
    public double ReversalHammerShadowRatio { get; set; } = 1.5;

    // 信号权重
    public Dictionary<string, int> SignalWeights { get; set; } = new()
    {
        [BuyPointDetectorService.VWAP_DIP] = 35,
        [BuyPointDetectorService.W_BOTTOM] = 30,
        [BuyPointDetectorService.PANIC_BUY] = 30,
        [BuyPointDetectorService.TAIL_BUY] = 20,
        [BuyPointDetectorService.REVERSAL_KLINE] = 25
    };

    // 关3：共振倍率
    public double ResonanceSingleMultiplier { get; set; } = 0.7;
    public double ResonanceReversalKlineMul { get; set; } = 0.5;
    public double ResonanceMultiSignalMul { get; set; } = 1.1;
}

// ============ 数据模型 ============

public class SignalDef
{
    public string Type { get; set; } = "";
    public double MorphDepth { get; set; }
    public double? Penalty { get; set; }
    public object? Details { get; set; }
}

public class BuySignalResult
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public int Score { get; set; }
    public double Price { get; set; }
    public DateTime? Time { get; set; }
    public double MorphDepth { get; set; }
    public object? Details { get; set; }
}

// 信号检测结果
public class VwapDipResult
{
    public double AvgPrice { get; set; }
    public double DeviationPct { get; set; }
    public double VolumeRatio { get; set; }
}

public class WBottomResult
{
    public double LeftBottomPrice { get; set; }
    public double RightBottomPrice { get; set; }
    public double NeckPrice { get; set; }
    public double NeckDepthPct { get; set; }
    public double HeightDiffPct { get; set; }
}

public class PanicBuyResult
{
    public double StartPrice { get; set; }
    public double DropPct { get; set; }
    public double RecentAvgVol { get; set; }
    public double PrevAvgVol { get; set; }
    public double VolumeRatio { get; set; }
}

public class TailBuyResult
{
    public double Position { get; set; }
    public double AmplitudePct { get; set; }
    public double RecentAvgVol { get; set; }
    public double PrevAvgVol { get; set; }
    public double VolumeRatio { get; set; }
}

public class ReversalKlineResult
{
    public string Pattern { get; set; } = "";
    public double Position { get; set; }
    public double BodySize { get; set; }
    public double LowerShadow { get; set; }
    public double UpperShadow { get; set; }
    public double VolumeRatio { get; set; }
}
