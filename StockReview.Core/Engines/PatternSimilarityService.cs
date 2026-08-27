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
        // 双顶
        _templates["double_top"] = new PatternTemplate
        {
            Name = "双顶",
            Template = Normalize(new double[] { 0.2, 0.5, 0.9, 0.6, 0.3, 0.85, 0.95, 0.5, 0.2, 0.1 }),
            FeatureTemplate = new double[] { 0.9, 0.2, 0.85, -0.7, 0.65, -0.5, 0.8, 0.75 },
            KeyPoints = new() { ["leftPeak"] = 2, ["neck"] = 4, ["rightPeak"] = 6 }
        };
        // 顶背离
        _templates["top_divergence"] = new PatternTemplate
        {
            Name = "顶背离",
            Template = Normalize(new double[] { 0.3, 0.6, 0.75, 0.5, 0.2, 0.8, 0.95, 0.4, 0.15, 0.05 }),
            FeatureTemplate = new double[] { 0.75, 0.2, 0.95, -0.55, 0.75, -0.6, 0.7, 0.85 },
            KeyPoints = new() { ["peak1"] = 2, ["trough"] = 4, ["peak2"] = 6 }
        };
        // 钓鱼线
        _templates["fishing_line"] = new PatternTemplate
        {
            Name = "钓鱼线",
            Template = Normalize(new double[] { 0.1, 0.2, 0.4, 0.7, 0.95, 0.8, 0.5, 0.3, 0.15, 0.05 }),
            FeatureTemplate = new double[] { 0.95, 0.2, 0.1, 0.75, -0.85, 0.88, 0.9, 0.5 },
            KeyPoints = new() { ["surgeStart"] = 0, ["peak"] = 4, ["downEnd"] = 8 }
        };
        // 冲高回落
        _templates["surge_pullback"] = new PatternTemplate
        {
            Name = "冲高回落",
            Template = Normalize(new double[] { 0.2, 0.35, 0.55, 0.75, 0.9, 0.7, 0.5, 0.35, 0.2, 0.1 }),
            FeatureTemplate = new double[] { 0.9, 0.25, 0.15, 0.65, -0.75, 0.87, 0.8, 0.45 },
            KeyPoints = new() { ["base"] = 0, ["peak"] = 4, ["pullback"] = 8 }
        };
        // 高乖离回落
        _templates["high_deviation_pullback"] = new PatternTemplate
        {
            Name = "高乖离回落",
            Template = Normalize(new double[] { 0.3, 0.4, 0.55, 0.7, 0.92, 0.75, 0.55, 0.4, 0.25, 0.15 }),
            FeatureTemplate = new double[] { 0.92, 0.35, 0.2, 0.57, -0.7, 0.81, 0.75, 0.4, 0.8 },
            KeyPoints = new() { ["base"] = 0, ["peak"] = 4, ["pullback"] = 8 }
        };
        // 头肩顶
        _templates["head_shoulder"] = new PatternTemplate
        {
            Name = "头肩顶",
            Template = Normalize(new double[] { 0.3, 0.6, 0.5, 0.4, 0.8, 0.95, 0.7, 0.5, 0.3, 0.15, 0.4, 0.35, 0.2 }),
            FeatureTemplate = new double[] { 0.6, 0.4, 0.95, 0.5, 0.7, 0.9, 0.65, -0.5 },
            KeyPoints = new() { ["leftShoulder"] = 1, ["leftNeck"] = 3, ["head"] = 5,
                ["rightNeck"] = 7, ["rightShoulder"] = 10, ["breakdown"] = 11 }
        };
        // 平台跌破
        _templates["platform_break"] = new PatternTemplate
        {
            Name = "平台跌破",
            Template = Normalize(new double[] { 0.5, 0.52, 0.48, 0.51, 0.5, 0.49, 0.52, 0.5, 0.3, 0.15, 0.05 }),
            FeatureTemplate = new double[] { 0.5, 0.04, 0.35, -0.02, -0.7, 2.5, 0.7, 0.3 },
            KeyPoints = new() { ["platformStart"] = 0, ["platformEnd"] = 7, ["breakdown"] = 8 }
        };
        // 三重顶
        _templates["triple_top"] = new PatternTemplate
        {
            Name = "三重顶",
            Template = Normalize(new double[] { 0.2, 0.6, 0.9, 0.5, 0.2, 0.6, 0.88, 0.5, 0.2, 0.6, 0.85, 0.4, 0.1 }),
            FeatureTemplate = new double[] { 0.9, 0.2, 0.88, 0.2, 0.85, 0.8, 0.75, 0.7 },
            KeyPoints = new() { ["peak1"] = 2, ["trough1"] = 4, ["peak2"] = 6,
                ["trough2"] = 8, ["peak3"] = 10 }
        };
    }

    // ============ 核心计算方法 ============

    /// <summary>
    /// 归一化价格序列到 [0, 1]
    /// </summary>
    public static double[] Normalize(double[] prices)
    {
        if (prices == null || prices.Length == 0) return Array.Empty<double>();
        var min = prices.Min();
        var max = prices.Max();
        var range = max - min;
        if (range <= 0) return prices.Select(_ => 0.5).ToArray();
        return prices.Select(p => (p - min) / range).ToArray();
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
    /// DTW 距离
    /// </summary>
    public static double DtwDistance(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length == 0 || b.Length == 0) return double.MaxValue;
        var n = a.Length;
        var m = b.Length;
        var dp = new double[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
                dp[i, j] = double.MaxValue;
        dp[0, 0] = 0;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = Math.Abs(a[i - 1] - b[j - 1]);
                dp[i, j] = cost + Math.Min(Math.Min(dp[i - 1, j], dp[i, j - 1]), dp[i - 1, j - 1]);
            }
        }
        return dp[n, m];
    }

    /// <summary>
    /// DTW 相似度（归一化到 [0, 1]）
    /// </summary>
    public static double DtwSimilarity(double[] a, double[] b)
    {
        var dtw = DtwDistance(a, b);
        if (dtw == double.MaxValue) return 0;
        // 归一化：1 / (1 + dtw)
        return 1.0 / (1.0 + dtw);
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

            var downSlice = vols.Skip(peakIdx).Take(endIdx - peakIdx + 1);
            var downVolAvg = downSlice.Any() ? downSlice.Average() : 0;

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
