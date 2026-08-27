// 缩量均线反弹失败(weak_rebound_failure) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectWeakReboundFailure。
// 绕 vwapSlope 检查（WeakReboundVwapSlopeMax=999），验证「7 条件判定」核心：当前下方+最近N下方+反弹高点+回落+缩量。
// 与 verify_sell_weakRebound_js.mjs（原 JS 抽方法体 + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectWeakReboundFailureTests
{
    private static IntradaySnapshot Mk(double price, double avgPrice, double iv, DateTime t) => new()
    {
        SnapshotAt = t, Price = price, Open = price, High = price, Low = price,
        AvgPrice = avgPrice, Volume = iv, IntervalVolume = iv,
        PreClose = 100.0, VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, double avgPrice, Func<int, double> priceFn, double beforeVol, double reboundVol)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 14, 30, 0);
        for (var i = 0; i < count; i++)
        {
            var iv = i >= 5 && i <= 9 ? reboundVol : beforeVol; // reboundWindow index 5..9
            list.Add(Mk(priceFn(i), avgPrice, iv, t.AddSeconds(i * 60)));
        }
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "weakRebound_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp); db.SetDataDir(tmp); db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            WeakReboundBelowConfirm = 5,
            WeakReboundMaxScan = 20,
            WeakReboundGapMin = -0.3,
            WeakReboundGapMax = 0.2,
            WeakReboundPullbackPct = 0.5,
            WeakReboundVolShrink = 0.6,
            WeakReboundVwapSlopeMax = 999,
        });
        return svc;
    }

    private static void AssertClose(double e, double a, double tol = 1e-6, string? m = null)
    {
        Assert.True(Math.Abs(e - a) < tol, $"{m ?? "value"}: expected ~{e}, actual {a}");
    }

    // S1: 触发（最近 5 根下方 + 反弹高点 index=7 + 缩量 50/100=0.5 < 0.6）
    [Fact]
    public void WeakReboundFailure_Fires()
    {
        var svc = CreateService();
        double avgPrice = 101.0;
        Func<int, double> priceFn = i => i < 7 ? 100.0 : (i == 7 ? 101.1 : 100.0);
        var snaps = Build(15, avgPrice, priceFn, 100, 50);
        var sig = svc.DetectWeakReboundFailure(snaps, 100);
        Assert.NotNull(sig);
        Assert.Equal("缩量均线反弹失败", sig!.LevelName);
        AssertClose(101.1, sig.GetDouble("reboundPrice"), 1e-9);
        AssertClose(0.09900990099009338, sig.GetDouble("reboundGap"), 1e-9);
        AssertClose(1.0880316518298658, sig.GetDouble("pullback"), 1e-9);
        AssertClose(0.5, sig.GetDouble("volumeShrinkRatio"), 1e-9);
    }

    // S2: 未缩量（reboundVol=80 → 0.8 不 < 0.6）
    [Fact]
    public void NoVolumeShrink_DoesNotFire()
    {
        var svc = CreateService();
        double avgPrice = 101.0;
        Func<int, double> priceFn = i => i < 7 ? 100.0 : (i == 7 ? 101.1 : 100.0);
        var snaps = Build(15, avgPrice, priceFn, 100, 80);
        var sig = svc.DetectWeakReboundFailure(snaps, 100);
        Assert.Null(sig);
    }

    // S3: 价格在均线上方（currentPrice=102 >= 101 → null 条件1）
    [Fact]
    public void PriceAboveVWAP_DoesNotFire()
    {
        var svc = CreateService();
        double avgPrice = 101.0;
        Func<int, double> priceFn = i => i < 7 ? 100.0 : (i == 7 ? 101.1 : 100.0);
        var snaps = Build(15, avgPrice, priceFn, 100, 50);
        var sig = svc.DetectWeakReboundFailure(snaps, 102);
        Assert.Null(sig);
    }
}
