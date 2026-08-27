using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 强势股仓储服务 - 对应 Electron 版 strongStockStore.js
/// 使用 Dapper + SQLite，增量更新避免全量重载
/// </summary>
public class StrongStockRepositoryService
{
    private readonly DatabaseService _db;
    private List<Dictionary<string, object?>> _strongStocks = new();
    private bool _loaded;

    public bool Loading { get; private set; }
    public string? Error { get; private set; }

    public StrongStockRepositoryService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// 获取所有强股记录（带缓存）
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> FetchStrongStocksAsync(bool force = false, CancellationToken ct = default)
    {
        if (_loaded && !force) return _strongStocks;
        Loading = true;
        Error = null;
        try
        {
            _strongStocks = await Task.Run(() => _db.GetAll("strongStocks"), ct);
            _loaded = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[StrongStockRepository] 获取强股记录失败");
        }
        finally
        {
            Loading = false;
        }
        return _strongStocks;
    }

    public async Task<List<Dictionary<string, object?>>> GetStrongStocksByDateAsync(string date, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM strongStocks WHERE date = @date ORDER BY createdAt DESC", new { date });
            return rows.Select(r => ToDict((IDictionary<string, object>)r)).ToList();
        }, ct);
    }

    public async Task<List<Dictionary<string, object?>>> GetStrongStocksByMonthAsync(string yearMonth, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM strongStocks WHERE date LIKE @pattern ORDER BY createdAt DESC",
                new { pattern = $"{yearMonth}%" });
            return rows.Select(r => ToDict((IDictionary<string, object>)r)).ToList();
        }, ct);
    }

    public async Task<List<Dictionary<string, object?>>> GetStrongStocksByStockCodeAsync(string stockCode, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM strongStocks WHERE stockCode = @code ORDER BY createdAt DESC", new { code = stockCode });
            return rows.Select(r => ToDict((IDictionary<string, object>)r)).ToList();
        }, ct);
    }

    public async Task<Dictionary<string, object?>?> GetStrongStockByIdAsync(object id, CancellationToken ct = default)
    {
        return await Task.Run(() => _db.GetById("strongStocks", id), ct);
    }

    /// <summary>
    /// 添加（增量更新）
    /// </summary>
    public async Task<object> AddStrongStockAsync(IDictionary<string, object?> data, CancellationToken ct = default)
    {
        try
        {
            var id = await Task.Run(() => _db.Add("strongStocks", data), ct);
            var record = new Dictionary<string, object?>(data) { ["id"] = id };
            _strongStocks.Add(record);
            return id;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[StrongStockRepository] 添加强股记录失败");
            throw;
        }
    }

    /// <summary>
    /// 更新（增量更新）
    /// </summary>
    public async Task<bool> UpdateStrongStockAsync(object id, IDictionary<string, object?> data, CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() => _db.Update("strongStocks", id, data), ct);
            if (result && _loaded)
            {
                var idx = _strongStocks.FindIndex(s => s.TryGetValue("id", out var v) && v?.ToString() == id?.ToString());
                if (idx >= 0)
                {
                    foreach (var kv in data)
                        _strongStocks[idx][kv.Key] = kv.Value;
                    _strongStocks[idx]["updatedAt"] = DateTime.UtcNow.ToString("o");
                }
            }
            return result;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[StrongStockRepository] 更新强股记录失败");
            throw;
        }
    }

    /// <summary>
    /// 删除（增量更新）
    /// </summary>
    public async Task<bool> DeleteStrongStockAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() => _db.Delete("strongStocks", id), ct);
            if (result && _loaded)
            {
                _strongStocks.RemoveAll(s => s.TryGetValue("id", out var v) && v?.ToString() == id?.ToString());
            }
            return result;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[StrongStockRepository] 删除强股记录失败");
            throw;
        }
    }

    // ============ 计算属性 ============

    /// <summary>
    /// 按类型分组
    /// </summary>
    public Dictionary<string, List<Dictionary<string, object?>>> StocksByType
    {
        get
        {
            var grouped = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var s in _strongStocks)
            {
                var type = s.TryGetValue("strongType", out var v) ? v?.ToString() ?? "" : "";
                if (!grouped.ContainsKey(type)) grouped[type] = new();
                grouped[type].Add(s);
            }
            return grouped;
        }
    }

    public List<string> AllYearMonths =>
        _strongStocks
            .Where(s => s.TryGetValue("date", out var d) && d != null)
            .Select(s => s["date"]!.ToString()!.Substring(0, 7))
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();

    public int TotalCount => _strongStocks.Count;

    public int LimitUpCount =>
        _strongStocks.Count(s => s.TryGetValue("strongType", out var v) && v?.ToString() == "涨停");

    private static Dictionary<string, object?> ToDict(IDictionary<string, object> row)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kv in row) dict[kv.Key] = kv.Value;
        return dict;
    }
}
