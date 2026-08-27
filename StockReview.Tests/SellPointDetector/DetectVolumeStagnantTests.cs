// 放量滞涨(volume_stagnant) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectVolumeStagnant。
// 关闭 位置/趋势/距离 过滤，验证「放量+滞涨」几何判定这一翻译核心。
// 与 verify_sell_volumeStagnant_js.mjs（原 JS 抽方法体 + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectVolumeStagnantTests
{
    private static IntradaySnapshot Mk(double price, double avgPrice, double volume, double intervalVolume, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = avgPrice,
        Volume = volume,
        IntervalVolume = intervalVolume,
        PreClose = 100.0,
        VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, Func<int, double> priceFn, double volBase, double volLast)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 9, 30, 0);
        for (var i = 0; i < count; i++)
        {
            var iv = i == count - 1 ? volLast : volBase;
            list.Add(Mk(priceFn(i), 103.5, volBase, iv, t.AddSeconds(i * 60)));
        }
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "volstagnant_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();

        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            TopPatternMinPosition = 0,
            TopPatternMaxVwapSlope = 999,
            VolumeAmplifyMultiple = 2.0,
            StagnantThreshold = 0.5,
            AvgPriceDistancePct = 0,
        });
        return svc;
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
    {
        Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
    }

    // S1: 放量滞涨触发（60根，价格 103→104 涨30根后维持30根，intervalVol 100→300 3x放大）
    [Fact]
    public void HighVolumeStagnant_Fires()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 30 ? 103.0 + i / 29.0 : 104.0;
        var snaps = Build(60, priceFn, 100, 300);

        var sig = svc.DetectVolumeStagnant(snaps, 104);

        Assert.NotNull(sig);
        Assert.Equal("放量滞涨", sig!.LevelName);
        AssertClose(3.0, sig.GetDouble("volumeMultiple"), 1e-9, "volumeMultiple");
        AssertClose(0.0, sig.GetDouble("changePct"), 1e-9, "changePct");
        AssertClose(0.4830917874396135, sig.GetDouble("distancePct"), 1e-9, "distancePct");
    }

    // S2: 未放量，不触发（currentVol=150，1.5x < 2x 阈值）
    [Fact]
    public void NoVolumeAmplify_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 30 ? 103.0 + i / 29.0 : 104.0;
        var snaps = Build(60, priceFn, 100, 150);

        var sig = svc.DetectVolumeStagnant(snaps, 104);

        Assert.Null(sig);
    }

    // S3: 涨太多（不滞涨），不触发（recentWindow 起点 snapshots[49]=103，
    // snapshots[50..59] 从 103→104 涨约 0.97% >= 0.5% StagnantThreshold → null）
    [Fact]
    public void TooMuchRise_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i =>
        {
            if (i < 30) return 103.0 + i / 29.0;
            if (i < 49) return 104.0;
            if (i == 49) return 103.0;
            return 103.0 + (i - 49) / 10.0;
        };
        var snaps = Build(60, priceFn, 100, 300);

        var sig = svc.DetectVolumeStagnant(snaps, 104);

        Assert.Null(sig);
    }
}
