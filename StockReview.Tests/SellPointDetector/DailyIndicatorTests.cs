// 日线技术指标纯函数单元测试 - MACD(12,26,9)/KDJ(9,3,3)/BOLL(20,2)（外部资源分析报告建议2配套）
// 验证口径：MACD 中国惯例柱=(DIF-DEA)×2；KDJ SMA 1/3 平滑初值 50；BOLL 总体标准差(÷n)
using System;
using System.Collections.Generic;
using System.Linq;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DailyIndicatorTests
{
    private static List<KLineData> BuildDaily(int n, double start, double step, double range = 0.3)
    {
        var list = new List<KLineData>();
        for (var i = 0; i < n; i++)
        {
            var c = (decimal)(start + i * step);
            list.Add(new KLineData
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = c, Close = c,
                High = c + (decimal)range, Low = c - (decimal)range,
                Volume = 100000
            });
        }
        return list;
    }

    private static List<KLineData> BuildFlat(int n, double price)
    {
        var list = new List<KLineData>();
        for (var i = 0; i < n; i++)
        {
            var c = (decimal)price;
            list.Add(new KLineData
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = c, Close = c, High = c, Low = c, Volume = 100000
            });
        }
        return list;
    }

    // ===== CalculateDailyMACD =====

    [Fact]
    public void MACD_NullForInsufficientBars()
    {
        Assert.Null(SellPointDetectorService.CalculateDailyMACD(BuildDaily(34, 10, 0.1)));
    }

    [Fact]
    public void MACD_MinimumBars_ReturnsValue()
    {
        var r = SellPointDetectorService.CalculateDailyMACD(BuildDaily(35, 10, 0.1));
        Assert.NotNull(r);
    }

    [Fact]
    public void MACD_Uptrend_DifAboveDea()
    {
        var r = SellPointDetectorService.CalculateDailyMACD(BuildDaily(60, 10, 0.5));
        Assert.NotNull(r);
        Assert.True(r!.Value.Dif > r.Value.Dea,
            $"Uptrend DIF({r.Value.Dif:F3}) should be above DEA({r.Value.Dea:F3})");
    }

    [Fact]
    public void MACD_Downtrend_DifBelowDea()
    {
        var r = SellPointDetectorService.CalculateDailyMACD(BuildDaily(60, 40, -0.5));
        Assert.NotNull(r);
        Assert.True(r!.Value.Dif < r.Value.Dea,
            $"Downtrend DIF({r.Value.Dif:F3}) should be below DEA({r.Value.Dea:F3})");
    }

    [Fact]
    public void MACD_BarIdentity_TwiceDifMinusDea()
    {
        var r = SellPointDetectorService.CalculateDailyMACD(BuildDaily(50, 10, 0.3));
        Assert.NotNull(r);
        var (dif, dea, bar) = r!.Value;
        Assert.Equal((dif - dea) * 2, bar, 6);
    }

    [Fact]
    public void MACD_FlatData_DifAndDeaNearZero()
    {
        var r = SellPointDetectorService.CalculateDailyMACD(BuildFlat(60, 10));
        Assert.NotNull(r);
        Assert.True(Math.Abs(r!.Value.Dif) < 1e-6 && Math.Abs(r.Value.Dea) < 1e-6,
            $"Flat DIF({r.Value.Dif:F6})/DEA({r.Value.Dea:F6}) should be ~0");
    }

    // ===== CalculateDailyKDJ =====

    [Fact]
    public void KDJ_NullForInsufficientBars()
    {
        Assert.Null(SellPointDetectorService.CalculateDailyKDJ(BuildDaily(8, 10, 0.1)));
    }

    [Fact]
    public void KDJ_Uptrend_KAboveDAndJHigh()
    {
        var r = SellPointDetectorService.CalculateDailyKDJ(BuildDaily(30, 10, 0.5));
        Assert.NotNull(r);
        var (k, d, j) = r!.Value;
        Assert.True(k > d, $"Uptrend K({k:F1}) should be above D({d:F1})");
        Assert.True(j > k, $"Uptrend J({j:F1}) should be above K({k:F1})");
    }

    [Fact]
    public void KDJ_Downtrend_KBelowDAndJLow()
    {
        var r = SellPointDetectorService.CalculateDailyKDJ(BuildDaily(30, 40, -0.5));
        Assert.NotNull(r);
        var (k, d, j) = r!.Value;
        Assert.True(k < d, $"Downtrend K({k:F1}) should be below D({d:F1})");
        Assert.True(j < k, $"Downtrend J({j:F1}) should be below K({k:F1})");
    }

    [Fact]
    public void KDJ_KAndDAlwaysInRange0To100()
    {
        // K/D 是 RSV 的 SMA 平滑，RSV∈[0,100] → K/D 恒在 [0,100]；J=3K-2D 允许越界
        var r = SellPointDetectorService.CalculateDailyKDJ(BuildDaily(30, 10, 0.5));
        Assert.NotNull(r);
        Assert.True(r!.Value.K is >= 0 and <= 100, $"K={r.Value.K:F1} out of [0,100]");
        Assert.True(r.Value.D is >= 0 and <= 100, $"D={r.Value.D:F1} out of [0,100]");
    }

    [Fact]
    public void KDJ_FlatData_KD50AndJ50()
    {
        // High==Low 时 RSV 取中性值 50，SMA 平滑后 K=D=J=50
        var r = SellPointDetectorService.CalculateDailyKDJ(BuildFlat(30, 10));
        Assert.NotNull(r);
        var (k, d, j) = r!.Value;
        Assert.Equal(50, k, 4);
        Assert.Equal(50, d, 4);
        Assert.Equal(50, j, 4);
    }

    // ===== CalculateDailyBOLL =====

    [Fact]
    public void BOLL_NullForInsufficientBars()
    {
        Assert.Null(SellPointDetectorService.CalculateDailyBOLL(BuildDaily(19, 10, 0.1)));
    }

    [Fact]
    public void BOLL_FlatData_AllBandsEqual()
    {
        var r = SellPointDetectorService.CalculateDailyBOLL(BuildFlat(30, 10));
        Assert.NotNull(r);
        var (mid, upper, lower) = r!.Value;
        Assert.Equal(10, mid, 6);
        Assert.Equal(upper, lower, 6);
    }

    [Fact]
    public void BOLL_SymmetricData_BandsAroundMid()
    {
        // 围绕 10 对称震荡 → Mid≈10 且 Upper-Mid == Mid-Lower
        var klines = new List<KLineData>();
        for (var i = 0; i < 30; i++)
        {
            var c = (decimal)(i % 2 == 0 ? 11 : 9);
            klines.Add(new KLineData
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = c, Close = c, High = c, Low = c, Volume = 100000
            });
        }
        var r = SellPointDetectorService.CalculateDailyBOLL(klines);
        Assert.NotNull(r);
        var (mid, upper, lower) = r!.Value;
        Assert.Equal(10, mid, 6);
        Assert.Equal(upper - mid, mid - lower, 6);
    }

    [Fact]
    public void BOLL_HigherVolatility_WiderBand()
    {
        // BOLL 只用 Close：构造 Close 波动幅度不同的两组（±0.1 vs ±0.75）
        static List<KLineData> BuildOsc(int n, double amp)
        {
            var list = new List<KLineData>();
            for (var i = 0; i < n; i++)
            {
                var c = (decimal)(i % 2 == 0 ? 10 : 10 + amp * 2);
                list.Add(new KLineData
                {
                    Date = DateTime.MinValue.AddDays(i),
                    Open = c, Close = c, High = c + 0.3m, Low = c - 0.3m, Volume = 100000
                });
            }
            return list;
        }
        var calm = SellPointDetectorService.CalculateDailyBOLL(BuildOsc(30, 0.1));
        var wild = SellPointDetectorService.CalculateDailyBOLL(BuildOsc(30, 0.75));
        Assert.NotNull(calm);
        Assert.NotNull(wild);
        var calmWidth = calm!.Value.Upper - calm.Value.Lower;
        var wildWidth = wild!.Value.Upper - wild.Value.Lower;
        Assert.True(wildWidth > calmWidth,
            $"Wild band({wildWidth:F2}) should be wider than calm({calmWidth:F2})");
    }

    [Fact]
    public void BOLL_PriceWithinBandForTrendingData()
    {
        // 等差上涨的收盘价应在轨道内（pos≈0.9 贴轨但不破轨）
        var klines = BuildDaily(60, 10, 0.5);
        var last = (double)klines[^1].Close;
        var r = SellPointDetectorService.CalculateDailyBOLL(klines);
        Assert.NotNull(r);
        var (_, upper, lower) = r!.Value;
        Assert.True(last <= upper + 1e-6 && last >= lower - 1e-6,
            $"Close({last:F2}) should be within [{lower:F2},{upper:F2}]");
    }
}
