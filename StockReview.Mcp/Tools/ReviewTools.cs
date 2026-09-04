using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Data;
using StockReview.Mcp;

namespace StockReview.Mcp.Tools;

[McpServerToolType]
internal class ReviewTools
{
    private const int TextLimit = 400;

    private static readonly string[] CaseListFields =
    {
        "id", "entryType", "caseType", "stockCode", "stockName",
        "tradeDate", "totalReturn", "reflection", "createdAt"
    };

    private static readonly string[] StrongStockListFields =
    {
        "id", "date", "stockCode", "stockName", "highPrice",
        "maxChangePct", "strongType", "relatedTradeIds"
    };

    private static readonly string[] InsightListFields =
    {
        "id", "recordDate", "title", "content", "importance", "isPinned",
        "stockCode", "stockName", "tags", "relatedCaseId", "relatedCaseType"
    };

    private readonly IDatabaseService _db;

    public ReviewTools(IDatabaseService db) => _db = db;

    [McpServerTool(Name = "search_pattern_cases")]
    [Description("搜索形态案例库。caseType 可选 success(成功案例) | fail(失败案例) | calibration(卖出校准) | all(默认)；可按入场类型、关键词（股票代码/名称/备注/反思）过滤；可按涨跌幅或日期排序。")]
    public string SearchPatternCases(
        [Description("案例类型：success | fail | calibration | all，默认 all")] string caseType = "all",
        [Description("入场类型精确过滤，如 首板")] string? entryType = null,
        [Description("关键词，匹配股票代码/名称/备注/反思")] string? keyword = null,
        [Description("排序：date_desc(默认) | date_asc | change_desc | change_asc")] string sortBy = "date_desc",
        [Description("页码，从 1 开始，默认 1")] int page = 1,
        [Description("每页条数，默认 20，最大 50")] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var (data, total) = _db.QueryCasesPaginated(
            caseType, entryType ?? "", null, keyword ?? "", sortBy, page, pageSize);
        var rows = data
            .Select(r => McpJson.Project(r, CaseListFields, TextLimit))
            .ToList();
        return McpJson.Serialize(new { total, page, pageSize, data = rows });
    }

    [McpServerTool(Name = "get_daily_summaries")]
    [Description("获取区间内的每日复盘日报（默认近 30 天）。summaryType 可选 daily(默认) | weekly | monthly。")]
    public string GetDailySummaries(
        [Description("起始日期 yyyy-MM-dd，默认 30 天前")] string? startDate = null,
        [Description("结束日期 yyyy-MM-dd，默认今天")] string? endDate = null,
        [Description("摘要类型：daily | weekly | monthly，默认 daily")] string summaryType = "daily")
    {
        var start = string.IsNullOrWhiteSpace(startDate)
            ? DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd")
            : startDate;
        var end = string.IsNullOrWhiteSpace(endDate)
            ? DateTime.Today.ToString("yyyy-MM-dd")
            : endDate;
        var rows = _db.GetDailySummariesInRange(start, end, summaryType)
            .OrderByDescending(r => r.TryGetValue("recordDate", out var d) ? d?.ToString() : null)
            .Select(r => McpJson.Project(r, null, 600))
            .ToList();
        return McpJson.Serialize(new { startDate = start, endDate = end, summaryType, count = rows.Count, data = rows });
    }

    [McpServerTool(Name = "get_monthly_summaries")]
    [Description("分页获取月度复盘总结，按月份倒序。含月度总结正文与标题。")]
    public string GetMonthlySummaries(
        [Description("返回条数，默认 12，最大 60")] int limit = 12,
        [Description("分页偏移量")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 60);
        offset = Math.Max(offset, 0);
        var rows = _db.GetPage("monthlySummaries", limit, offset, "yearMonth", "DESC")
            .Select(r => McpJson.Project(r, null, 600))
            .ToList();
        return McpJson.Serialize(new { total = _db.Count("monthlySummaries"), limit, offset, data = rows });
    }

    [McpServerTool(Name = "query_strong_stocks")]
    [Description("分页查询强势股记录。支持按日期(yyyy-MM-dd)、月份(YYYY-MM)或股票代码过滤，按日期倒序。含高开价、最大涨幅、强势类型。")]
    public string QueryStrongStocks(
        [Description("日期精确过滤，格式 yyyy-MM-dd")] string? date = null,
        [Description("月份前缀过滤，格式 YYYY-MM")] string? yearMonth = null,
        [Description("股票代码精确匹配")] string? stockCode = null,
        [Description("返回条数，默认 20，最大 100")] int limit = 20,
        [Description("分页偏移量")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        List<Dictionary<string, object?>> source;
        if (!string.IsNullOrWhiteSpace(date))
            source = _db.WhereEquals("strongStocks", "date", date);
        else if (!string.IsNullOrWhiteSpace(yearMonth))
            source = _db.WhereStartsWith("strongStocks", "date", yearMonth);
        else if (!string.IsNullOrWhiteSpace(stockCode))
            source = _db.WhereEquals("strongStocks", "stockCode", stockCode);
        else
            source = _db.GetAll("strongStocks");

        var rows = source
            .OrderByDescending(r => r.TryGetValue("date", out var d) ? d?.ToString() : null)
            .ToList();
        var page = rows.Skip(offset).Take(limit)
            .Select(r => McpJson.Project(r, StrongStockListFields, 0))
            .ToList();
        return McpJson.Serialize(new { total = rows.Count, limit, offset, data = page });
    }

    [McpServerTool(Name = "get_insights")]
    [Description("分页获取复盘心得洞见，置顶优先、按记录日期倒序。可只看置顶。含标题、正文（截断）、重要度、关联案例。")]
    public string GetInsights(
        [Description("只返回置顶心得，默认 false")] bool pinnedOnly = false,
        [Description("返回条数，默认 20，最大 100")] int limit = 20,
        [Description("分页偏移量")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var source = pinnedOnly
            ? _db.WhereEquals("insights", "isPinned", 1)
            : _db.GetAll("insights");
        var rows = source
            .OrderByDescending(r => IsTruthy(r, "isPinned"))
            .ThenByDescending(r => r.TryGetValue("recordDate", out var d) ? d?.ToString() : null)
            .ToList();
        var page = rows.Skip(offset).Take(limit)
            .Select(r => McpJson.Project(r, InsightListFields, 300))
            .ToList();
        return McpJson.Serialize(new { total = rows.Count, limit, offset, data = page });
    }

    private static bool IsTruthy(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v == null) return false;
        if (v is bool b) return b;
        return v.ToString() == "1";
    }
}
