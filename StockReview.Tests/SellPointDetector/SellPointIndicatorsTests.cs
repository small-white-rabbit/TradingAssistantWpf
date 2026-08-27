// SellPointDetector 指标计算单元测试 - 验证 ATR/RSI/WR/MFI 纯函数
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class SellPointIndicatorsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly SellPointDetectorService _svc;
    private readonly string _tmp;

    public SellPointIndicatorsTests()
    {
        _db = new DatabaseService();
        _tmp = Path.Combine(Path.GetTempPath(), "ind_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _db.SetDataDir(_tmp);
        _db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        _svc = new SellPointDetectorService(_db, market);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static IntradaySnapshot Mk(double price, double high, double low, double vol = 100, double avg = 0) => new()
    {
        SnapshotAt = DateTime.MinValue,
        Price = price, Open = price, High = high, Low = low,
        AvgPrice = avg > 0 ? avg : price, Volume = vol, PreClose = price, VolumeReliable = true
    };

    private static List<IntradaySnapshot> BuildTrending(int n, double start, double step)
    {
        var list = new List<IntradaySnapshot>();
        for (var i = 0; i < n; i++)
        {
            var p = start + i * step;
            list.Add(Mk(p, p + 0.5, p - 0.5));
        }
        return list;
    }

    // ===== CalculateATR =====

    [Fact]
    public void CalculateATR_ReturnsPositiveForVolatileData()
    {
        var snaps = BuildTrending(25, 10, 0.5);
        var atr = _svc.CalculateATR(snaps, 14);
        Assert.True(atr > 0, $"ATR should be positive, got {atr}");
    }

    [Fact]
    public void CalculateATR_ZeroForFlatData()
    {
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < 20; i++) snaps.Add(Mk(10, 10, 10));
        var atr = _svc.CalculateATR(snaps, 14);
        Assert.Equal(0, atr);
    }

    [Fact]
    public void CalculateATR_HigherVolatility_HigherATR()
    {
        var lowVol = BuildTrending(25, 10, 0.1);
        var highVol = BuildTrending(25, 10, 1.0);
        var atrLow = _svc.CalculateATR(lowVol, 14);
        var atrHigh = _svc.CalculateATR(highVol, 14);
        Assert.True(atrHigh > atrLow, $"High vol ATR ({atrHigh}) should > low vol ATR ({atrLow})");
    }

    // ===== CalculateRSI =====

    [Fact]
    public void CalculateRSI_Uptrend_Above50()
    {
        var uptrend = BuildTrending(20, 10, 0.5);
        var rsi = _svc.CalculateRSI(uptrend, 14);
        Assert.True(rsi > 50, $"Uptrend RSI should be > 50, got {rsi}");
    }

    [Fact]
    public void CalculateRSI_Downtrend_Below50()
    {
        var downtrend = BuildTrending(20, 20, -0.5);
        var rsi = _svc.CalculateRSI(downtrend, 14);
        Assert.True(rsi < 50, $"Downtrend RSI should be < 50, got {rsi}");
    }

    [Fact]
    public void CalculateRSI_FlatData_InRange()
    {
        var flat = new List<IntradaySnapshot>();
        for (var i = 0; i < 20; i++) flat.Add(Mk(10, 10, 10));
        var rsi = _svc.CalculateRSI(flat, 14);
        Assert.True(rsi is >= 0 and <= 100, $"RSI should be in [0,100], got {rsi}");
    }

    // ===== CalculateWR =====

    [Fact]
    public void CalculateWR_Uptrend_NearZero()
    {
        // WR 实现：(high-current)/(high-low)*-100，上升趋势 current 接近 high → WR 接近 0
        var uptrend = BuildTrending(20, 10, 0.5);
        var wr = _svc.CalculateWR(uptrend, 14);
        Assert.True(wr > -50, $"Uptrend WR should be near 0 (above -50), got {wr}");
    }

    // ===== CalculateMFI =====

    [Fact]
    public void CalculateMFI_ReturnsInRange()
    {
        var snaps = BuildTrending(20, 10, 0.5);
        var mfi = _svc.CalculateMFI(snaps, 14);
        Assert.True(mfi is >= 0 and <= 100, $"MFI should be in [0,100], got {mfi}");
    }

    // ===== CheckOverboughtResonance =====

    [Fact]
    public void CheckOverboughtResonance_StrongUptrend_ReturnsResult()
    {
        var strong = BuildTrending(25, 10, 1.0);
        var res = _svc.CheckOverboughtResonance(strong);
        Assert.NotNull(res);
        Assert.True(res!.ResonanceCount >= 0);
    }

    // ===== GetPositionFactor =====

    [Fact]
    public void GetPositionFactor_PriceAtMidpoint_ReturnsInRange()
    {
        var klines = new List<KLineData>();
        for (var i = 0; i < 30; i++)
            klines.Add(new KLineData { Date = DateTime.MinValue.AddDays(i), High = 15, Low = 5, Close = 10 });

        var factor = _svc.GetPositionFactor(klines, 10);
        Assert.True(factor is >= 0 and <= 1, $"Position factor should be [0,1], got {factor}");
    }

    // ===== FindPeaksRobust =====

    [Fact]
    public void FindPeaksRobust_FindsLocalMaxima()
    {
        var prices = new List<double> { 10, 12, 15, 12, 10, 13, 16, 13, 10 };
        var peaks = _svc.FindPeaksRobust(prices, radius: 1, minRelHeight: 0.1);
        Assert.True(peaks.Count >= 1, "Should find at least one peak");
    }

    [Fact]
    public void FindPeaksRobust_FlatData_NoPeaks()
    {
        var prices = Enumerable.Repeat(10.0, 10).ToList();
        var peaks = _svc.FindPeaksRobust(prices, radius: 1, minRelHeight: 0.2);
        Assert.Empty(peaks);
    }
}
