using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace StockReview.Core.Engines;

/// <summary>
/// 形态相似度计算服务 - 对应 Electron 版 patternSimilarity.js (1043行)
/// 三层防御体系：Pearson(25%) + DTW(45%) + 特征余弦(30%)
/// 8种标准形态模板 + 多输入分支架构
/// </summary>
public class PatternSimilarityService
{
    // ============ 标准形态模板 ============
    private readonly Dictionary<string, PatternTemplate> _templates = new();

    // ============ 各形态的分支索引 ============
    private static readonly Dictionary<string, BranchIndices> BranchIndexMap = new()
    {
        ["double_top"] = new() { Price = new[] { 0, 1, 2, 3, 4, 5 }, Vol = new[] { 6, 7 } },
        ["top_divergence"] = new() { Price = new[] { 0, 1, 2, 3, 4, 5 }, Vol = new[] { 6, 7 } },
        ["fishing_line"] = new() { Price = new[] { 0, 1, 2, 3, 4, 5 }, Vol = new[] { 6, 7 } },
        ["surge_pullback"] = new() { Price = new[] { 0, 1, 2, 3, 4, 5 }, Vol = new[] { 6, 7 } },
        ["high_deviation_pullback"] = new() { Price = new[] { 0, 1, 2, 3, 4, 5, 8 }, Vol = new[] { 6, 7 } },
        ["head_shoulder"] = new() { Price = new[] { 0, 1, 2, 3, 4, 7 }, Vol = new[] { 5, 6 } },
        ["platform_break"] = new() { Price = new[] { 0, 1, 2, 3, 4, 6, 7 }, Vol = new[] { 5 } },
        ["triple_top"] = new() { Price = new[] { 0, 1, 2, 3, 4 }, Vol = new[] { 5, 6, 7 } }
    };

    public PatternSimilarityService()
    {
        InitTemplates();
        Log.Information("[PatternSimilarity] 形态相似度计算器初始化完成, {Count} 个模板", _templates.Count);
    }

    private void InitTemplates()
    {
        // ===== 以下模板定义严格对齐 JS 原版 STANDARD_PATTERNS (patternSimilarity.js) =====
        // Template 为原始 [0,1] 手工形态序列（不再预 Normalize，归一化交由 Normalize() 在
        //   CalculateSimilarity 中统一处理，与 JS normalize(template.template) 一致）
        // FeatureTemplate 此处为 JS 原版手工标注值（含量能经验值），仅作量能维度回填源；
        //   PrecomputeTemplateFeatures() 会用 ExtractFeatures 重算价格维度，得到与候选同一
        //   z-score 空间的特征向量（修复 JS 记录的 Bug：手工 min-max 与候选 z-score 空间错配）

        // 双顶（17点）
        _templates["double_top"] = new PatternTemplate
        {
            Name = "双顶",
            Template = new[] { 0.3, 0.45, 0.6, 0.8, 1.0, 0.85, 0.6, 0.5, 0.65, 0.8, 0.95, 0.9, 0.75, 0.6, 0.45, 0.35, 0.3 },
            FeatureTemplate = new[] { 1.0, 0.2857, 0.9286, -0.2381, 0.2143, -0.1548, 0.9, 0.55 },
            KeyPoints = new() { ["leftPeak"] = 4, ["neck"] = 7, ["rightPeak"] = 10, ["breakdown"] = 16 }
        };
        // 顶背离（15点）
        _templates["top_divergence"] = new PatternTemplate
        {
            Name = "顶背离",
            Template = new[] { 0.3, 0.5, 0.7, 0.85, 0.8, 0.65, 0.7, 0.85, 0.95, 1.0, 0.9, 0.8, 0.7, 0.6, 0.55 },
            FeatureTemplate = new[] { 0.7857, 0.5, 1.0, -0.1429, 0.125, -0.1286, 0.85, 0.7 },
            KeyPoints = new() { ["peak1"] = 3, ["trough"] = 5, ["peak2"] = 9, ["current"] = 14 }
        };
        // 钓鱼线（20点）
        _templates["fishing_line"] = new PatternTemplate
        {
            Name = "钓鱼线",
            Template = new[] { 0.3, 0.35, 0.4, 0.45, 0.5, 0.55, 0.6, 0.7, 0.85, 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.65, 0.6, 0.55, 0.5 },
            FeatureTemplate = new[] { 1.0, 0.3571, 0.2857, 0.1607, -0.0714, 2.25, 0.95, 0.595 },
            KeyPoints = new() { ["surgeStart"] = 5, ["peak"] = 9, ["downEnd"] = 19 }
        };
        // 冲高回落（15点）
        _templates["surge_pullback"] = new PatternTemplate
        {
            Name = "冲高回落",
            Template = new[] { 0.3, 0.32, 0.35, 0.4, 0.5, 0.65, 0.85, 1.0, 0.9, 0.75, 0.6, 0.5, 0.45, 0.42, 0.4 },
            FeatureTemplate = new[] { 1.0, 0.0, 0.1429, 0.1429, -0.1224, 1.1667, 0.9, 0.6286 },
            KeyPoints = new() { ["base"] = 0, ["peak"] = 7, ["pullback"] = 14 }
        };
        // 高乖离回落（15点）
        _templates["high_deviation_pullback"] = new PatternTemplate
        {
            Name = "高乖离回落",
            Template = new[] { 0.3, 0.32, 0.35, 0.4, 0.45, 0.55, 0.7, 0.85, 1.0, 0.95, 0.85, 0.75, 0.65, 0.55, 0.5 },
            FeatureTemplate = new[] { 1.0, 0.0, 0.2857, 0.125, -0.119, 1.05, 0.95, 0.6583, 0.6 },
            KeyPoints = new() { ["base"] = 0, ["peak"] = 8, ["pullback"] = 14 }
        };
        // 头肩顶（20点）
        _templates["head_shoulder"] = new PatternTemplate
        {
            Name = "头肩顶",
            Template = new[] { 0.2, 0.4, 0.55, 0.6, 0.5, 0.35, 0.55, 0.75, 0.95, 1.0, 0.85, 0.6, 0.45, 0.55, 0.7, 0.6, 0.45, 0.35, 0.25, 0.2 },
            FeatureTemplate = new[] { 0.5, 0.1875, 1.0, 0.5, 0.625, 0.9, 0.6, -0.125 },
            KeyPoints = new() { ["leftShoulder"] = 3, ["leftNeck"] = 5, ["head"] = 9,
                ["rightNeck"] = 11, ["rightShoulder"] = 14, ["breakdown"] = 19 }
        };
        // 平台跌破（16点）
        _templates["platform_break"] = new PatternTemplate
        {
            Name = "平台跌破",
            Template = new[] { 0.5, 0.52, 0.48, 0.51, 0.49, 0.5, 0.47, 0.52, 0.5, 0.48, 0.5, 0.49, 0.45, 0.35, 0.25, 0.18 },
            FeatureTemplate = new[] { 0.9332, 0.1471, 0.9332, 0.0, -0.25, 0.54, 0.6875, 0.1875 },
            KeyPoints = new() { ["platformStart"] = 0, ["platformEnd"] = 10, ["breakdown"] = 13 }
        };
        // 三重顶（20点）
        _templates["triple_top"] = new PatternTemplate
        {
            Name = "三重顶",
            Template = new[] { 0.25, 0.4, 0.55, 0.7, 0.85, 0.95, 0.6, 0.4, 0.55, 0.7, 0.9, 0.6, 0.4, 0.55, 0.7, 0.9, 0.7, 0.5, 0.35, 0.25 },
            FeatureTemplate = new[] { 1.0, 0.2143, 0.9286, 0.2143, 0.9286, 0.85, 0.7, 0.6 },
            KeyPoints = new() { ["peak1"] = 5, ["trough1"] = 7, ["peak2"] = 10,
                ["trough2"] = 12, ["peak3"] = 15, ["breakdown"] = 19 }
        };

        // 预计算模板特征向量（修复 Bug A：使模板特征与候选处于同一 z-score 空间）
        PrecomputeTemplateFeatures();
    }

    /// <summary>
    /// 预计算所有模板的 z-score 归一化特征向量，对齐 JS 原版 _precomputeTemplateFeatures。
    /// 价格维度用 ExtractFeatures(模板形态, null, key, keyPoints) 重算（与候选同一空间）；
    /// 量能维度保留 InitTemplates 中手工标注的经验值（候选有真实量能，模板无量能数据）。
    /// </summary>
    private void PrecomputeTemplateFeatures()
    {
        foreach (var (key, template) in _templates)
        {
            if (template.Template == null || template.Template.Length == 0 || template.KeyPoints == null) continue;
            if (!BranchIndexMap.TryGetValue(key, out var indices)) continue;

            var originalFeatures = template.FeatureTemplate; // 手工标注的量能经验值
            var features = ExtractFeatures(template.Template, null, key, template.KeyPoints);
            if (features == null || features.Length == 0) continue;

            // 保留原手工量能维度，只替换价格维度
            if (originalFeatures != null && originalFeatures.Length == features.Length)
            {
                foreach (var volIdx in indices.Vol)
                {
                    if (volIdx < originalFeatures.Length && double.IsFinite(originalFeatures[volIdx]))
                    {
                        features[volIdx] = originalFeatures[volIdx];
                    }
                }
            }
            template.FeatureTemplate = features;
        }
    }

    // ============ 核心计算方法 ============

    /// <summary>
    /// 归一化价格序列到 [0, 1]。
    /// 与 JS 原版 (patternSimilarity.js) 对齐：先 z-score 标准化，再按 mean±3std 线性映射到 [0,1]。
    /// 相比 min-max 对异常值（急拉尖峰）更鲁棒，且值域与标准模板一致。
    /// </summary>
    public static double[] Normalize(double[] prices)
    {
        if (prices == null || prices.Length == 0) return Array.Empty<double>();
        if (prices.Length == 1) return new[] { 0.5 };

        // 过滤 NaN/Infinity，防止污染后续计算（JS：validPrices = prices.filter(Number.isFinite)）
        var valid = prices.Where(double.IsFinite).ToArray();
        if (valid.Length == 0 || valid.Length == 1) return prices.Select(_ => 0.5).ToArray();

        var mean = valid.Average();
        var variance = valid.Select(v => (v - mean) * (v - mean)).Average();
        var std = Math.Sqrt(variance);
        if (std == 0) return prices.Select(_ => 0.5).ToArray(); // 完全横盘

        return prices.Select(p =>
        {
            if (!double.IsFinite(p)) return 0.5; // 非法值兜底为中性
            var z = (p - mean) / std;
            return Math.Max(0, Math.Min(1, (z + 3) / 6));
        }).ToArray();
    }

    /// <summary>
    /// EMA 平滑
    /// </summary>
    public static double[] EmaSmooth(double[] data, int period = 3)
    {
        if (data == null || data.Length == 0) return Array.Empty<double>();
        var result = new double[data.Length];
        var alpha = 2.0 / (period + 1);
        result[0] = data[0];
        for (var i = 1; i < data.Length; i++)
        {
            result[i] = alpha * data[i] + (1 - alpha) * result[i - 1];
        }
        return result;
    }

    /// <summary>
    /// 重采样到指定长度（线性插值）
    /// </summary>
    public static double[] Resample(double[] data, int targetLen)
    {
        if (data == null || data.Length == 0 || targetLen <= 0) return Array.Empty<double>();
        if (data.Length == targetLen) return data;

        var result = new double[targetLen];
        for (var i = 0; i < targetLen; i++)
        {
            var ratio = (double)i / (targetLen - 1) * (data.Length - 1);
            var idx = (int)Math.Floor(ratio);
            var frac = ratio - idx;
            if (idx >= data.Length - 1)
                result[i] = data[^1];
            else
                result[i] = data[idx] * (1 - frac) + data[idx + 1] * frac;
        }
        return result;
    }

    /// <summary>
    /// Pearson 相关系数
    /// </summary>
    public static double PearsonCorrelation(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0;
        var n = a.Length;
        var sumA = a.Sum();
        var sumB = b.Sum();
        var sumAB = a.Zip(b, (x, y) => x * y).Sum();
        var sumA2 = a.Select(x => x * x).Sum();
        var sumB2 = b.Select(x => x * x).Sum();

        var numerator = n * sumAB - sumA * sumB;
        var denominator = Math.Sqrt((n * sumA2 - sumA * sumA) * (n * sumB2 - sumB * sumB));
        if (denominator == 0) return 0;
        return numerator / denominator;
    }

    /// <summary>
    /// JS 原版 dtwDistance 的 Sakoe-Chiba band 窗口宽度（允许时间扭曲的最大幅度，采样点）
    /// </summary>
    public const int DtwWindow = 10;

    /// <summary>
    /// JS 原版 dtwDistance 的 psi 约束：序列两端各允许免费跳过的最大点数
    /// </summary>
    public const int DtwPsi = 5;

    /// <summary>
    /// DTW 距离（带 Sakoe-Chiba band + psi 约束，与 JS 原版对齐）。
    /// band 限制 |i-j|&lt;=window，复杂度从 O(n*m) 降到 O(n*window)；
    /// psi 允许两端各跳过 psi 个点不产生代价，使匹配更关注形态主体而非边界精度。
    /// 传 window=+∞、psi=0 可退化为无约束 DTW。
    /// </summary>
    public static double DtwDistance(double[] a, double[] b, double? window = null, int? psi = null)
    {
        if (a == null || b == null || a.Length == 0 || b.Length == 0) return double.PositiveInfinity;
        var n = a.Length;
        var m = b.Length;

        // 窗口宽度：取配置值与序列长度比例的较小值，避免窗口过大
        var w = Math.Min(window ?? DtwWindow, Math.Abs(n - m) + Math.Min(n, m));
        // psi 约束：两端允许跳过的点数，取配置值与序列长度 1/4 的较小值
        var p = Math.Min(psi ?? DtwPsi, (int)Math.Floor((double)Math.Min(n, m) / 4));

        // 滚动数组优化空间：只用两行（前一行 + 当前行）
        var prev = new double[m + 1];
        var curr = new double[m + 1];
        for (var j = 0; j <= m; j++) { prev[j] = double.PositiveInfinity; curr[j] = double.PositiveInfinity; }

        // psi 约束初始化：前 psi 列设为 0，允许序列 A 前 psi 个点免费跳过
        prev[0] = 0;
        for (var j = 1; j <= p; j++) prev[j] = 0;

        for (var i = 1; i <= n; i++)
        {
            // psi 约束：第一列前 psi 行设为 0（允许 B 序列前 psi 个点跳过）
            curr[0] = i <= p ? 0 : double.PositiveInfinity;
            // Sakoe-Chiba band：j 的范围限制在 [i-w, i+w] 内
            var jStart = Math.Max(1, i - w);
            var jEnd = Math.Min(m, i + w);
            for (var j = 1; j <= m; j++)
            {
                if (j < jStart || j > jEnd)
                {
                    curr[j] = double.PositiveInfinity;
                    continue;
                }
                var cost = Math.Abs(a[i - 1] - b[j - 1]);
                curr[j] = cost + Math.Min(Math.Min(prev[j], curr[j - 1]), prev[j - 1]);
            }
            // 交换 prev / curr
            (prev, curr) = (curr, prev);
        }

        // psi 约束结果：取最后 psi 列的最小值，而非仅 prev[m]
        var result = prev[m];
        for (var j = m - 1; j >= Math.Max(1, m - p); j--)
            if (prev[j] < result) result = prev[j];
        return result;
    }

    /// <summary>
    /// DTW 相似度（将距离映射到 [0, 1]，与 JS 原版对齐）。
    /// 用序列长度归一化距离得到平均单点距离，再用指数衰减 exp(-avgDistance*3) 增强区分度。
    /// </summary>
    public static double DtwSimilarity(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length < 3 || b.Length < 3) return 0;
        var dtw = DtwDistance(a, b);
        if (!double.IsFinite(dtw)) return 0;

        // 归一化：用序列长度归一化距离，得到平均单点距离
        var maxLen = Math.Max(a.Length, b.Length);
        var avgDistance = dtw / maxLen;

        // 指数衰减：avgDistance=0 时相似度=1；scale=3 增强区分度
        var similarity = Math.Exp(-avgDistance * 3);
        return Math.Max(0, Math.Min(1, similarity));
    }

    /// <summary>
    /// 余弦相似度（映射到 [0, 1]）
    /// </summary>
    public static double CosineSimilarity(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0;
        var dot = a.Zip(b, (x, y) => x * y).Sum();
        var normA = Math.Sqrt(a.Select(x => x * x).Sum());
        var normB = Math.Sqrt(b.Select(x => x * x).Sum());
        if (normA == 0 || normB == 0) return 0;
        return Math.Max(0, dot / (normA * normB));
    }

    // ============ 形态约束检查 ============

    public bool CheckConstraints(string patternType, Dictionary<string, int> keyPoints, double[] normalized)
    {
        if (keyPoints == null || normalized == null) return true;

        if (patternType == "double_top" || patternType == "top_divergence")
        {
            var lp = keyPoints.GetValueOrDefault("leftPeak", keyPoints.GetValueOrDefault("peak1", -1));
            var nk = keyPoints.GetValueOrDefault("neck", keyPoints.GetValueOrDefault("trough", -1));
            var rp = keyPoints.GetValueOrDefault("rightPeak", keyPoints.GetValueOrDefault("peak2", -1));
            if (lp < 0 || nk < 0 || rp < 0) return true;
            if (!(lp < nk && nk < rp)) return false;
            if (patternType == "double_top")
            {
                if (normalized[lp] < normalized[rp] - 0.05) return false;
                if (normalized[rp] < normalized[lp] * 0.6) return false;
            }
            else
            {
                if (normalized[rp] <= normalized[lp]) return false;
            }
            return true;
        }

        if (patternType == "fishing_line")
        {
            var ss = keyPoints.GetValueOrDefault("surgeStart", -1);
            var pk = keyPoints.GetValueOrDefault("peak", -1);
            var de = keyPoints.GetValueOrDefault("downEnd", -1);
            if (ss < 0 || pk < 0 || de < 0) return true;
            return ss < pk && pk < de;
        }

        if (patternType == "surge_pullback" || patternType == "high_deviation_pullback")
        {
            var b = keyPoints.GetValueOrDefault("base", -1);
            var pk = keyPoints.GetValueOrDefault("peak", -1);
            var pb = keyPoints.GetValueOrDefault("pullback", -1);
            if (b < 0 || pk < 0 || pb < 0) return true;
            return b < pk && pk < pb;
        }

        if (patternType == "triple_top")
        {
            var p1 = keyPoints.GetValueOrDefault("peak1", -1);
            var p2 = keyPoints.GetValueOrDefault("peak2", -1);
            var p3 = keyPoints.GetValueOrDefault("peak3", -1);
            if (p1 < 0 || p2 < 0 || p3 < 0) return true;
            if (!(p1 < p2 && p2 < p3)) return false;
            var maxP = Math.Max(normalized[p1], Math.Max(normalized[p2], normalized[p3]));
            var minP = Math.Min(normalized[p1], Math.Min(normalized[p2], normalized[p3]));
            if (maxP - minP > 0.15) return false;
            return true;
        }

        if (patternType == "head_shoulder")
        {
            var ls = keyPoints.GetValueOrDefault("leftShoulder", -1);
            var hd = keyPoints.GetValueOrDefault("head", -1);
            var rs = keyPoints.GetValueOrDefault("rightShoulder", -1);
            if (ls < 0 || hd < 0 || rs < 0) return true;
            if (!(ls < hd && hd < rs)) return false;
            var hv = normalized[hd];
            var lv = normalized[ls];
            var rv = normalized[rs];
            if (hv < lv || hv < rv) return false;
            if (hv - lv < 0.15 || hv - rv < 0.15) return false;
            if (Math.Abs(lv - rv) > 0.2) return false;
            return true;
        }

        if (patternType == "platform_break")
        {
            var ps = keyPoints.GetValueOrDefault("platformStart", -1);
            var pe = keyPoints.GetValueOrDefault("platformEnd", -1);
            var bd = keyPoints.GetValueOrDefault("breakdown", -1);
            if (ps < 0 || pe < 0 || bd < 0) return true;
            return ps < pe && bd > pe;
        }

        return true;
    }

    // ============ 特征提取 ============

    public double[]? ExtractFeatures(double[] prices, double[]? volumes, string patternType,
        Dictionary<string, int> keyPoints)
    {
        if (prices == null || prices.Length == 0 || keyPoints == null) return null;

        var normalized = Normalize(prices);
        var vols = volumes ?? new double[prices.Length];
        var maxVol = Math.Max(vols.Max(), 1.0);

        if (patternType == "double_top" || patternType == "top_divergence")
        {
            var lp = keyPoints.GetValueOrDefault("leftPeak", keyPoints.GetValueOrDefault("peak1", -1));
            var nk = keyPoints.GetValueOrDefault("neck", keyPoints.GetValueOrDefault("trough", -1));
            var rp = keyPoints.GetValueOrDefault("rightPeak", keyPoints.GetValueOrDefault("peak2", -1));
            if (lp < 0 || nk < 0 || rp < 0) return null;
            var peak1 = normalized[lp];
            var trough = normalized[nk];
            var peak2 = normalized[rp];
            var curr = normalized[prices.Length - 1];
            return new[]
            {
                peak1, trough, peak2,
                (trough - peak1) / Math.Max(1, nk - lp),
                (peak2 - trough) / Math.Max(1, rp - nk),
                (curr - peak2) / Math.Max(1, prices.Length - 1 - rp),
                vols[lp] / maxVol, vols[rp] / maxVol
            };
        }

        if (patternType == "fishing_line" || patternType == "surge_pullback" || patternType == "high_deviation_pullback")
        {
            var peakIdx = keyPoints.GetValueOrDefault("peak", -1);
            var startIdx = patternType == "fishing_line"
                ? keyPoints.GetValueOrDefault("surgeStart", 0)
                : keyPoints.GetValueOrDefault("base", 0);
            var endIdx = patternType == "fishing_line"
                ? keyPoints.GetValueOrDefault("downEnd", prices.Length - 1)
                : keyPoints.GetValueOrDefault("pullback", prices.Length - 1);
            if (peakIdx < 0 || startIdx < 0 || endIdx < 0) return null;

            var peakVal = normalized[peakIdx];
            var startVal = normalized[startIdx];
            var endVal = normalized[endIdx];
            var surgeSlope = (peakVal - startVal) / Math.Max(1, peakIdx - startIdx);
            var downSlope = (endVal - peakVal) / Math.Max(1, endIdx - peakIdx);
            var slopeRatio = Math.Abs(surgeSlope) / Math.Max(0.001, Math.Abs(downSlope));

            var downSlice = vols.Skip(peakIdx).Take(endIdx - peakIdx + 1).ToArray();
            // 与 JS 原版一致：回落均量除数用 (endIdx - peakIdx)，而非元素个数（属 JS 既有行为，保持对齐）
            var downVolAvg = downSlice.Length > 0 ? downSlice.Sum() / Math.Max(1, endIdx - peakIdx) : 0;

            var baseFeatures = new List<double>
            {
                peakVal, startVal, endVal, surgeSlope, downSlope, slopeRatio,
                vols[peakIdx] / maxVol, downVolAvg / maxVol
            };

            if (patternType == "high_deviation_pullback")
            {
                var peakPrice = prices[peakIdx];
                var meanPrice = prices.Average();
                var deviationPct = meanPrice > 0 ? ((peakPrice - meanPrice) / meanPrice) * 100 : 0;
                var deviationNorm = Math.Max(0, Math.Min(1, deviationPct / 5));
                baseFeatures.Add(deviationNorm);
            }
            return baseFeatures.ToArray();
        }

        if (patternType == "head_shoulder")
        {
            var ls = keyPoints.GetValueOrDefault("leftShoulder", -1);
            var ln = keyPoints.GetValueOrDefault("leftNeck", 0);
            var hd = keyPoints.GetValueOrDefault("head", -1);
            var rn = keyPoints.GetValueOrDefault("rightNeck", 0);
            var rs = keyPoints.GetValueOrDefault("rightShoulder", -1);
            if (ls < 0 || hd < 0 || rs < 0) return null;
            var bdSlope = (normalized[prices.Length - 1] - normalized[rs]) /
                Math.Max(1, prices.Length - 1 - rs);
            return new[]
            {
                normalized[ls], normalized[ln], normalized[hd], normalized[rn], normalized[rs],
                vols[hd] / maxVol, vols[rs] / maxVol, bdSlope
            };
        }

        if (patternType == "platform_break")
        {
            var ps = keyPoints.GetValueOrDefault("platformStart", -1);
            var pe = keyPoints.GetValueOrDefault("platformEnd", -1);
            var bd = keyPoints.GetValueOrDefault("breakdown", -1);
            if (ps < 0 || pe < 0 || bd < 0) return null;
            var platSlice = normalized.Skip(ps).Take(pe - ps + 1).ToArray();
            var platMean = platSlice.Average();
            var platVol = platSlice.Length > 0 ? platSlice.Max() - platSlice.Min() : 0.0;
            var bdDepth = platMean - normalized[prices.Length - 1];
            var platSlope = platSlice.Length > 1
                ? (platSlice[^1] - platSlice[0]) / (platSlice.Length - 1) : 0;
            var bdSlope = (normalized[prices.Length - 1] - normalized[bd]) /
                Math.Max(1, prices.Length - 1 - bd);
            var platVolMean = vols.Skip(ps).Take(pe - ps + 1).Average();
            var bdVol = vols[bd];
            var totalLen = prices.Length;
            return new[]
            {
                platMean, platVol, bdDepth, platSlope, bdSlope,
                bdVol / Math.Max(0.001, platVolMean),
                (double)(pe - ps + 1) / totalLen,
                (double)(totalLen - bd) / totalLen
            };
        }

        if (patternType == "triple_top")
        {
            var p1 = keyPoints.GetValueOrDefault("peak1", -1);
            var p2 = keyPoints.GetValueOrDefault("peak2", -1);
            var p3 = keyPoints.GetValueOrDefault("peak3", -1);
            if (p1 < 0 || p2 < 0 || p3 < 0) return null;
            var t1 = keyPoints.GetValueOrDefault("trough1", (p1 + p2) / 2);
            var t2 = keyPoints.GetValueOrDefault("trough2", (p2 + p3) / 2);
            return new[]
            {
                normalized[p1], normalized[t1], normalized[p2], normalized[t2], normalized[p3],
                vols[p1] / maxVol, vols[p2] / maxVol, vols[p3] / maxVol
            };
        }

        return null;
    }

    // ============ 关键点匹配分数 ============

    public double KeyPointMatch(double[] candidate, PatternTemplate template,
        Dictionary<string, int>? candidateKeyPoints)
    {
        if (candidateKeyPoints == null || template.KeyPoints == null) return 0.5;

        var templateLen = template.Template.Length;
        var candidateLen = candidate.Length;
        var matchScore = 0.0;
        var keyCount = 0;

        foreach (var (keyName, templateIdx) in template.KeyPoints)
        {
            if (!candidateKeyPoints.TryGetValue(keyName, out var candidateIdx)) continue;
            var templatePos = (double)templateIdx / templateLen;
            var candidatePos = (double)candidateIdx / candidateLen;
            var posDiff = Math.Abs(templatePos - candidatePos);
            var posScore = Math.Max(0, 1 - posDiff * 2);
            matchScore += posScore;
            keyCount++;
        }
        return keyCount > 0 ? matchScore / keyCount : 0.5;
    }

    // ============ 振幅合理性分数 ============

    public double AmplitudeScore(double[]? candidateRaw)
    {
        if (candidateRaw == null || candidateRaw.Length == 0) return 0;
        var min = candidateRaw.Min();
        var max = candidateRaw.Max();
        if (min <= 0) return 0;
        var amplitude = ((max - min) / min) * 100;
        if (amplitude < 0.5) return 0.2;
        if (amplitude > 8) return 0.5;
        if (amplitude >= 1 && amplitude <= 5) return 1.0;
        return 0.7;
    }

    // ============ 多输入分支相似度 ============

    public BranchSimilarityResult CalculateBranchSimilarity(
        double[] candidateFeatures, double[] templateFeatures, string patternType)
    {
        if (!BranchIndexMap.TryGetValue(patternType, out var indices))
        {
            // 未知形态：前6维价格 + 后2维量能
            var priceDims = 6;
            var priceCand = candidateFeatures.Take(priceDims).ToArray();
            var priceTpl = templateFeatures.Take(priceDims).ToArray();
            var priceBranch = CosineSimilarity(priceCand, priceTpl);
            double? volumeBranch = null;
            if (candidateFeatures.Length > priceDims && templateFeatures.Length > priceDims)
            {
                volumeBranch = CosineSimilarity(
                    candidateFeatures.Skip(priceDims).ToArray(),
                    templateFeatures.Skip(priceDims).ToArray());
            }
            var merged = volumeBranch.HasValue
                ? priceBranch * 0.7 + volumeBranch.Value * 0.3
                : priceBranch;
            return new() { Merged = merged, PriceBranch = priceBranch, VolumeBranch = volumeBranch };
        }

        var pc = indices.Price.Where(i => i < candidateFeatures.Length && i < templateFeatures.Length)
            .Select(i => candidateFeatures[i]).ToArray();
        var pt = indices.Price.Where(i => i < candidateFeatures.Length && i < templateFeatures.Length)
            .Select(i => templateFeatures[i]).ToArray();
        var priceBranchVal = pc.Length > 0 && pc.Length == pt.Length
            ? CosineSimilarity(pc, pt) : 0.5;

        double? volBranch = null;
        var vc = indices.Vol.Where(i => i < candidateFeatures.Length && i < templateFeatures.Length)
            .Select(i => candidateFeatures[i]).ToArray();
        var vt = indices.Vol.Where(i => i < candidateFeatures.Length && i < templateFeatures.Length)
            .Select(i => templateFeatures[i]).ToArray();
        if (vc.Length > 0 && vc.Length == vt.Length)
        {
            volBranch = CosineSimilarity(vc, vt);
        }

        var mergedVal = volBranch.HasValue
            ? priceBranchVal * 0.7 + volBranch.Value * 0.3
            : priceBranchVal;

        return new() { Merged = mergedVal, PriceBranch = priceBranchVal, VolumeBranch = volBranch };
    }

    // ============ 主入口：综合相似度计算 ============

    public SimilarityResult CalculateSimilarity(double[] candidatePrices, string patternType,
        Dictionary<string, int>? candidateKeyPoints = null, double[]? candidateVolumes = null)
    {
        if (!_templates.TryGetValue(patternType, out var template) ||
            candidatePrices == null || candidatePrices.Length < 5)
        {
            return new() { Similarity = 0, Details = new() { Reason = "invalid_input" } };
        }

        // 1. 归一化
        var normalizedCandidate = Normalize(candidatePrices);
        var normalizedTemplate = Normalize(template.Template);

        // 2. 重采样
        var templateLen = template.Template.Length;
        var resampledCandidate = Resample(normalizedCandidate, templateLen);

        // 第一层：Pearson (25%)
        var pearson = PearsonCorrelation(resampledCandidate, normalizedTemplate);
        var pearsonScore = Math.Max(0, pearson);

        // 第二层：DTW (45%)
        var dtwScore = DtwSimilarity(resampledCandidate, normalizedTemplate);

        // 第三层：特征余弦 (30%)
        var featureScore = 0.5;
        double? volumeBranchScore = null;
        var constraintPassed = true;
        double[]? candidateFeatures = null;

        if (candidateKeyPoints != null && template.FeatureTemplate != null)
        {
            constraintPassed = CheckConstraints(patternType, candidateKeyPoints, normalizedCandidate);
            if (constraintPassed)
            {
                candidateFeatures = ExtractFeatures(candidatePrices, candidateVolumes, patternType, candidateKeyPoints);
                if (candidateFeatures != null && candidateFeatures.Length == template.FeatureTemplate.Length)
                {
                    var branchResult = CalculateBranchSimilarity(candidateFeatures, template.FeatureTemplate, patternType);
                    featureScore = branchResult.Merged;
                    volumeBranchScore = branchResult.VolumeBranch;
                }
            }
        }

        if (!constraintPassed)
        {
            return new()
            {
                Similarity = 0,
                Details = new()
                {
                    Pearson = pearson, PearsonScore = pearsonScore, DtwScore = dtwScore,
                    FeatureScore = 0, VolumeBranchScore = null,
                    ConstraintPassed = false, CandidateFeatures = null,
                    KeyPointScore = KeyPointMatch(resampledCandidate, template, candidateKeyPoints),
                    AmpScore = AmplitudeScore(candidatePrices),
                    PatternType = patternType, PatternName = template.Name
                }
            };
        }

        // 综合相似度
        var similarity = pearsonScore * 0.25 + dtwScore * 0.45 + featureScore * 0.30;

        return new()
        {
            Similarity = similarity,
            Details = new()
            {
                Pearson = pearson, PearsonScore = pearsonScore, DtwScore = dtwScore,
                FeatureScore = featureScore, VolumeBranchScore = volumeBranchScore,
                ConstraintPassed = constraintPassed, CandidateFeatures = candidateFeatures,
                KeyPointScore = KeyPointMatch(resampledCandidate, template, candidateKeyPoints),
                AmpScore = AmplitudeScore(candidatePrices),
                PatternType = patternType, PatternName = template.Name
            }
        };
    }

    /// <summary>
    /// 检查形态相似度是否达标
    /// </summary>
    public bool IsPatternSimilar(double[] candidatePrices, string patternType,
        Dictionary<string, int>? candidateKeyPoints = null, double threshold = 0.6)
    {
        var result = CalculateSimilarity(candidatePrices, patternType, candidateKeyPoints);
        return result.Similarity >= threshold;
    }

    /// <summary>
    /// 获取所有模板
    /// </summary>
    public IReadOnlyDictionary<string, PatternTemplate> GetTemplates() => _templates;
}

// ============ 数据模型 ============

public class PatternTemplate
{
    public string Name { get; set; } = "";
    public double[] Template { get; set; } = Array.Empty<double>();
    public double[]? FeatureTemplate { get; set; }
    public Dictionary<string, int> KeyPoints { get; set; } = new();
}

public class BranchIndices
{
    public int[] Price { get; set; } = Array.Empty<int>();
    public int[] Vol { get; set; } = Array.Empty<int>();
}

public class BranchSimilarityResult
{
    public double Merged { get; set; }
    public double PriceBranch { get; set; }
    public double? VolumeBranch { get; set; }
}

public class SimilarityResult
{
    public double Similarity { get; set; }
    public SimilarityDetails Details { get; set; } = new();
}

public class SimilarityDetails
{
    public double Pearson { get; set; }
    public double PearsonScore { get; set; }
    public double DtwScore { get; set; }
    public double FeatureScore { get; set; }
    public double? VolumeBranchScore { get; set; }
    public bool ConstraintPassed { get; set; } = true;
    public double[]? CandidateFeatures { get; set; }
    public double KeyPointScore { get; set; }
    public double AmpScore { get; set; }
    public string PatternType { get; set; } = "";
    public string PatternName { get; set; } = "";
    public string? Reason { get; set; }
}
