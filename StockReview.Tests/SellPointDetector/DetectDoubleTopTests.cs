// 双顶形态(double_top) 检测回归测试
// 背景（2026-09-04 一博科技 9:41 误报修复）：
//   1) 颈线深度须 ≥ max(价格档位下限, 日内波幅×0.35)——高波动股 0.8% 档位下限只是整理噪音；
//   2) 两顶真实时间间隔 ≥ DoubleTopMinPeakGapMinutes(5分钟)——根数下限与推送频率耦合
//      （10秒快照下5根=25秒无约束力），早盘急拉后的第一个整理平台是上升中继而非双头；
//   3) 形态相似度 CheckConstraints 增加颈线深度结构约束：归一化后颈线须下探至
//      形态波幅30%以上，颈线贴近峰值的高位浅缺口不是M头；
//   4) 相似度 keyPoints 的 neck 用真实谷底索引（原为两峰中点，谷底偏侧时失真）。
// 本文件验证：真实M头在完整相似度门控下正常触发，两类误报源被拦截。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectDoubleTopTests
{
    private static IntradaySnapshot Mk(double price, double volume, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = price, // VWAP斜率跟随价格方向，双顶不限制下行斜率
        Volume = volume,
        IntervalVolume = volume,
        PreClose = 100.0,
        VolumeReliable = true,
    };

    /// <summary>
    /// 合成标准M头（48根，价格100→105→102.5→105→102）：
    /// 左顶 idx19、颈线 idx27(102.5)、右顶 idx37，颈线深度2.38%，右腿缩量至0.57。
    /// 快照间隔由参数控制（1分钟=真实M头应触发；10秒=时间间隔不足应拦截）。
    /// </summary>
    private static List<IntradaySnapshot> BuildMHead(TimeSpan interval)
    {
        var prices = new List<double>();
        for (var i = 0; i <= 19; i++) prices.Add(100.0 + 5.0 * i / 19);   // 左侧上攻 100→105
        for (var i = 1; i <= 8; i++) prices.Add(105.0 - 2.5 * i / 8);    // 回落至颈线 102.5 (idx27)
        for (var i = 1; i <= 10; i++) prices.Add(102.5 + 2.5 * i / 10);  // 缩量反弹 102.5→105 (idx37)
        for (var i = 1; i <= 10; i++) prices.Add(105.0 - 3.0 * i / 10);  // 跌破颈线 105→102 (idx47)

        var volumes = new List<double>();
        for (var i = 0; i <= 19; i++) volumes.Add(100);  // 左上攻腿量能
        for (var i = 20; i <= 27; i++) volumes.Add(80);  // 回落
        for (var i = 28; i <= 38; i++) volumes.Add(60);  // 右腿缩量（0.57倍）
        for (var i = 39; i <= 49; i++) volumes.Add(90);  // 跌破颈线

        var t0 = new DateTime(2026, 9, 4, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < prices.Count; i++)
            snaps.Add(Mk(prices[i], volumes[i], t0 + TimeSpan.FromTicks(interval.Ticks * i)));
        return snaps;
    }

    private static SellPointDetectorService CreateService(bool enableSimilarity = true)
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "doubletop_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();

        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(
            db, market,
            enableSimilarity ? new PatternSimilarityAdapter(new PatternSimilarityService()) : null);
        // 其余参数全部取默认值（颈线档位/波幅比例0.35/最小顶间隔5分钟/相似度门限0.50）
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = enableSimilarity,
        });
        return svc;
    }

    // ===== 1. 真实M头：完整检测链（含形态相似度三层计算+新约束）正常触发 =====

    [Fact]
    public void GenuineMHead_FiresWithSimilarity()
    {
        var svc = CreateService(enableSimilarity: true);
        var snaps = BuildMHead(TimeSpan.FromMinutes(1)); // 两顶间隔18分钟 ≥ 5分钟

        var sig = svc.DetectDoubleTop(snaps, snaps[^1].Price);

        Assert.NotNull(sig);
        Assert.Equal("双顶形态", sig!.LevelName);
        // 颈线 = 两顶间真实谷底 102.5
        AssertClose(102.5, sig.LevelPrice, 1e-9, "troughPrice");
        // 颈线深度 (105-102.5)/105 = 2.381% ≥ max(0.8%, 5%×0.35=1.75%)
        AssertClose(2.380952380952381, sig.GetDouble("neckToTopPct"), 1e-9, "neckToTopPct");
        // 形态相似度参与门控且达标（DoubleTopSimilarityMin=0.50）
        Assert.True(sig.GetDouble("similarity") >= 0.50,
            $"similarity {sig.GetDouble("similarity")} 应 >= 0.50");
        // 右顶以来跌幅 (105-102)/105 = 2.857%
        AssertClose(2.857142857142857, sig.GetDouble("dropFromRight"), 1e-9, "dropFromRight");
        // 右腿缩量
        Assert.True(sig.GetDouble("rightLeftVolumeRatio") < 0.8, "右腿应缩量");
    }

    // ===== 2. 真实M头：相似度服务直查（新颈线深度结构约束不误伤正常形态） =====

    [Fact]
    public void GenuineMHead_SimilarityPassesNeckDepthConstraint()
    {
        // 检测器传给相似度服务的候选窗口：patternStart = 左顶-5 = idx14，共34点（idx14..47）
        var snaps = BuildMHead(TimeSpan.FromMinutes(1));
        var prices = snaps.Select(s => s.Price).ToList().GetRange(14, 34);
        var volumes = snaps.Select(s => s.IntervalVolume ?? s.Volume).ToList().GetRange(14, 34);
        // keyPoints 与检测器一致：neck 用真实谷底索引（idx27-14=13）
        var kp = new Dictionary<string, int>
        {
            ["leftPeak"] = 19 - 14,
            ["neck"] = 27 - 14,
            ["rightPeak"] = 37 - 14,
            ["breakdown"] = 47 - 14,
        };

        var svc = new PatternSimilarityService();
        var res = svc.CalculateSimilarity(prices.ToArray(), "double_top", kp, volumes.ToArray());

        Assert.True(res.Details.ConstraintPassed, "真实M头必须通过结构约束（含颈线深度≥30%波幅）");
        Assert.True(res.Similarity >= 0.60,
            $"真实M头相似度 {res.Similarity} 应 >= 0.60（远超门限0.50）");
        // 第三层特征分支生效（非中性0.5兜底）
        Assert.True(res.Details.FeatureScore > 0.6, $"featureScore {res.Details.FeatureScore} 应 > 0.6");
    }

    // ===== 3. 一博科技式浅颈线：颈线深度结构约束拦截（相似度归零） =====

    [Fact]
    public void ShallowNeckPlatform_BlockedByNeckDepthConstraint()
    {
        // 急拉100→105后高位平台浅缺口：左顶105(idx7)、颈线104.5(idx11)、右顶104.9(idx15)。
        // 颈线仅低于左顶0.5%，归一化后颈线贴近峰值——与一博科技9:41误报同构。
        var prices = new List<double>();
        for (var i = 0; i <= 7; i++) prices.Add(100.0 + 5.0 * i / 7);   // 急拉 100→105
        for (var i = 1; i <= 4; i++) prices.Add(105.0 - 0.5 * i / 4);  // 浅回落 →104.5 (idx11)
        for (var i = 1; i <= 4; i++) prices.Add(104.5 + 0.4 * i / 4);  // 弱反弹 →104.9 (idx15)
        for (var i = 1; i <= 7; i++) prices.Add(104.9 - 0.7 * i / 7);  // 回落 →104.2 (idx22)
        var kp = new Dictionary<string, int>
        {
            ["leftPeak"] = 7, ["neck"] = 11, ["rightPeak"] = 15, ["breakdown"] = 22,
        };
        var vols = new double[prices.Count];
        Array.Fill(vols, 1.0);

        var svc = new PatternSimilarityService();

        // 结构约束直接判定失败
        var normalized = PatternSimilarityService.Normalize(prices.ToArray());
        Assert.False(svc.CheckConstraints("double_top", kp, normalized),
            "浅颈线（归一化颈深<30%波幅）应被结构约束拦截");

        // 端到端：相似度归零且标记约束未通过
        var res = svc.CalculateSimilarity(prices.ToArray(), "double_top", kp, vols);
        Assert.False(res.Details.ConstraintPassed);
        Assert.Equal(0.0, res.Similarity, 12);
    }

    [Fact]
    public void DeepNeckPlatform_PassesNeckDepthConstraint()
    {
        // 对照组：同样的高位平台，但颈线下探至103（低于左顶2%），
        // 归一化颈深≈40%波幅 ≥ 30%，结构约束应放行。
        var prices = new List<double>();
        for (var i = 0; i <= 7; i++) prices.Add(100.0 + 5.0 * i / 7);   // 急拉 100→105
        for (var i = 1; i <= 4; i++) prices.Add(105.0 - 2.0 * i / 4);   // 颈线下探 →103 (idx11)
        for (var i = 1; i <= 4; i++) prices.Add(103.0 + 1.9 * i / 4);   // 反弹 →104.9 (idx15)
        for (var i = 1; i <= 7; i++) prices.Add(104.9 - 0.7 * i / 7);   // 回落 →104.2 (idx22)
        var kp = new Dictionary<string, int>
        {
            ["leftPeak"] = 7, ["neck"] = 11, ["rightPeak"] = 15, ["breakdown"] = 22,
        };

        var svc = new PatternSimilarityService();
        var normalized = PatternSimilarityService.Normalize(prices.ToArray());
        Assert.True(svc.CheckConstraints("double_top", kp, normalized),
            "深颈线（归一化颈深≥30%波幅）应通过结构约束");
    }

    // ===== 4. 两顶时间间隔不足：分钟级约束拦截（根数下限无法约束的场景） =====

    [Fact]
    public void PeakGapTooShortInMinutes_Blocked()
    {
        // 与测试1完全相同的M头几何形状，仅快照间隔改为10秒：
        // 两顶间隔18根×10秒=3分钟 < 5分钟 → 拦截。
        // 旧逻辑仅有根数下限（18根≥5根会放行），本测试锁定新分钟级约束的拦截效果。
        var svc = CreateService(enableSimilarity: true);
        var snaps = BuildMHead(TimeSpan.FromSeconds(10));

        var sig = svc.DetectDoubleTop(snaps, snaps[^1].Price);

        Assert.Null(sig);
    }

    // ===== 5. 波幅比例下限：颈线深度低于日内波幅×0.35 时被检测器拦截 =====

    [Fact]
    public void NeckDepthBelowVolatilityFloor_Blocked()
    {
        // 日内波幅5% → 颈线下限 5%×0.35 = 1.75%。
        // 构造颈线深度1.5%（高于0.8%档位下限但低于波幅比例下限）的M头：
        // 105 → 103.425(颈线1.5%) → 105 → 102。其余几何与测试1一致。
        var prices = new List<double>();
        for (var i = 0; i <= 19; i++) prices.Add(100.0 + 5.0 * i / 19);
        for (var i = 1; i <= 8; i++) prices.Add(105.0 - 1.575 * i / 8);  // →103.425
        for (var i = 1; i <= 10; i++) prices.Add(103.425 + 1.575 * i / 10); // →105
        for (var i = 1; i <= 10; i++) prices.Add(105.0 - 3.0 * i / 10);  // →102

        var volumes = new List<double>();
        for (var i = 0; i <= 19; i++) volumes.Add(100);
        for (var i = 20; i <= 27; i++) volumes.Add(80);
        for (var i = 28; i <= 38; i++) volumes.Add(60);
        for (var i = 39; i <= 49; i++) volumes.Add(90);

        var t0 = new DateTime(2026, 9, 4, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < prices.Count; i++)
            snaps.Add(Mk(prices[i], volumes[i], t0.AddMinutes(i)));

        var svc = CreateService(enableSimilarity: false);
        var sig = svc.DetectDoubleTop(snaps, snaps[^1].Price);

        Assert.Null(sig);
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-6, string? msg = null)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");
}
