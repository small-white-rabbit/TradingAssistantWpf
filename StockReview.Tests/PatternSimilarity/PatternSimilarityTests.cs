using StockReview.Core.Engines;
using Xunit;

namespace StockReview.Tests.PatternSimilarity;

/// <summary>
/// PatternSimilarityService 跨语言回归测试。
///
/// 背景：C# PatternSimilarityService 最初是一套「简化重写」，其中 normalize（min-max）与
/// dtwSimilarity（1/(1+d)）与原 JS (src/stores/patternSimilarity.js) 算法不同。
/// 经核对确认这并非有意改进，已于 2026-08-27 按 JS 原版对齐：
///   - normalize: 改为 z-score 标准化后按 mean±3std 映射 [0,1]（与 JS 一致）
///   - dtwDistance: 加入 Sakoe-Chiba band(window=10) + psi(psi=5) 约束（与 JS 一致）
///   - dtwSimilarity: 改为 exp(-avgDistance*3) 长度归一指数衰减（与 JS 一致）
///
/// 所有期望值来自 CrossLanguageBaseline/verify_pattern_js.mjs（原 JS 权威数值）或本文件内
/// 标注的 JS 基准。本测试确保「与 JS 原版对齐后的 C# 行为」不被后续改动悄悄破坏。
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

    // ===== 等价子集：C# 与 JS 原版数值完全一致 =====

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
        // 显式传 window=+∞、psi=0 关闭 band/psi 约束，等价于 JS dtwDistance(A,B,Infinity,0)
        AssertClose(0.0, PatternSimilarityService.DtwDistance(A, A, double.PositiveInfinity, 0));
        AssertClose(44.0, PatternSimilarityService.DtwDistance(A, B, double.PositiveInfinity, 0));
        AssertClose(35.980000000000004, PatternSimilarityService.DtwDistance(A, C, double.PositiveInfinity, 0));
    }

    [Fact]
    public void DtwDistance_BandedDefault_MatchesJsBaseline()
    {
        // 默认 window=10、psi=5（Sakoe-Chiba band + psi），与 JS dtwDistance(A,B) 默认行为一致
        AssertClose(0.0, PatternSimilarityService.DtwDistance(A, A));
        AssertClose(25.0, PatternSimilarityService.DtwDistance(A, B));
        AssertClose(34.0, PatternSimilarityService.DtwDistance(A, C));
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

    // ===== 已对齐子集：C# 现在应与 JS 原版数值完全一致 =====

    [Fact]
    public void Normalize_MatchesJsZScoreBaseline()
    {
        // JS 原版：z-score 标准化后按 mean±3std 线性映射到 [0,1]
        var expected = new[]
        {
            0.26854497505686215,
            0.37141387503159007,
            0.3199794250442261,
            0.525717224993682,
            0.47428277500631805,
            0.6800205749557738,
            0.5771516749810459,
            0.7828894749305019
        };
        AssertClose(expected, PatternSimilarityService.Normalize(A));
    }

    [Fact]
    public void DtwSimilarity_MatchesJsExpDecayBaseline()
    {
        // JS 原版：exp(-avgDistance*3)，avgDistance = dtw / maxLen（带默认 band/psi）
        AssertClose(1.0, PatternSimilarityService.DtwSimilarity(A, A));
        AssertClose(0.00008481823524646916, PatternSimilarityService.DtwSimilarity(A, B));
    }
}
