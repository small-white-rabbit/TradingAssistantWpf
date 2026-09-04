using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace StockReview.Core.Engines;

/// <summary>
/// IPatternSimilarityCalculator 适配器（接线层）
/// 把卖点检测引擎的领域类型（List&lt;double&gt;）转换为 PatternSimilarityService
/// 的输入类型（double[]）后委托计算，使 SellPointDetectorService._patternSimilarity
/// 不再为 null——此前 DI 只注册了具体类而接口未被注册，形态相似度防御层
/// 在生产环境从未生效（2026-09-04 一博科技双顶误报排查发现）。
/// </summary>
public class PatternSimilarityAdapter : IPatternSimilarityCalculator
{
    private readonly PatternSimilarityService _engine;

    public PatternSimilarityAdapter(PatternSimilarityService engine)
    {
        _engine = engine;
    }

    public (double similarity, object? details) CalculateSimilarity(
        List<double> prices, string patternType,
        Dictionary<string, int> keyPoints, List<double> volumes)
    {
        try
        {
            var result = _engine.CalculateSimilarity(
                prices?.ToArray() ?? Array.Empty<double>(),
                patternType,
                keyPoints,
                volumes?.ToArray());
            return (result.Similarity, result.Details);
        }
        catch (Exception ex)
        {
            // 适配器只做类型转换，异常属意外缺陷：降级放行（与多因子适配器降级策略一致），
            // 避免防御层自身故障静默杀死全部相似度门控信号
            Log.Warning(ex, "[形态相似度适配] 计算失败，降级放行 pattern={Pattern}", patternType);
            return (1.0, null);
        }
    }
}
