// 高乖离回落(high_deviation_pullback) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectHighDeviationPullback。
// 关闭形态相似度（EnablePatternSimilarity=false），验证「峰值扫描 + 乖离度 + 回落」几何核心。
// 与 verify_sell_highDeviation_js.mjs（原 JS 抽方法体 + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectHighDeviationPullbackTests
{
    private static IntradaySnapshot Mk(double price, double avgPrice, DateTime t) => new()
    {
        SnapshotAt = t, Price = price, Open = price, High = price, Low = price,
        AvgPrice = avgPrice,
        Volume = 100, IntervalVolume = 100,
        PreClose = 100.0, VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, Func<int, double> priceFn, double avgPrice)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 9, 30, 0);
        for (var i = 0; i < count; i++)
            list.Add(Mk(priceFn(i), avgPrice, t.AddSeconds(i * 60)));
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "highDev_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp); db.SetDataDir(tmp); db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            TopPatternMinPosition = 0.5,
            TopPatternMaxVwapSlope = 999,
            HighDeviationPct = 1.5,
            HighDeviationPullback = 0.5,
        });
        return svc;
    }

    private static void AssertClose(double e, double a, double tol = 1e-6, string? m = null)
    {
        Assert.True(Math.Abs(e - a) < tol, $"{m ?? "value"}: expected ~{e}, actual {a}");
    }

    // S1: 触发（peakIdx=10 price=107, deviation≈2.88% >= 1.5%, pullback=2.80% >= 0.5%）
    [Fact]
    public void HighDeviationPullback_Fires()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i =>
        {
            if (i < 8) return 100.0 + i;
            if (i <= 9) return 106.0;
            if (i == 10) return 107.0;
            if (i <= 12) return 106.0;
            return 104.0;
        };
        var snaps = Build(30, priceFn, 104.0);
        var sig = svc.DetectHighDeviationPullback(snaps, 104);
        Assert.NotNull(sig);
        Assert.Equal("高乖离回落", sig!.LevelName);
        AssertClose(107.0, sig.GetDouble("peakPrice"), 1e-9);
        AssertClose(104.0, sig.GetDouble("peakAvgPrice"), 1e-9);
        AssertClose(2.8846153846153846, sig.GetDouble("deviation"), 1e-9);
        AssertClose(2.803738317757009, sig.GetDouble("pullback"), 1e-9);
    }

    // S2: 乖离度不够（peak=105, avg=104, deviation=0.96% < 1.5%）
    [Fact]
    public void InsufficientDeviation_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 5 ? 100.0 + i : (i == 10 ? 105.0 : 104.0);
        var snaps = Build(30, priceFn, 104.0);
        var sig = svc.DetectHighDeviationPullback(snaps, 104);
        Assert.Null(sig);
    }

    // S3: 回落不足（peak=107, currentPrice=106.5, pullback=0.467% < 0.5%）
    [Fact]
    public void InsufficientPullback_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i =>
        {
            if (i < 8) return 100.0 + i;
            if (i <= 9) return 106.0;
            if (i == 10) return 107.0;
            if (i <= 12) return 106.0;
            return 104.0;
        };
        var snaps = Build(30, priceFn, 104.0);
        var sig = svc.DetectHighDeviationPullback(snaps, 106.5);
        Assert.Null(sig);
    }
}
