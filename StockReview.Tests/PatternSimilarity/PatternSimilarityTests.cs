using StockReview.Core.Engines;
using Xunit;

namespace StockReview.Tests.PatternSimilarity;

/// <summary>
/// PatternSimilarityService 跨语言回归测试。
///
/// 重要背景：C# PatternSimilarityService 并非 JS 原版的逐行移植，而是一套「简化重写」，
/// 其中部分方法与原 JS (src/stores/patternSimilarity.js) 算法不同：
///   - normalize: C# 用 min-max，JS 用 z-score  —— 分歧（本测试只锁定 C# 行为）
///   - dtwSimilarity: C# 用 1/(1+d)，JS 用 exp(-d*3) —— 分歧（本测试只锁定 C# 行为）
///   - emaSmooth: C# 默认 α=0.5（period=3），JS 默认自适应 0.2/0.3 —— 等价（测试传 α=0.5 对齐）
///   - resample / pearsonCorrelation / cosineSimilarity / dtwDistance: 两边公式等价
///
/// 等价子集的期望值来自 CrossLanguageBaseline/verify_pattern_js.mjs（原 JS 权威数值）。
/// 本测试确保「翻译/重写后的 C# 行为」不被后续改动悄悄破坏。
/// </summary>
public class PatternSimilarityTests
{
    private static readonly double[] A = { 10, 12, 11, 15, 14, 18, 16, 20 };
    private static readonly double[] B = { 20, 18, 16, 14, 15, 11, 12, 10 };
    private static readonly double[] C = { 10, 10.1, 9.9, 10.05, 10, 9.95, 10.02, 10 };

    private static void AssertClose(double expected, double actual, double tol = 1e-9, string? msg = null)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"{msg ?? "value"}: expected ~{expected}, actual {actual}");

    private static void AssertClose(double[] expected, double[] actual, double tol = 1e-9)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < tol,
                $"index {i}: expected ~{expected[i]}, actual {actual[i]}");
    }

    // ===== 等价子集：C# 应与原 JS 数值完全一致 =====

    [Fact]
    public void PearsonCorrelation_MatchesJsBaseline()
    {
        AssertClose(1.0, PatternSimilarityService.PearsonCorrelation(A, A));
        AssertClose(-0.9523809523809523, PatternSimilarityService.PearsonCorrelation(A, B));
        AssertClose(-0.01370570471345981, PatternSimilarityService.PearsonCorrelation(A, C));
    }

    [Fact]
    public void CosineSimilarity_MatchesJsBaseline()
    {
        AssertClose(1.0, PatternSimilarityService.CosineSimilarity(A, A), 1e-9, "A vs A");
        AssertClose(0.9071347678369195, PatternSimilarityService.CosineSimilarity(A, B), 1e-9, "A vs B");
    }

    [Fact]
    public void DtwDistance_Unconstrained_MatchesJsBaseline()
    {
        // JS 基线用 dtwDistance(A,B,Infinity,0) 关闭 band/psi 约束，等价于 C# 无约束 DTW
        AssertClose(0.0, PatternSimilarityService.DtwDistance(A, A));
        AssertClose(44.0, PatternSimilarityService.DtwDistance(A, B));
        AssertClose(35.980000000000004, PatternSimilarityService.DtwDistance(A, C));
    }

    [Fact]
    public void EmaSmooth_DefaultAlpha_MatchesJsWithAlpha05()
    {
        // C# 默认 period=3 → α=0.5；JS 默认自适应，需显式传 0.5 才能对齐
        var expected = new[] { 10.0, 11.0, 11.0, 13.0, 13.5, 15.75, 15.875, 17.9375 };
        AssertClose(expected, PatternSimilarityService.EmaSmooth(A, 3));
    }

    [Fact]
    public void Resample_Linear_MatchesJsBaseline()
    {
        var expected = new[] { 10.0, 12.333333333333334, 16.666666666666664, 20.0 };
        AssertClose(expected, PatternSimilarityService.Resample(A, 4));
    }

    // ===== 分歧子集：C# 故意重写，仅锁定 C# 当前行为（不要求等于 JS）=====

    [Fact]
    public void Normalize_UsesMinMaxMapping()
    {
        // C# min-max：min=10→0, max=20→1
        var expected = new[] { 0.0, 0.2, 0.1, 0.5, 0.4, 0.8, 0.6, 1.0 };
        AssertClose(expected, PatternSimilarityService.Normalize(A));
    }

    [Fact]
    public void DtwSimilarity_UsesOneOverOnePlusD()
    {
        // C# 公式 1/(1+d)，d 为无约束 DTW 距离
        AssertClose(1.0, PatternSimilarityService.DtwSimilarity(A, A));
        AssertClose(1.0 / (1.0 + 44.0), PatternSimilarityService.DtwSimilarity(A, B));
    }
}
