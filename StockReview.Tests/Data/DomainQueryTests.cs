// P5 领域查询下沉的回归测试（2026-09-02）。
// 覆盖从 ViewModel 内联 SQL 下沉到 Core 的 5 个领域方法 + appConfig upsert 等价性。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockReview.Core.Data;
using Xunit;

namespace StockReview.Tests.Data;

public class DomainQueryTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dir;

    public DomainQueryTests()
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

    [Fact]
    public void GetDailySummariesInRange_FiltersByRangeAndType()
    {
        _db.Add("dailySummaries", new Dictionary<string, object?>
        { ["summaryType"] = "日结", ["recordDate"] = "2026-08-27", ["title"] = "a", ["summary"] = "s", ["content"] = "c" });
        _db.Add("dailySummaries", new Dictionary<string, object?>
        { ["summaryType"] = "周结", ["recordDate"] = "2026-08-27", ["title"] = "b", ["summary"] = "s", ["content"] = "c" });
        _db.Add("dailySummaries", new Dictionary<string, object?>
        { ["summaryType"] = "日结", ["recordDate"] = "2026-09-01", ["title"] = "c", ["summary"] = "s", ["content"] = "c" });

        var rows = _db.GetDailySummariesInRange("2026-08-01", "2026-08-31", "日结");

        Assert.Single(rows);
        Assert.Equal("a", rows[0]["title"]?.ToString());
    }

    [Fact]
    public void GetActiveEntryTypes_FiltersActiveAndOrdersBySortOrder()
    {
        // 注意：Initialize() 会向空库预置 6 条默认进场类型（全部 isActive=1），
        // 因此这里断言过滤/排序语义而非固定总数。
        _db.Add("entryTypes", new Dictionary<string, object?> { ["typeName"] = "测试类型甲", ["sortOrder"] = 2, ["isActive"] = 1 });
        _db.Add("entryTypes", new Dictionary<string, object?> { ["typeName"] = "测试类型乙", ["sortOrder"] = 1, ["isActive"] = 1 });
        _db.Add("entryTypes", new Dictionary<string, object?> { ["typeName"] = "停用项", ["sortOrder"] = 0, ["isActive"] = 0 });

        var rows = _db.GetActiveEntryTypes();

        Assert.Contains(rows, r => r["typeName"]?.ToString() == "测试类型甲");
        Assert.Contains(rows, r => r["typeName"]?.ToString() == "测试类型乙");
        Assert.DoesNotContain(rows, r => r["typeName"]?.ToString() == "停用项");
        Assert.All(rows, r => Assert.Equal(1L, Convert.ToInt64(r["isActive"])));
        // 按 sortOrder 升序：乙(1) 必须排在 甲(2) 之前
        var idxA = rows.FindIndex(r => r["typeName"]?.ToString() == "测试类型甲");
        var idxB = rows.FindIndex(r => r["typeName"]?.ToString() == "测试类型乙");
        Assert.True(idxB < idxA, $"sortOrder 升序失败: 乙@{idxB} 应在 甲@{idxA} 之前");
    }

    [Fact]
    public void GetActiveProblemTags_FiltersActive()
    {
        // 注意：Initialize() 会向空库预置 6 条默认问题标签（全部 isActive=1）。
        _db.Add("problemTags", new Dictionary<string, object?> { ["tagName"] = "测试标签追高", ["sortOrder"] = 1, ["isActive"] = 1 });
        _db.Add("problemTags", new Dictionary<string, object?> { ["tagName"] = "停用", ["sortOrder"] = 2, ["isActive"] = 0 });

        var rows = _db.GetActiveProblemTags();

        Assert.Contains(rows, r => r["tagName"]?.ToString() == "测试标签追高");
        Assert.DoesNotContain(rows, r => r["tagName"]?.ToString() == "停用");
        Assert.All(rows, r => Assert.Equal(1L, Convert.ToInt64(r["isActive"])));
    }

    [Fact]
    public void GetTradesByYearPrefix_FiltersByYear()
    {
        _db.Add("trades", new Dictionary<string, object?> { ["tradeDate"] = "2026-08-27", ["stockCode"] = "600000" });
        _db.Add("trades", new Dictionary<string, object?> { ["tradeDate"] = "2025-12-30", ["stockCode"] = "600001" });

        var rows = _db.GetTradesByYearPrefix("2026-");

        Assert.Single(rows);
        Assert.Equal("600000", rows[0]["stockCode"]?.ToString());
    }

    [Fact]
    public void GetStrongStocksByYearPrefix_FiltersByYear()
    {
        _db.Add("strongStocks", new Dictionary<string, object?> { ["date"] = "2026-08-27", ["stockCode"] = "600000" });
        _db.Add("strongStocks", new Dictionary<string, object?> { ["date"] = "2025-12-30", ["stockCode"] = "600001" });

        var rows = _db.GetStrongStocksByYearPrefix("2026-");

        Assert.Single(rows);
        Assert.Equal("600000", rows[0]["stockCode"]?.ToString());
    }

    [Fact]
    public void PutAppConfig_UpsertsByKey()
    {
        // YearMonthViewModel.SaveConfig 的等价路径：Put appConfig = INSERT OR REPLACE
        _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = "yearMonthDisplayMode", ["value"] = "show" });
        _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = "yearMonthDisplayMode", ["value"] = "hidden" });

        var row = _db.GetById("appConfig", "yearMonthDisplayMode");
        Assert.NotNull(row);
        Assert.Equal("hidden", row!["value"]?.ToString());
    }
}
