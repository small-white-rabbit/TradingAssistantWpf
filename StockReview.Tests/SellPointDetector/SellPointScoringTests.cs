// SellPointDetector 评分系统单元测试 - 验证 GetBaseWeight/GetSignalWeight/CalculateTimeDensity
using System;
using System.Collections.Generic;
using System.IO;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class SellPointScoringTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly SellPointDetectorService _svc;
    private readonly string _tmp;

    public SellPointScoringTests()
    {
        _db = new DatabaseService();
        _tmp = Path.Combine(Path.GetTempPath(), "scoring_test_" + Guid.NewGuid().ToString("N"));
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

    // ===== GetBaseWeight =====

    [Theory]
    [InlineData(SignalTypes.SurgePullback, 10)]
    [InlineData(SignalTypes.VolumeStagnant, 20)]
    [InlineData(SignalTypes.VwapBreakdown, 15)]
    [InlineData(SignalTypes.DoubleTop, 15)]
    [InlineData(SignalTypes.AtrStopLoss, 25)]
    public void GetBaseWeight_ReturnsPositiveForKnownTypes(string type, int minExpected)
    {
        var w = _svc.GetBaseWeight(type);
        Assert.True(w >= minExpected, $"Base weight for {type} should be >= {minExpected}, got {w}");
    }

    [Fact]
    public void GetBaseWeight_UnknownType_ReturnsDefault()
    {
        var w = _svc.GetBaseWeight("nonexistent_type");
        Assert.Equal(10, w); // 默认权重
    }

    // ===== GetSignalWeight =====

    [Fact]
    public void GetSignalWeight_AppliesMultiplier()
    {
        var weight = _svc.GetSignalWeight(SignalTypes.SurgePullback);
        Assert.True(weight > 0, $"Signal weight should be positive, got {weight}");
    }

    // ===== CalculateTimeDensity =====

    [Fact]
    public void CalculateTimeDensity_SingleSignal_ReturnsZero()
    {
        var signals = new List<SellPointSignal>
        {
            new() { Type = SignalTypes.SurgePullback, Timestamp = 1000 }
        };
        var density = _svc.CalculateTimeDensity(signals, 300000);
        Assert.Equal(0, density);
    }

    [Fact]
    public void CalculateTimeDensity_MultipleSignalsSameTime_HighDensity()
    {
        // TimeDensity 用 now - timestamp < windowMs 过滤，时间戳需接近当前
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signals = new List<SellPointSignal>
        {
            new() { Type = SignalTypes.SurgePullback, Timestamp = now - 1000 },
            new() { Type = SignalTypes.VolumeStagnant, Timestamp = now - 500 },
            new() { Type = SignalTypes.VwapBreakdown, Timestamp = now },
        };
        var density = _svc.CalculateTimeDensity(signals, 300000);
        Assert.True(density > 0, $"Multiple signals in window should have positive density, got {density}");
    }

    [Fact]
    public void CalculateTimeDensity_SignalsSpreadOut_LowDensity()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signals = new List<SellPointSignal>
        {
            new() { Type = SignalTypes.SurgePullback, Timestamp = now }, // 当前
            new() { Type = SignalTypes.VolumeStagnant, Timestamp = now - 600000 }, // 10 分钟前（超出窗口）
        };
        var density = _svc.CalculateTimeDensity(signals, 300000);
        // 只有一个信号在窗口内，不满足 >= 2，density = 0
        Assert.Equal(0, density);
    }
}
