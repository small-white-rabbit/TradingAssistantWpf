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

    // ==================== 技术指标计算 ====================

    /// <summary>
    /// 计算简易 ATR（平均真实波幅）
    /// </summary>
    public double CalculateATR(List<IntradaySnapshot> snapshots, int period = 20)
    {
        if (snapshots.Count < 3) return 0;
        var recent = snapshots.Skip(snapshots.Count - Math.Min(period, snapshots.Count)).ToList();
        var ranges = new List<double>();
        for (var i = 1; i < recent.Count; i++)
        {
            var high = Math.Max(recent[i].Price, recent[i - 1].Price);
            var low = Math.Min(recent[i].Price, recent[i - 1].Price);
            ranges.Add(high - low);
        }
        return ranges.Count > 0 ? ranges.Sum() / ranges.Count : 0;
    }

    /// <summary>
    /// 计算简单移动均线（基于日内快照价格）
    /// </summary>
    public double? CalculateMA(List<IntradaySnapshot> snapshots, int period)
    {
        if (snapshots.Count < period) return null;
        var slice = snapshots.Skip(snapshots.Count - period).Take(period).ToList();
        return slice.Sum(s => s.Price) / period;
    }

    /// <summary>
    /// 计算真实N日均价（基于日K线收盘价）
    /// </summary>
    public static double? CalculateDailyMA(List<KLineData> dailyKlines, int period)
    {
        if (dailyKlines == null || dailyKlines.Count < period) return null;
        var slice = dailyKlines.Skip(dailyKlines.Count - period).Take(period).ToList();
        return slice.Sum(k => (double)k.Close) / period;
    }

    /// <summary>
    /// 计算RSI（Wilder's RSI）
    /// </summary>
    public double CalculateRSI(List<IntradaySnapshot> snapshots, int period = 14)
    {
        if (snapshots == null || snapshots.Count < period + 1) return 50;
        var prices = snapshots.Select(s => s.Price).ToList();
        var avgGain = 0.0;
        var avgLoss = 0.0;

        for (var i = 1; i <= period; i++)
        {
            var diff = prices[i] - prices[i - 1];
            if (diff > 0) avgGain += diff;
            else avgLoss += Math.Abs(diff);
        }
        avgGain /= period;
        avgLoss /= period;

        for (var i = period + 1; i < prices.Count; i++)
        {
            var diff = prices[i] - prices[i - 1];
            var gain = diff > 0 ? diff : 0;
            var loss = diff < 0 ? Math.Abs(diff) : 0;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        if (avgLoss == 0) return 100;
        var rs = avgGain / avgLoss;
        return 100 - 100 / (1 + rs);
    }

    /// <summary>
    /// 计算WR（威廉指标）
    /// </summary>
    public double CalculateWR(List<IntradaySnapshot> snapshots, int period = 14)
    {
        if (snapshots == null || snapshots.Count < period) return -50;
        var recent = snapshots.Skip(snapshots.Count - period).Take(period).ToList();
        var prices = recent.Select(s => s.Price).ToList();
        var high = prices.Max();
        var low = prices.Min();
        var current = prices[^1];
        if (high == low) return -50;
        return (high - current) / (high - low) * -100;
    }

    /// <summary>
    /// 计算MFI（资金流向指标）
    /// </summary>
    public double CalculateMFI(List<IntradaySnapshot> snapshots, int period = 14)
    {
        if (snapshots == null || snapshots.Count < period + 1) return 50;
        var recent = snapshots.Skip(snapshots.Count - (period + 1)).Take(period + 1).ToList();
        var posFlow = 0.0;
        var negFlow = 0.0;

        for (var i = 1; i < recent.Count; i++)
        {
            var prevPrice = recent[i - 1].Price;
            var currPrice = recent[i].Price;
            var vol = GetIntervalVolume(recent[i]);
            var moneyFlow = currPrice * vol;

            if (currPrice > prevPrice) posFlow += moneyFlow;
            else if (currPrice < prevPrice) negFlow += moneyFlow;
        }

        if (negFlow == 0) return 100;
        var moneyRatio = posFlow / negFlow;
        return 100 - 100 / (1 + moneyRatio);
    }

    /// <summary>
    /// 检查技术指标共振（RSI/WR/MFI超买共振）
    /// </summary>
    public OverboughtResonance CheckOverboughtResonance(List<IntradaySnapshot> snapshots)
    {
        var rsi = CalculateRSI(snapshots, 14);
        var wr = CalculateWR(snapshots, 14);
        var mfi = CalculateMFI(snapshots, 14);

        var resonanceCount = 0;
        if (rsi >= 70) resonanceCount++;
        if (wr >= -20) resonanceCount++;
        if (mfi >= 80) resonanceCount++;

        return new OverboughtResonance
        {
            IsOverbought = resonanceCount >= 2,
            ResonanceCount = resonanceCount,
            Rsi = rsi,
            Wr = wr,
            Mfi = mfi
        };
    }

    // ==================== 工具方法 ====================
    /// <summary>
    /// 获取市场环境上下文
    /// </summary>
    public MarketContext GetMarketContext(IntradaySnapshot? currentSnapshot)
    {
        var ctx = new MarketContext();
        if (currentSnapshot == null) return ctx;

        var (hour, minute) = GetHourMin(currentSnapshot.SnapshotAt);
        var totalMinutes = hour * 60 + minute;

        ctx.IsMorningOpen = totalMinutes >= 570 && totalMinutes <= 600;
        ctx.IsAfternoonOpen = totalMinutes >= 780 && totalMinutes <= 810;
        ctx.IsLateSession = totalMinutes >= 870;

        var preClose = currentSnapshot.PreClose;
        var price = currentSnapshot.Price;
        if (preClose > 0 && price > 0)
        {
            ctx.ChangePct = (price - preClose) / preClose * 100;
            ctx.IsUpLimit = ctx.ChangePct >= 9.5;
            ctx.IsDownLimit = ctx.ChangePct <= -9.5;
        }
        return ctx;
    }

    /// <summary>
    /// 计算个股位置系数：0=历史低位，1=历史高位
    /// </summary>
    public double GetPositionFactor(List<KLineData>? dailyKlines, double currentPrice)
    {
        if (dailyKlines == null || dailyKlines.Count < 20) return 0.5;
        var closes = dailyKlines.Select(k => (double)k.Close).Where(v => v > 0).ToList();
        if (closes.Count < 20) return 0.5;
        var last20 = closes.Skip(closes.Count - 20).Take(20).ToList();
        var max20 = last20.Max();
        var min20 = last20.Min();
        if (max20 == min20) return 0.5;
        var factor = (currentPrice - min20) / (max20 - min20);
        return Math.Max(0, Math.Min(1, factor));
    }

    /// <summary>
    /// 基于真实时间戳计算线性回归斜率（价格/分钟）
    /// </summary>
    public double CalculateSlopeByTime(List<double> prices, List<DateTime> timestamps)
    {
        if (prices == null || prices.Count < 2 || prices.Count != timestamps.Count) return 0;
        var n = prices.Count;
        var baseTime = timestamps[0];
        var xs = timestamps.Select(t => (t - baseTime).TotalMinutes).ToList();
        var sumX = xs.Sum();
        var sumY = prices.Sum();
        var sumXY = 0.0;
        var sumX2 = 0.0;
        for (var i = 0; i < n; i++) { sumXY += xs[i] * prices[i]; sumX2 += xs[i] * xs[i]; }
        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-10) return 0;
        return (n * sumXY - sumX * sumY) / denom;
    }

    /// <summary>
    /// 计算分时均价线（VWAP）最近斜率（%/分钟）
    /// </summary>
    public double CalculateVWAPSlope(List<IntradaySnapshot> snapshots)
    {
        return CalcVWAPSlopeRaw(snapshots);
    }


    private double CalcVWAPSlopeRaw(List<IntradaySnapshot> snapshots)
    {
        if (snapshots.Count < 10) return 0;
        var recent = snapshots.Skip(snapshots.Count - 10).Take(10).ToList();
        var recentValid = recent.Where(s => s.AvgPrice > 0).ToList();
        if (recentValid.Count < 8) return 0;
        var avgPrices = recentValid.Select(s => s.AvgPrice).ToList();
        var timestamps = recentValid.Select(s => s.SnapshotAt).ToList();
        var slope = CalculateSlopeByTime(avgPrices, timestamps);
        var startAvg = avgPrices[0];
        if (startAvg <= 0) return 0;
        return slope / startAvg * 100;
    }

    /// <summary>
    /// 预计算 analyze 上下文
    /// </summary>
    public AnalyzeContext PrepareAnalyzeCtx(List<IntradaySnapshot> snapshots)
    {
        var prices = new List<double>();
        var dayLow = double.MaxValue;
        var dayHigh = double.MinValue;
        foreach (var s in snapshots)
        {
            var p = s.Price;
            if (double.IsFinite(p) && p > 0)
            {
                prices.Add(p);
                if (p < dayLow) dayLow = p;
                if (p > dayHigh) dayHigh = p;
            }
            if (double.IsFinite(s.High) && s.High > dayHigh) dayHigh = s.High;
            if (double.IsFinite(s.Low) && s.Low > 0 && s.Low < dayLow) dayLow = s.Low;
        }
        if (dayLow == double.MaxValue) dayLow = 0;
        if (dayHigh == double.MinValue) dayHigh = 0;
        var vwapSlope = CalcVWAPSlopeRaw(snapshots);
        var ctx = new AnalyzeContext { Prices = prices, DayLow = dayLow, DayHigh = dayHigh, VwapSlope = vwapSlope };
        return ctx;
    }

    /// <summary>
    /// 检查是否放量
    /// </summary>
    public bool CheckVolumeAmplified(List<IntradaySnapshot> snapshots)
    {
        if (snapshots.Count < 6) return false;
        var current = snapshots[^1];
        if (!current.VolumeReliable) return false;
        var previous = snapshots.Skip(snapshots.Count - 6).Take(5).ToList();
        var avgVolume = previous.Sum(s => GetIntervalVolume(s)) / previous.Count;
        var currentVol = GetIntervalVolume(current);
        return avgVolume > 0 && currentVol > avgVolume * 1.5;
    }

    /// <summary>
    /// 在 endIndex 之前找最近的横盘平台
    /// </summary>
    public PlatformInfo? FindPlatformBefore(List<IntradaySnapshot> snapshots, int endIndex)
    {
        var minBars = _config.DeepDropPlatformMinBars;
        var ampMax = _config.DeepDropPlatformAmplitude;
        var searchEnd = Math.Min(endIndex - 1, snapshots.Count - 1);
        for (var end = searchEnd; end >= minBars - 1; end--)
        {
            var start = end - minBars + 1;
            var segMax = double.MinValue;
            var segMin = double.MaxValue;
            var valid = true;
            for (var i = start; i <= end; i++)
            {
                var p = snapshots[i].Price;
                if (!double.IsFinite(p)) { valid = false; break; }
                if (p > segMax) segMax = p;
                if (p < segMin) segMin = p;
            }
            if (!valid) continue;
            var mid = (segMax + segMin) / 2;
            if (mid <= 0) continue;
            var amp = (segMax - segMin) / mid * 100;
            if (amp < ampMax)
                return new PlatformInfo { Start = start, End = end, Top = segMax, Bottom = segMin };
        }
        return null;
    }

    // ==================== 评分系统 ====================

    /// <summary>
    /// 获取信号基础权重（静态表）
    /// </summary>
}
