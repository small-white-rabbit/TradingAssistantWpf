// GetStatisticsSummary 回归测试。
// 背景：2026-09-02 优化审查发现问题标签统计 SQL 存在双 WHERE——
// whereClause 已带 "WHERE tradeDate LIKE @ym"，模板中又硬编码一个 WHERE，
// 导致按年/月筛选时 SQL 语法错误（无筛选时恰好不触发）。修复后本测试锁住行为。
using System;
using System.Collections.Generic;
using System.IO;
using StockReview.Core.Data;
using Xunit;

namespace StockReview.Tests.Data;

public class StatisticsSummaryTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dir;

    public StatisticsSummaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sreview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService();
        _db.SetDataDir(_dir);
        _db.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响测试 */ }
    }

    private static Dictionary<string, object?> Trade(string tradeDate, string problemTags) => new()
    {
        ["tradeDate"] = tradeDate,
        ["stockCode"] = "600000",
        ["stockName"] = "浦发银行",
        ["positionStatus"] = "已清仓",
        ["totalReturn"] = 3.5,
        ["entryType"] = "打板",
        ["problemTags"] = problemTags,
    };

    [Fact]
    public void GetStatisticsSummary_WithYearMonthFilter_DoesNotThrow()
    {
        _db.Add("trades", Trade("2026-08-27", "[\"追高\",\"不止损\"]"));
        _db.Add("trades", Trade("2026-07-15", "[]"));

        // 修复前：yearMonth 非空时 problemRows 的 SQL 为
        // "WHERE tradeDate LIKE @ym WHERE problemTags ..." → SqliteException
        var ex = Record.Exception(() => _db.GetStatisticsSummary("2026-08"));

        Assert.Null(ex);
    }

    [Fact]
    public void GetStatisticsSummary_WithYearFilter_DoesNotThrow()
    {
        _db.Add("trades", Trade("2026-08-27", "[\"追高\"]"));

        var ex = Record.Exception(() => _db.GetStatisticsSummary(year: "2026"));

        Assert.Null(ex);
    }

    [Fact]
    public void GetStatisticsSummary_WithoutFilter_ReturnsProblemCounts()
    {
        _db.Add("trades", Trade("2026-08-27", "[\"追高\",\"不止损\"]"));
        _db.Add("trades", Trade("2026-08-28", "[\"追高\"]"));

        var summary = _db.GetStatisticsSummary();

        Assert.NotNull(summary);
        // 修复后的 tagFilter 在无筛选时仍应带 WHERE（否则返回全表含空标签行）
        // 用 JsonDocument 语义化断言（默认序列化器会把中文转义为 \uXXXX，不能直接 Contains 原文）
        var json = System.Text.Json.JsonSerializer.Serialize(summary);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var stats = doc.RootElement.GetProperty("problemStats");
        var zg = stats.EnumerateArray().First(e => e.GetProperty("problem").GetString() == "追高");
        Assert.Equal(2, zg.GetProperty("count").GetInt32());
        Assert.Single(stats.EnumerateArray(), e => e.GetProperty("problem").GetString() == "不止损");
    }
}
