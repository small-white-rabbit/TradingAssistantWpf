using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Services;
using StockReview.Mcp;

namespace StockReview.Mcp.Tools;

[McpServerToolType]
internal class PlanTools
{
    private readonly TradePlanService _plan;

    public PlanTools(TradePlanService plan) => _plan = plan;

    [McpServerTool(Name = "get_trade_plans")]
    [Description("查询交易计划。不传参数返回监控中的计划列表；planId 精确查单个；status 按状态字段做包含匹配过滤。计划含入场理由、目标价、止损价、监控点等字段。")]
    public string GetTradePlans(
        [Description("计划 id，精确查询单个计划（返回全字段）")] string? planId = null,
        [Description("状态过滤，对 Status 字段不区分大小写包含匹配")] string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(planId))
        {
            var plan = _plan.GetPlan(planId);
            if (plan == null)
                throw new McpException($"未找到计划 {planId}");
            return McpJson.Serialize(plan);
        }

        var plans = _plan.GetMonitoringPlans();
        if (!string.IsNullOrWhiteSpace(status))
            plans = plans
                .Where(p => p.Status != null &&
                            p.Status.Contains(status, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return McpJson.Serialize(new { count = plans.Count, data = plans });
    }
}
