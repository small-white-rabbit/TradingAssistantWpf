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
///   - 模板库 + 第三层特征: InitTemplates 改为 JS 原版形态序列/关键点，新增 PrecomputeTemplateFeatures
///     用 ExtractFeatures 重算价格维度、保留手工量能经验值，使特征余弦与候选同处 z-score 空间
///     （修复 JS 记录的 Bug：手工 min-max 特征与候选 z-score 空间错配，导致第三层相似度偏差）
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

    // ===== 第三层特征模板预计算：与 JS 原版 _precomputeTemplateFeatures 对齐 =====

    // 来自 JS 原版 _precomputeTemplateFeatures 的金标准（四舍五入 6 位）。
    // 价格维度由 ExtractFeatures 在 z-score 空间重算，量能维度保留手工经验值。
    private static readonly Dictionary<string, double[]> ExpectedJsTemplateFeatures = new()
    {
        ["double_top"] = new[] { 0.775771, 0.394624, 0.737656, -0.127049, 0.114344, -0.082582, 0.9, 0.55 },
        ["fishing_line"] = new[] { 0.797994, 0.417576, 0.375307, 0.095104, -0.042269, 2.25, 0.95, 0.595 },
        ["surge_pullback"] = new[] { 0.840348, 0.299704, 0.376939, 0.077235, -0.066201, 1.166667, 0.9, 0.6286 },
        ["top_divergence"] = new[] { 0.617486, 0.431982, 0.756613, -0.092752, 0.081158, -0.083477, 0.85, 0.7 },
        ["head_shoulder"] = new[] { 0.543263, 0.355164, 0.844221, 0.543263, 0.618502, 0.9, 0.6, -0.07524 },
        ["platform_break"] = new[] { 0.581986, 0.085584, 0.543072, 0, -0.145494, 0.54, 0.6875, 0.1875 },
        ["triple_top"] = new[] { 0.791386, 0.346213, 0.750916, 0.346213, 0.750916, 0.85, 0.7, 0.6 },
        ["high_deviation_pullback"] = new[] { 0.789894, 0.267787, 0.41696, 0.065263, -0.062156, 1.05, 0.95, 0.6583, 1 }
    };

    [Fact]
    public void TemplateFeatures_Precomputed_MatchJsBaseline()
    {
        // 模板形态/关键点已对齐 JS 原版，且 InitTemplates 末尾调用 PrecomputeTemplateFeatures，
        // 用 ExtractFeatures 重算价格维度、保留手工量能经验值。预计算后的 featureTemplate
        // 应与 JS 原版 _precomputeTemplateFeatures 输出完全一致。
        var svc = new PatternSimilarityService();
        var templates = svc.GetTemplates();

        foreach (var (key, expected) in ExpectedJsTemplateFeatures)
        {
            Assert.True(templates.ContainsKey(key), $"缺少模板 {key}");
            var actual = templates[key].FeatureTemplate;
            Assert.NotNull(actual);
            Assert.Equal(expected.Length, actual!.Length);
            AssertClose(expected, actual, 1e-6);
        }
    }

    [Fact]
    public void CalculateSimilarity_SurgePullbackSelfMatch_MatchesJsBaseline()
    {
        // 端到端：候选 = JS 原版 surge_pullback 模板自身，量能全设 1.0（使量能分支有确定值）。
        // 期望值来自 JS 原版 calculateSimilarity，验证三层融合（含第三层预计算特征）整体对齐。
        var svc = new PatternSimilarityService();
        var prices = new[] { 0.3, 0.32, 0.35, 0.4, 0.5, 0.65, 0.85, 1.0, 0.9, 0.75, 0.6, 0.5, 0.45, 0.42, 0.4 };
        var vols = new double[prices.Length];
        Array.Fill(vols, 1.0);
        var kp = new Dictionary<string, int> { ["base"] = 0, ["peak"] = 7, ["pullback"] = 14 };

        var res = svc.CalculateSimilarity(prices, "surge_pullback", kp, vols);
        AssertClose(0.9973713033589424, res.Similarity, 1e-6, "similarity");
        AssertClose(0.9912376778631415, res.Details.FeatureScore, 1e-6, "featureScore");
        AssertClose(0.9707922595438055, res.Details.VolumeBranchScore ?? 0, 1e-6, "volumeBranch");
    }
}
