using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Services;
using StockReview.Mcp;

namespace StockReview.Mcp.Tools;

[McpServerToolType]
internal class SignalTools
{
    private readonly SignalEventService _signal;

    public SignalTools(SignalEventService signal) => _signal = signal;

    [McpServerTool(Name = "query_signal_events")]
    [Description("查询信号事件及评估结果。指定日期返回当日全量，不指定返回今日；可按股票代码过滤。含 evaluation（结果/原因/最大涨幅/奖励分等）。")]
    public string QuerySignalEvents(
        [Description("日期，格式 yyyy-MM-dd，留空取今日")] string? date = null,
        [Description("股票代码过滤")] string? stockCode = null,
        [Description("返回条数，默认 50，最大 200")] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        List<SignalEvent> events;
        string actualDate;
        if (!string.IsNullOrWhiteSpace(date))
        {
            events = _signal.GetEventsByDate(date);
            if (!string.IsNullOrWhiteSpace(stockCode))
                events = events.Where(e => e.StockCode == stockCode).ToList();
            actualDate = date;
        }
        else
        {
            events = _signal.GetTodayEvents(stockCode);
            actualDate = DateTime.Now.ToString("yyyy-MM-dd");
        }

        var page = events
            .OrderByDescending(e => e.TimeStr)
            .Take(limit)
            .ToList();
        return McpJson.Serialize(new { date = actualDate, total = events.Count, limit, data = page });
    }

    [McpServerTool(Name = "get_signal_stats")]
    [Description("获取信号统计。kind=recent：各信号类型的触发/成功/失败计数与均值（默认）；kind=quality：各股票的高/中/低质量信号计数；kind=factor：因子奖励与判别力统计。")]
    public string GetSignalStats(
        [Description("统计天数，默认 5，最大 60")] int days = 5,
        [Description("统计维度：recent | quality | factor，默认 recent")] string kind = "recent")
    {
        days = Math.Clamp(days, 1, 60);
        object data = kind switch
        {
            "quality" => _signal.GetQualityStatsByStock(days),
            "factor" => _signal.GetFactorRewardStats(days),
            "recent" => _signal.GetRecentStats(days),
            _ => throw new McpException($"未知的 kind: {kind}，可选值 recent | quality | factor")
        };
        return McpJson.Serialize(new { kind, days, data });
    }

    [McpServerTool(Name = "get_signal_suggestions")]
    [Description("获取信号参数优化建议列表（基于近期统计自动生成的调参方向与理由）")]
    public string GetSignalSuggestions(
        [Description("统计天数，默认 5，最大 60")] int days = 5)
    {
        days = Math.Clamp(days, 1, 60);
        var suggestions = _signal.GetOptimizationSuggestions(days);
        return McpJson.Serialize(new { days, data = suggestions });
    }
}
