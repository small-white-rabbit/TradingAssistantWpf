using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Data;
using StockReview.Mcp;

namespace StockReview.Mcp.Tools;

[McpServerToolType]
internal class TradeTools
{
    private const int TextLimit = 400;

    private static readonly string[] ListFields =
    {
        "id", "tradeDate", "stockCode", "stockName", "entryType", "parentEntryType",
        "positionStatus", "caseType", "changePct", "maxChangePct", "todayPerformance",
        "meetExpectation", "exitPrice", "exitDate", "totalReturn", "problemTags", "followUp"
    };

    private readonly IDatabaseService _db;

    public TradeTools(IDatabaseService db) => _db = db;

    [McpServerTool(Name = "query_trades")]
    [Description("分页查询交易记录，支持按月份(YYYY-MM)、股票代码、年份前缀过滤，按交易日倒序。列表返回精简字段，完整字段用 get_trade_detail。")]
    public string QueryTrades(
        [Description("月份筛选，格式 YYYY-MM")] string? yearMonth = null,
        [Description("股票代码精确匹配，如 00700")] string? stockCode = null,
        [Description("年份或年月前缀，如 2026")] string? year = null,
        [Description("返回条数，默认 20，最大 100")] int limit = 20,
        [Description("分页偏移量")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        List<Dictionary<string, object?>> source;
        if (!string.IsNullOrWhiteSpace(yearMonth))
            source = _db.WhereStartsWith("trades", "tradeDate", yearMonth);
        else if (!string.IsNullOrWhiteSpace(stockCode))
            source = _db.WhereEquals("trades", "stockCode", stockCode);
        else if (!string.IsNullOrWhiteSpace(year))
            source = _db.GetTradesByYearPrefix(year);
        else
            source = _db.GetAll("trades");

        var rows = source
            .OrderByDescending(r => r.TryGetValue("tradeDate", out var d) ? d?.ToString() : null)
            .ToList();
        var page = rows.Skip(offset).Take(limit)
            .Select(r => McpJson.Project(r, ListFields, TextLimit))
            .ToList();
        return McpJson.Serialize(new { total = rows.Count, limit, offset, data = page });
    }

    [McpServerTool(Name = "get_trade_detail")]
    [Description("按 id 获取单笔交易完整详情（含备注、反思、跟进等全部字段，长文本截断）")]
    public string GetTradeDetail(
        [Description("交易记录 id，可通过 query_trades 获取")] long id)
    {
        var row = _db.GetById("trades", id);
        if (row == null)
            throw new McpException($"未找到 id={id} 的交易记录");
        return McpJson.Serialize(McpJson.Project(row, null, 500));
    }
}
