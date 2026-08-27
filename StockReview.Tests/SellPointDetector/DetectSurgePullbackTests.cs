// 冲高回落(surge_pullback) 翻译回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectSurgePullback。
// 关闭 位置/趋势/相似度 过滤，仅验证「冲高回落几何判定」这一翻译核心，
// 防止未来重构破坏已被 JS 原版交叉验证过的行为。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectSurgePullbackTests
{
    private static IntradaySnapshot Mk(double price, double preClose, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = price,
        Volume = 1000,
        IntervalVolume = 100,
        PreClose = preClose,
        VolumeReliable = true,
    };

    private static List<IntradaySnapshot> Build(double[] prices, double preClose)
    {
        var list = new List<IntradaySnapshot>();
        var t = new DateTime(2026, 1, 1, 9, 30, 0);
        for (var i = 0; i < prices.Length; i++)
            list.Add(Mk(prices[i], preClose, t.AddSeconds(i * 60)));
        return list;
    }

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        // 每个测试方法用独立临时库，互不干扰
        var tmp = Path.Combine(Path.GetTempPath(), "surge_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();

        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
            TopPatternMinPosition = 0,
            TopPatternMaxVwapSlope = 1.0,
            SurgePullbackThreshold = 1.8,
            PullbackRatio = 0.35,
            SurgeFastSpan = 3,
            SurgeFastMinRisePct = 1.2,
        });
        return svc;
    }

    // S1: 快速拉升(100→103) 随后回落(103→101.8) → 应触发卖点
    [Fact]
    public void SpikeThenPullback_Fires()
    {
        var svc = CreateService();
        var snaps = Build(new[]
        {
            100d, 100.2, 100.4, 100.6, 100.8, 101, 102, 103, 103, 103, 102.5, 101.8,
        }, 100);

        var sig = svc.DetectSurgePullback(snaps, 101.8);

        Assert.NotNull(sig);
        Assert.Equal(103d, sig.GetDouble("peakPrice"), 2);
        Assert.True(sig.GetDouble("pullbackRatio") > 0, "回落比例应为正");
        Assert.Equal(1.8, sig.GetDouble("currentChangePct"), 2);
    }

    // S2: 拉升后横盘未回落(100→103 维持) → 不应触发
    [Fact]
    public void SustainedHigh_NoPullback_DoesNotFire()
    {
        var svc = CreateService();
        var snaps = Build(new[]
        {
            100d, 100.2, 100.4, 100.6, 100.8, 101, 102, 103, 103, 103, 103, 103,
        }, 100);

        var sig = svc.DetectSurgePullback(snaps, 103.0);

        Assert.Null(sig);
    }

    // S3: 拉升幅度过小(100→101) → 不应触发
    [Fact]
    public void SmallSpike_DoesNotFire()
    {
        var svc = CreateService();
        var snaps = Build(new[]
        {
            100d, 100.1, 100.2, 100.3, 100.4, 100.5, 100.6, 100.7, 100.8, 100.9, 101, 100.5,
        }, 100);

        var sig = svc.DetectSurgePullback(snaps, 100.5);

        Assert.Null(sig);
    }
}
