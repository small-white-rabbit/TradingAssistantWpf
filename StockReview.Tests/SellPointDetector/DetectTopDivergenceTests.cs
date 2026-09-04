// 顶背离(top_divergence) 检测回归测试
// 背景（2026-09-04 一博科技双顶误报修复的举一反三）：顶背离检测器存在与双顶同构的两处缺陷，已同步修复：
//   1) 相似度 keyPoints 的 trough 原用两峰中点——谷底偏侧时（急跌缓涨/缓跌急涨）中点不在谷底上，
//      相似度的 trough 特征失真 → 现用两峰之间真实谷底索引；
//   2) 两高点间隔原只有根数下限（5根），10秒快照下仅25秒，间隔过近的两个高点乖离率比较无统计意义
//      → 现增加真实时间间隔 ≥ DoubleTopMinPeakGapMinutes(5分钟)。
// 本文件验证：真实顶背离正常触发，高频快照短间隔被拦截，trough 特征用真实谷底。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectTopDivergenceTests
{
    /// <summary>
    /// 合成顶背离（41根）：
    /// p1=idx14(105, avg=102.5, dev1=2.44%)、谷底=idx24(103)、p2=idx30(105.8, avg=103.8, dev2=1.93%)。
    /// 价格创新高（+0.76% > NewHighPct 0.5%）但VWAP乖离收缩（1.93 < 2.44×0.85=2.07）。
    /// 快照间隔由参数控制。
    /// </summary>
    private static List<IntradaySnapshot> BuildDivergence(TimeSpan interval)
    {
        var prices = new List<double>();
        for (var i = 0; i <= 13; i++) prices.Add(100.0 + 2.0 * i / 13);  // 缓涨 100→102
        prices.Add(105.0);                                               // p1 (idx14)
        for (var i = 1; i <= 10; i++) prices.Add(105.0 - 2.0 * i / 10); // 回落 →103 (idx24)
        for (var i = 1; i <= 6; i++) prices.Add(103.0 + 2.8 * i / 6);   // 回升 →105.8 (idx30)
        for (var i = 1; i <= 10; i++) prices.Add(105.8 - 1.3 * i / 10); // 回落 →104.5 (idx40)

        // VWAP：前段跟随价格，p1 处 102.5（乖离 2.44%），p2 处 103.8（乖离 1.93%）
        var avgs = new List<double>();
        for (var i = 0; i <= 13; i++) avgs.Add(100.0 + 1.0 * i / 13);   // 100→101
        avgs.Add(102.5);                                                // idx14
        for (var i = 1; i <= 10; i++) avgs.Add(102.5 + 0.5 * i / 10);   // 102.5→103 (idx24)
        for (var i = 1; i <= 6; i++) avgs.Add(103.0 + 0.8 * i / 6);     // 103→103.8 (idx30)
        for (var i = 1; i <= 10; i++) avgs.Add(103.8 - 0.3 * i / 10);   // 103.8→103.5

        var t0 = new DateTime(2026, 9, 4, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < prices.Count; i++)
        {
            snaps.Add(new IntradaySnapshot
            {
                SnapshotAt = t0 + TimeSpan.FromTicks(interval.Ticks * i),
                Price = prices[i],
                Open = prices[i],
                High = prices[i],
                Low = prices[i],
                AvgPrice = avgs[i],
                Volume = 100,
                IntervalVolume = 100,
                PreClose = 100.0,
                VolumeReliable = true,
            });
        }
        return snaps;
    }

    private static SellPointDetectorService CreateService(bool enableSimilarity = true)
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "topdiv_test_" + Guid.NewGuid().ToString("N"));
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

    // ===== 1. 真实顶背离：完整检测链（含相似度）正常触发 =====

    [Fact]
    public void GenuineDivergence_FiresWithSimilarity()
    {
        var svc = CreateService(enableSimilarity: true);
        var snaps = BuildDivergence(TimeSpan.FromMinutes(1)); // 两高点间隔16分钟 ≥ 5分钟

        var sig = svc.DetectTopDivergence(snaps, snaps[^1].Price);

        Assert.NotNull(sig);
        Assert.Equal("顶背离", sig!.LevelName);
        AssertClose(105.0, sig.GetDouble("firstHigh"), 1e-9, "firstHigh");
        AssertClose(105.8, sig.GetDouble("secondHigh"), 1e-9, "secondHigh");
        // 乖离收缩：dev2 < dev1 × 0.85
        Assert.True(sig.GetDouble("secondDeviation") < sig.GetDouble("firstDeviation") * 0.85,
            "第二高点乖离应显著收缩");
        // 形态相似度参与门控且达标（TopDivergenceSimilarityMin=0.45）
        Assert.True(sig.GetDouble("similarity") >= 0.45,
            $"similarity {sig.GetDouble("similarity")} 应 >= 0.45");
    }

    // ===== 2. 两高点时间间隔不足：分钟级约束拦截 =====

    [Fact]
    public void PeakGapTooShortInMinutes_Blocked()
    {
        // 相同几何，仅快照间隔改为10秒：两高点间隔16根×10秒=2.67分钟 < 5分钟 → 拦截。
        // 旧逻辑仅有根数下限（16根≥5根会放行）。
        var svc = CreateService(enableSimilarity: false);
        var snaps = BuildDivergence(TimeSpan.FromSeconds(10));

        var sig = svc.DetectTopDivergence(snaps, snaps[^1].Price);

        Assert.Null(sig);
    }

    // ===== 3. 相似度直查：trough 用真实谷底索引（非两峰中点） =====

    [Fact]
    public void GenuineDivergence_SimilarityUsesRealTrough()
    {
        // 检测器传给相似度服务的候选窗口：patternStart = p1-5 = idx9，共32点（idx9..40）
        var snaps = BuildDivergence(TimeSpan.FromMinutes(1));
        var allPrices = snaps.Select(s => s.Price).ToList();
        var allVols = snaps.Select(s => s.IntervalVolume ?? s.Volume).ToList();
        var prices = allPrices.GetRange(9, 32);
        var volumes = allVols.GetRange(9, 32);
        // keyPoints 与检测器一致：trough 用真实谷底索引（idx24-9=15，
        // 而旧两峰中点=(14+30)/2=22，对应价格103.6≠谷底103）
        var kp = new Dictionary<string, int>
        {
            ["peak1"] = 14 - 9,
            ["trough"] = 24 - 9,
            ["peak2"] = 30 - 9,
            ["current"] = 40 - 9,
        };

        var svc = new PatternSimilarityService();
        var res = svc.CalculateSimilarity(prices.ToArray(), "top_divergence", kp, volumes.ToArray());

        Assert.True(res.Details.ConstraintPassed, "真实顶背离必须通过结构约束（右峰高于左峰）");
        // 提取特征中 trough 维度 = 归一化后的真实谷底值，而非两峰中点值
        var normalized = PatternSimilarityService.Normalize(prices.ToArray());
        var features = res.Details.CandidateFeatures!;
        AssertClose(normalized[kp["trough"]], features[1], 1e-9, "trough特征=真实谷底归一化值");
        // 谷底归一化值应低于中点归一化值（修复使特征真正反映谷深）
        var midPoint = (int)Math.Floor((kp["peak1"] + kp["peak2"]) / 2.0);
        Assert.True(normalized[kp["trough"]] < normalized[midPoint] - 0.05,
            "真实谷底应显著低于两峰中点（本数据形态谷底偏后）");
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
}
