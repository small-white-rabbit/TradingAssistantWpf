using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Data;
using StockReview.Mcp;

namespace StockReview.Mcp.Tools;

[McpServerToolType]
internal class StatisticsTools
{
    private static readonly string[] Tables =
    {
        "trades", "entryTypes", "strongStocks", "problemTags",
        "monthlySummaries", "dailySummaries", "todoTemplates", "dailyPicks",
        "appConfig", "patternCases", "insights"
    };

    private readonly IDatabaseService _db;

    public StatisticsTools(IDatabaseService db) => _db = db;

    [McpServerTool(Name = "get_statistics_summary")]
    [Description("获取交易统计总览：交易总数、胜率、平均收益等概览，及各入场类型与问题标签的统计分布。可按月份(YYYY-MM)或年份过滤。")]
    public string GetStatisticsSummary(
        [Description("月份过滤，格式 YYYY-MM，与 year 互斥优先")] string? yearMonth = null,
        [Description("年份过滤，如 2026")] string? year = null)
    {
        var summary = _db.GetStatisticsSummary(yearMonth, year);
        return McpJson.Serialize(new { yearMonth, year, summary });
    }

    [McpServerTool(Name = "get_monthly_win_rate_stats")]
    [Description("获取近 N 个月的月度胜率统计趋势（默认 6 个月）")]
    public string GetMonthlyWinRateStats(
        [Description("统计月数，默认 6，最大 24")] int months = 6)
    {
        months = Math.Clamp(months, 1, 24);
        var stats = _db.GetMonthlyWinRateStats(months);
        return McpJson.Serialize(new { months, data = stats });
    }

    [McpServerTool(Name = "get_type_win_rate_stats")]
    [Description("获取各入场类型的胜率统计与收益分布")]
    public string GetTypeWinRateStats()
    {
        var typeWinRate = _db.GetTypeWinRateStats();
        var tradeDistribution = _db.GetTradeDistribution();
        return McpJson.Serialize(new { typeWinRate, tradeDistribution });
    }

    [McpServerTool(Name = "list_data_tables")]
    [Description("列出数据库全部表及行数概览，用于探索数据规模。配合 query_trades / search_pattern_cases / get_insights 等工具使用。")]
    public string ListDataTables()
    {
        var tables = Tables
            .Select(t => new { table = t, rows = _db.Count(t) })
            .ToList();
        return McpJson.Serialize(new { tables });
    }
}
