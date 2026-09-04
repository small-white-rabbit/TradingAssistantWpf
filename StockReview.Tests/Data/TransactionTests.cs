// P1 环境事务回归测试（2026-09-04，archify 模式对照：跨表多步写的原子性）。
// 覆盖：RunInTransaction 提交路径 / 异常整体回滚 / 嵌套加入外层 / Put 并发安全由本地事务兜底。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockReview.Core.Data;
using Xunit;

namespace StockReview.Tests.Data;

public class TransactionTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dir;

    public TransactionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sreview-tx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService();
        _db.SetDataDir(_dir);
        _db.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响测试 */ }
    }

    private Dictionary<string, object?> Trade(string code) => new()
    {
        ["tradeDate"] = "2026-09-04", ["stockCode"] = code, ["stockName"] = "测试股"
    };

    [Fact]
    public void RunInTransaction_CommitsWhenBodySucceeds()
    {
        var id = _db.RunInTransaction(() =>
        {
            var tradeId = _db.Add("trades", Trade("600000"));
            _db.Add("strongStocks", new Dictionary<string, object?>
            { ["date"] = "2026-09-04", ["stockCode"] = "600000", ["maxChangePct"] = 6.0 });
            return tradeId;
        });

        Assert.NotNull(id);
        Assert.Single(_db.WhereEquals("trades", "stockCode", "600000"));
        Assert.Single(_db.WhereEquals("strongStocks", "stockCode", "600000"));
    }

    [Fact]
    public void RunInTransaction_RollsBackAllStepsWhenBodyThrows()
    {
        // 库里预置一条，证明回滚不会误伤事务外的已有数据
        _db.Add("trades", Trade("000001"));

        Assert.Throws<InvalidOperationException>(() => _db.RunInTransaction(() =>
        {
            _db.Add("trades", Trade("600001"));
            _db.Add("strongStocks", new Dictionary<string, object?>
            { ["date"] = "2026-09-04", ["stockCode"] = "600001" });
            throw new InvalidOperationException("模拟中途失败");
#pragma warning disable CS0162 // 模拟分支，保证 lambda 返回类型
            return 0;
#pragma warning restore CS0162
        }));

        // 事务内两步写入都必须回滚
        Assert.Empty(_db.WhereEquals("trades", "stockCode", "600001"));
        Assert.Empty(_db.WhereEquals("strongStocks", "stockCode", "600001"));
        // 事务外的已有数据不受影响
        Assert.Single(_db.WhereEquals("trades", "stockCode", "000001"));
    }

    [Fact]
    public void RunInTransaction_RollbackRestoresDelete()
    {
        var id = _db.Add("trades", Trade("600002"));

        Assert.Throws<InvalidOperationException>(() => _db.RunInTransaction(() =>
        {
            _db.Delete("trades", id);
            throw new InvalidOperationException("删完就失败");
        }));

        Assert.Single(_db.WhereEquals("trades", "stockCode", "600002"));
    }

    [Fact]
    public void RunInTransaction_NestedJoinsOuterTransaction()
    {
        Assert.Throws<InvalidOperationException>(() => _db.RunInTransaction(() =>
        {
            _db.RunInTransaction(() => _db.Add("trades", Trade("600003")));
            throw new InvalidOperationException("嵌套提交后外层失败");
        }));

        // 嵌套事务不得提前提交：外层失败时内层写入一并回滚
        Assert.Empty(_db.WhereEquals("trades", "stockCode", "600003"));
    }

    [Fact]
    public void RunInTransaction_ReadSeesUncommittedWrites()
    {
        var found = _db.RunInTransaction(() =>
        {
            _db.Add("trades", Trade("600004"));
            // 同一事务连接内应读到未提交写入（读一致快照）
            return _db.WhereCompoundFirst("trades",
                new Dictionary<string, object> { ["stockCode"] = "600004" });
        });

        Assert.NotNull(found);
        Assert.Equal("600004", found!["stockCode"]?.ToString());
    }

    [Fact]
    public void Put_UpdateThenInsertRemainsAtomicUnderConcurrency()
    {
        // 并发对同一 id 各 Put 一次：本地事务 + busy_timeout 下，
        // 最终必须恰好一行（旧行为：两连接 UPDATE 均命中 0 行 → 双 INSERT）。
        var first = (long)_db.Put("trades", Trade("600005"));
        var data1 = Trade("600005"); data1["id"] = first; data1["stockName"] = "并发甲";
        var data2 = Trade("600005"); data2["id"] = first; data2["stockName"] = "并发乙";

        var results = System.Threading.Tasks.Parallel.For(0, 2, i =>
        {
            if (i == 0) _db.Put("trades", data1);
            else _db.Put("trades", data2);
        });
        Assert.True(results.IsCompleted);

        var rows = _db.WhereEquals("trades", "stockCode", "600005");
        Assert.Single(rows);
        Assert.Equal(first, Convert.ToInt64(rows[0]["id"]));
    }
}
