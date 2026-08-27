// 大跌反抽卖点(deep_drop_rebound) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectDeepDropRebound + FindPlatformBefore。
// 验证「11 条件判定 + 平台检测」翻译核心。
// 与 verify_sell_deepDropRebound_js.mjs（原 JS 抽方法体 + _findPlatformBefore + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectDeepDropReboundTests
{
    private static IntradaySnapshot Mk(double price, double avgPrice, double preClose, DateTime t) => new()
    {
        SnapshotAt = t, Price = price, Open = price, High = price, Low = price,
        AvgPrice = avgPrice, PreClose = preClose,
        Volume = 100, IntervalVolume = 100,
        VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, Func<int, double> priceFn, Func<int, double> avgPriceFn, double preClose)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 9, 30, 0);
        for (var i = 0; i < count; i++)
            list.Add(Mk(priceFn(i), avgPriceFn(i), preClose, t.AddSeconds(i * 60)));
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "deepDrop_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp); db.SetDataDir(tmp); db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            DeepDropMinSnapshots = 10,
            DeepDropMinPct = 5,
            DeepDropReboundMinPct = 2,
            DeepDropAboveVwapTol = 0.5,
            DeepDropPlatformMinBars = 5,
            DeepDropPlatformAmplitude = 1.0,
            DeepDropTouchTolerance = 1.0,
            DeepDropVolShrink = 0.6,
            DeepDropMaxElapsed = 30,
            DeepDropPullbackPct = 0.5,
        });
        return svc;
    }

    private static void AssertClose(double e, double a, double tol = 1e-6, string? m = null)
    {
        Assert.True(Math.Abs(e - a) < tol, $"{m ?? "value"}: expected ~{e}, actual {a}");
    }

    // S1: 触发（lowPrice=93 -7%、平台[3..7]=95、reboundHigh=95.5 idx=8、过均线、触平台顶、pullback 1.57%）
    [Fact]
    public void DeepDropRebound_Fires()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 3 ? 93.0 : (i < 8 ? 95.0 : (i == 8 ? 95.5 : 94.0));
        Func<int, double> avgFn = i => i < 3 ? 93.0 : (i < 8 ? 94.0 : (i == 8 ? 94.5 : 94.5));
        var snaps = Build(15, priceFn, avgFn, 100);
        var sig = svc.DetectDeepDropRebound(snaps, 94);
        Assert.NotNull(sig);
        Assert.Equal("大跌反抽卖点", sig!.LevelName);
        AssertClose(-7.0, sig.GetDouble("dropPct"), 1e-9);
        AssertClose(2.6881720430107525, sig.GetDouble("reboundPct"), 1e-9);
        AssertClose(1.5706806282722512, sig.GetDouble("pullbackPct"), 1e-9);
        AssertClose(93.0, sig.GetDouble("lowPrice"), 1e-9);
        Assert.Equal("top", sig.Get<string>("touchedPlatform"));
    }

    // S2: 未深跌（lowPrice=98，dropPct=-2% > -5% → null）
    [Fact]
    public void NotDeepDrop_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 3 ? 98.0 : 99.0;
        Func<int, double> avgFn = i => i < 3 ? 98.0 : 99.0;
        var snaps = Build(15, priceFn, avgFn, 100);
        var sig = svc.DetectDeepDropRebound(snaps, 99);
        Assert.Null(sig);
    }

    // S3: 反弹不足（reboundHigh=94.5 来自 idx=8，reboundPct=1.61% < 2% → null）
    [Fact]
    public void InsufficientRebound_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 3 ? 93.0 : (i < 8 ? 94.0 : (i == 8 ? 94.5 : 94.0));
        Func<int, double> avgFn = i => i < 3 ? 93.0 : (i < 8 ? 94.0 : (i == 8 ? 94.5 : 94.5));
        var snaps = Build(15, priceFn, avgFn, 100);
        var sig = svc.DetectDeepDropRebound(snaps, 94);
        Assert.Null(sig);
    }
}
