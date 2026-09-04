// 三重顶(triple_top) 检测回归测试
// 背景（2026-09-04 一博科技双顶误报修复的举一反三）：三重顶检测器存在与双顶完全同构的三处缺陷，
//   已同步修复：
//   1) 谷底深度原只用固定档位下限（GetMinNeckDepth），高波动日内整理平台的小波动即可拼出"合格谷底"
//      → 现为 max(档位下限, 日内波幅×0.35)；
//   2) 相邻两顶间隔原只有根数下限（5根），10秒快照下仅25秒
//      → 现增加真实时间间隔 ≥ DoubleTopMinPeakGapMinutes(5分钟)；
//   3) 相似度 keyPoints 的 trough1/trough2 原用两峰中点，谷底偏侧时特征失真
//      → 现用真实谷底索引。
// 本文件验证：真实三顶正常触发，两类误报源被拦截，keyPoints 特征真实。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectTripleTopTests
{
    private static IntradaySnapshot Mk(double price, double volume, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = price,
        Volume = volume,
        IntervalVolume = volume,
        PreClose = 100.0,
        VolumeReliable = true,
    };

    /// <summary>
    /// 合成标准三顶（43根，价格100→104→105→103→104.8→102.8→104.9→102）：
    /// p1=idx15(105)、谷1=idx22(103)、p2=idx23(104.8)、谷2=idx30(102.8)、p3=idx31(104.9)。
    /// 三顶偏差0.19%（≤0.5%），谷深1.9%（≥max(0.8%, 波幅5%×0.35=1.75%)），
    /// 相邻顶间隔8根。快照间隔由参数控制。
    /// </summary>
    private static List<IntradaySnapshot> BuildTripleTop(TimeSpan interval, double neckDrop1 = 2.0, double neckDrop2 = 2.2)
    {
        var prices = new List<double>();
        for (var i = 0; i <= 14; i++) prices.Add(100.0 + 4.0 * i / 14);        // 前置缓涨 100→104
        prices.Add(105.0);                                                      // p1 (idx15)
        for (var i = 1; i <= 7; i++) prices.Add(105.0 - neckDrop1 * i / 7);     // 谷1 (idx22)
        prices.Add(105.0 - 0.2);                                                // p2 = 104.8 (idx23)
        for (var i = 1; i <= 7; i++) prices.Add(104.8 - (neckDrop2 - 0.2) * i / 7); // 谷2 (idx30)
        prices.Add(104.9);                                                      // p3 (idx31)
        for (var i = 1; i <= 11; i++) prices.Add(104.9 - 2.9 * i / 11);         // 回落 →102 (idx42)

        var volumes = new List<double>();
        for (var i = 0; i <= 15; i++) volumes.Add(100);  // 左上攻腿
        for (var i = 16; i <= 22; i++) volumes.Add(80);  // 谷1
        for (var i = 23; i <= 30; i++) volumes.Add(60);  // 中段（缩量）
        for (var i = 31; i <= 42; i++) volumes.Add(90);  // 末段回落

        var t0 = new DateTime(2026, 9, 4, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < prices.Count; i++)
            snaps.Add(Mk(prices[i], volumes[i], t0 + TimeSpan.FromTicks(interval.Ticks * i)));
        return snaps;
    }

    private static SellPointDetectorService CreateService(bool enableSimilarity = true)
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "tripletop_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();

        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(
            db, market,
            enableSimilarity ? new PatternSimilarityAdapter(new PatternSimilarityService()) : null);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = enableSimilarity,
        });
        return svc;
    }

    // ===== 1. 真实三顶：完整检测链（含相似度）正常触发 =====

    [Fact]
    public void GenuineTripleTop_FiresWithSimilarity()
    {
        var svc = CreateService(enableSimilarity: true);
        var snaps = BuildTripleTop(TimeSpan.FromMinutes(1)); // 相邻顶间隔8分钟 ≥ 5分钟

        var sig = svc.DetectTripleTop(snaps, snaps[^1].Price);

        Assert.NotNull(sig);
        Assert.Equal("三次上攻不创新高", sig!.LevelName);
        // 三顶价格 [105, 104.8, 104.9]
        var peaks = sig.Get<double[]>("peaks")!;
        AssertClose(105.0, peaks[0], 1e-9, "p1");
        AssertClose(104.8, peaks[1], 1e-9, "p2");
        AssertClose(104.9, peaks[2], 1e-9, "p3");
        // 谷深 (105-103)/105 = 1.905% ≥ max(0.8%, 5%×0.35=1.75%)
        AssertClose(1.9047619047619048, sig.GetDouble("depth12"), 1e-9, "depth12");
        // 形态相似度参与门控且达标（TripleTopSimilarityMin=0.50）
        Assert.True(sig.GetDouble("similarity") >= 0.50,
            $"similarity {sig.GetDouble("similarity")} 应 >= 0.50");
    }

    // ===== 2. 相似度直查：真实谷底 keyPoints 使特征不失真 =====

    [Fact]
    public void GenuineTripleTop_SimilarityUsesRealTrough()
    {
        // 检测器传给相似度服务的候选窗口：patternStart = p1-5 = idx10，共33点（idx10..42）
        var snaps = BuildTripleTop(TimeSpan.FromMinutes(1));
        var allPrices = snaps.Select(s => s.Price).ToList();
        var allVols = snaps.Select(s => s.IntervalVolume ?? s.Volume).ToList();
        var prices = allPrices.GetRange(10, 33);
        var volumes = allVols.GetRange(10, 33);
        // keyPoints 与检测器一致：trough1/trough2 用真实谷底索引（idx22/idx30）
        var kp = new Dictionary<string, int>
        {
            ["peak1"] = 15 - 10,
            ["trough1"] = 22 - 10,
            ["peak2"] = 23 - 10,
            ["trough2"] = 30 - 10,
            ["peak3"] = 31 - 10,
            ["breakdown"] = 42 - 10,
        };

        var svc = new PatternSimilarityService();
        var res = svc.CalculateSimilarity(prices.ToArray(), "triple_top", kp, volumes.ToArray());

        Assert.True(res.Details.ConstraintPassed, "真实三顶必须通过结构约束");
        Assert.True(res.Similarity >= 0.50, $"真实三顶相似度 {res.Similarity} 应 >= 0.50");
        // 提取特征中 trough1 维度 = 归一化后的真实谷底值（103），
        // 而非两峰中点（idx18≈103.7）——修复后特征不再失真
        var normalized = PatternSimilarityService.Normalize(prices.ToArray());
        var features = res.Details.CandidateFeatures!;
        AssertClose(normalized[kp["trough1"]], features[1], 1e-9, "trough1特征=真实谷底归一化值");
        AssertClose(normalized[kp["trough2"]], features[3], 1e-9, "trough2特征=真实谷底归一化值");
    }

    // ===== 3. 相邻顶时间间隔不足：分钟级约束拦截 =====

    [Fact]
    public void PeakGapTooShortInMinutes_Blocked()
    {
        // 相同三顶几何，仅快照间隔改为10秒：相邻顶间隔8根×10秒=1.33分钟 < 5分钟 → 拦截。
        // 旧逻辑仅有根数下限（8根≥5根会放行）。
        var svc = CreateService(enableSimilarity: false);
        var snaps = BuildTripleTop(TimeSpan.FromSeconds(10));

        var sig = svc.DetectTripleTop(snaps, snaps[^1].Price);

        Assert.Null(sig);
    }

    // ===== 4. 谷深低于波幅比例下限：被检测器拦截 =====

    [Fact]
    public void TroughDepthBelowVolatilityFloor_Blocked()
    {
        // 日内波幅5% → 谷底下限 5%×0.35 = 1.75%。
        // 构造两谷深度均1.2%（高于0.8%档位下限但低于波幅比例下限）：
        // 高波动日内整理平台的小波动不应拼出三重顶。
        var svc = CreateService(enableSimilarity: false);
        var snaps = BuildTripleTop(TimeSpan.FromMinutes(1), neckDrop1: 1.26, neckDrop2: 1.46);

        var sig = svc.DetectTripleTop(snaps, snaps[^1].Price);

        Assert.Null(sig);
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
}
