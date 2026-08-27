// 均价线拐头向下(vwap_slope_down) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectVWAPSlopeDown + CalculateSlopeByTime。
// 验证「OLS 线性回归斜率 + 拐头向下判定」翻译核心。
// 与 verify_sell_vwapSlopeDown_js.mjs（原 JS 抽方法体 + calculateSlopeByTime + 同一组 3 场景）跨语言比对。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectVWAPSlopeDownTests
{
    private static IntradaySnapshot Mk(double price, double avgPrice, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price, Open = price, High = price, Low = price,
        AvgPrice = avgPrice,
        Volume = 100, IntervalVolume = 100,
        PreClose = 100.0,
        VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(int count, DateTime baseTime, Func<int, double> avgPriceFn, Func<int, double> priceFn)
    {
        var list = new List<IntradaySnapshot>();
        for (var i = 0; i < count; i++)
            list.Add(Mk(priceFn(i), avgPriceFn(i), baseTime.AddMinutes(i)));
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "vwapSlope_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            VwapSlopeDownCandles = 5,
            VwapSlopeDownThreshold = -0.1,
        });
        return svc;
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
    {
        Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
    }

    // S1: 触发（8根，avgPrice 线性下降 -0.5/min，OLS slope≈-0.483%/min < -0.1）
    [Fact]
    public void VWAPSlopeDown_Fires()
    {
        var svc = CreateService();
        var baseTime = new DateTime(2026, 1, 1, 14, 30, 0);
        Func<int, double> avgFn = i => 105.0 - 0.5 * i;
        var snaps = Build(8, baseTime, avgFn, _ => 100.0);

        var sig = svc.DetectVWAPSlopeDown(snaps, 100);

        Assert.NotNull(sig);
        Assert.Equal("均价线拐头向下", sig!.LevelName);
        AssertClose(-0.4830917874396135, sig.GetDouble("slope"), 1e-9, "slope");
        AssertClose(101.5, sig.GetDouble("currentAvg"), 1e-9, "currentAvg");
    }

    // S2: 斜率为 0（avgPrice 全=105），slope=0 >= -0.1 → null
    [Fact]
    public void NoSlopeDown_DoesNotFire()
    {
        var svc = CreateService();
        var baseTime = new DateTime(2026, 1, 1, 14, 30, 0);
        Func<int, double> avgFn = _ => 105.0;
        var snaps = Build(8, baseTime, avgFn, _ => 100.0);

        var sig = svc.DetectVWAPSlopeDown(snaps, 100);

        Assert.Null(sig);
    }

    // S3: currentPrice >= currentAvg → null（即使斜率下行）
    [Fact]
    public void PriceAboveVWAP_DoesNotFire()
    {
        var svc = CreateService();
        var baseTime = new DateTime(2026, 1, 1, 14, 30, 0);
        var s3Base = new[] { 110.0, 109.5, 109.0, 108.5, 108.0, 107.5, 107.0, 108.0 };
        Func<int, double> avgFn = i => s3Base[i];
        Func<int, double> priceFn = i => i == 7 ? 109.0 : 100.0;
        var snaps = Build(8, baseTime, avgFn, priceFn);

        var sig = svc.DetectVWAPSlopeDown(snaps, 109);

        Assert.Null(sig);
    }
}
