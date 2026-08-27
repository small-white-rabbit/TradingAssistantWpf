// DatabaseService 写入路径回归测试。
// 背景：旧实现 Add/BulkPut/BulkAdd 用 VALUES(?) 位置占位 + Dictionary/数组参数，
// Dapper 对字典按 @key 命名绑定、不认 ?，导致插入从未真正落库（ImportAll 中同类
// 写法已被作者确认"从未真正插入过数据"，但当时未同步修复 Add/BulkPut/BulkAdd）。
// 修复后统一命名参数绑定；本测试对真实 SQLite 做落库验证，防止回退。
using System;
using System.Collections.Generic;
using System.IO;
using StockReview.Core.Data;
using Xunit;

namespace StockReview.Tests.Data;

public class DatabaseServiceWriteTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dir;

    public DatabaseServiceWriteTests()
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

    private static Dictionary<string, object?> Trade(string code, string name) => new()
    {
        ["stockCode"] = code,
        ["stockName"] = name,
        ["tradeDate"] = "2026-08-27",
        ["closePrice"] = 10.5,
        ["remark"] = "unit-test",
    };

    [Fact]
    public void Add_InsertsRow_AndReturnsRowId()
    {
        var id = _db.Add("trades", Trade("600000", "浦发银行"));

        Assert.True((long)id > 0, "Add 应回自增 rowid");
        var row = _db.GetById("trades", id);
        Assert.NotNull(row);
        Assert.Equal("600000", row!["stockCode"]);
        Assert.Equal("浦发银行", row["stockName"]);
        Assert.Equal("unit-test", row["remark"]);
    }

    [Fact]
    public void BulkAdd_InsertsAllRows()
    {
        _db.BulkAdd("trades", new[]
        {
            Trade("600000", "a"), Trade("000001", "b"), Trade("300750", "c")
        });

        Assert.Equal(3L, _db.Count("trades"));
    }

    [Fact]
    public void BulkPut_Upserts_ExistingAndNewRows()
    {
        // 第一批：插入 1 行
        _db.BulkPut("trades", new[] { Trade("600000", "a") });
        Assert.Equal(1L, _db.Count("trades"));

        // 第二批：同 id 更新 + 新增，验证 upsert 不炸、不重复
        var first = _db.GetAll("trades")[0];
        var id = first["id"]!;
        var updated = Trade("600000", "a2");
        updated["id"] = id;
        var fresh = Trade("600519", "新行");

        _db.BulkPut("trades", new[] { updated, fresh });

        Assert.Equal(2L, _db.Count("trades"));
        Assert.Equal("a2", _db.GetById("trades", id)!["stockName"]);
    }

    [Fact]
    public void WhereAnyOf_MatchesByValueList()
    {
        _db.BulkAdd("trades", new[] { Trade("600000", "a"), Trade("000001", "b") });

        var rows = _db.WhereAnyOf("trades", "stockCode", new object[] { "600000", "300750" });

        Assert.Single(rows);
        Assert.Equal("600000", rows[0]["stockCode"]);
    }

    [Fact]
    public void Update_ChangesFields()
    {
        var id = _db.Add("trades", Trade("600000", "a"));

        var ok = _db.Update("trades", id, new Dictionary<string, object?> { ["stockName"] = "改名" });

        Assert.True(ok);
        Assert.Equal("改名", _db.GetById("trades", id)!["stockName"]);
    }

    [Fact]
    public void Add_RejectsInvalidIdentifierKey()
    {
        var bad = Trade("600000", "x");
        bad["bad\"col"] = 1; // 含引号的键 → SQL 注入面，应被 AssertIdentifier 拦截

        Assert.Throws<ArgumentException>(() => _db.Add("trades", bad));
    }
}
