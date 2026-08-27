// 尾盘资金出逃(late_session_exit) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectLateSessionExit。
// 绕时间检查（LateSessionStart='00:00'），验证「放量+跌破」几何判定这一翻译核心。
// 与 verify_sell_lateSessionExit_js.mjs（原 JS 抽方法体 + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectLateSessionExitTests
{
    private static IntradaySnapshot Mk(double price, double volume, double intervalVolume, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price,
        Open = price, High = price, Low = price,
        AvgPrice = price,
        Volume = volume,
        IntervalVolume = intervalVolume,
        PreClose = 100.0,
        VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, Func<int, double> priceFn, double volBase, double volLast)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 14, 35, 0); // 任意时间（绕时间检查）
        for (var i = 0; i < count; i++)
        {
            var iv = i == count - 1 ? volLast : volBase;
            list.Add(Mk(priceFn(i), volBase, iv, t.AddSeconds(i * 60)));
        }
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "lateSess_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            LateSessionStart = "00:00",          // 绕时间检查
            LateSessionVolumeMultiple = 2.0,
            LateSessionBreakdownPct = 0.3,
        });
        return svc;
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
    {
        Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
    }

    // S1: 触发（8 根，前 6 根 104、最后 2 根 103.6，跌 0.38% >= 0.3%，currentVol 300=3x）
    [Fact]
    public void LateSessionExit_Fires()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 6 ? 104.0 : 103.6;
        var snaps = Build(8, priceFn, 100, 300);

        var sig = svc.DetectLateSessionExit(snaps, 103.6);

        Assert.NotNull(sig);
        Assert.Equal("尾盘资金出逃", sig!.LevelName);
        AssertClose(3.0, sig.GetDouble("volumeMultiple"), 1e-9, "volumeMultiple");
        AssertClose(0.3846153846153901, sig.GetDouble("breakdownPct"), 1e-9, "breakdownPct");
    }

    // S2: 未放量，不触发（currentVol=150，1.5x < 2x 阈值）
    [Fact]
    public void NoVolumeAmplify_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 6 ? 104.0 : 103.6;
        var snaps = Build(8, priceFn, 100, 150);

        var sig = svc.DetectLateSessionExit(snaps, 103.6);

        Assert.Null(sig);
    }

    // S3: 跌太少，不触发（currentPrice=103.95，跌 0.048% < 0.3% 阈值）
    [Fact]
    public void TooSmallBreakdown_DoesNotFire()
    {
        var svc = CreateService();
        Func<int, double> priceFn = i => i < 6 ? 104.0 : 103.95;
        var snaps = Build(8, priceFn, 100, 300);

        var sig = svc.DetectLateSessionExit(snaps, 103.95);

        Assert.Null(sig);
    }
}
