using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 交易记录仓储服务
/// 使用 Dapper + SQLite，增量更新避免全量重载
/// </summary>
public class TradeRepositoryService
{
    private readonly DatabaseService _db;
    private List<Dictionary<string, object?>> _trades = new();
    private bool _loaded;

    public bool Loading { get; private set; }
    public string? Error { get; private set; }

    public TradeRepositoryService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// 获取所有交易记录（带缓存）
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> FetchTradesAsync(bool force = false, CancellationToken ct = default)
    {
        if (_loaded && !force) return _trades;
        Loading = true;
        Error = null;
        try
        {
            _trades = await Task.Run(() => _db.GetAll("trades"), ct).ConfigureAwait(false);
            _loaded = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[TradeRepository] 获取交易记录失败");
        }
        finally
        {
            Loading = false;
        }
        return _trades;
    }

    /// <summary>
    /// 按日期获取交易记录
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> GetTradesByDateAsync(string date, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM trades WHERE tradeDate = @date ORDER BY createdAt DESC", new { date });
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按月份获取交易记录
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> GetTradesByMonthAsync(string yearMonth, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM trades WHERE tradeDate LIKE @pattern ORDER BY createdAt DESC",
                new { pattern = $"{yearMonth}%" });
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按股票代码获取交易记录
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> GetTradesByStockCodeAsync(string stockCode, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM trades WHERE stockCode = @code ORDER BY createdAt DESC", new { code = stockCode });
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取已清仓交易
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> GetClearedTradesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM trades WHERE positionStatus = '已清仓' ORDER BY createdAt DESC");
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取持仓中交易
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> GetActiveTradesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var conn = _db.CreateConnection();
            var rows = conn.Query("SELECT * FROM trades WHERE positionStatus = '持仓中' ORDER BY createdAt DESC");
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 添加交易记录（增量更新）
    /// </summary>
    public async Task<object> AddTradeAsync(IDictionary<string, object?> data, CancellationToken ct = default)
    {
        try
        {
            var id = await Task.Run(() => _db.Add("trades", data), ct).ConfigureAwait(false);
            // 增量追加到缓存
            var record = new Dictionary<string, object?>(data) { ["id"] = id };
            _trades.Add(record);
            return id;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[TradeRepository] 添加交易记录失败");
            throw;
        }
    }

    /// <summary>
    /// 更新交易记录（增量更新）
    /// </summary>
    public async Task<bool> UpdateTradeAsync(object id, IDictionary<string, object?> data, CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() => _db.Update("trades", id, data), ct).ConfigureAwait(false);
            if (result && _loaded)
            {
                // 增量更新缓存
                var idx = _trades.FindIndex(t => t.TryGetValue("id", out var v) && v?.ToString() == id?.ToString());
                if (idx >= 0)
                {
                    foreach (var kv in data)
                        _trades[idx][kv.Key] = kv.Value;
                    _trades[idx]["updatedAt"] = DateTime.UtcNow.ToString("o");
                }
            }
            return result;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[TradeRepository] 更新交易记录失败");
            throw;
        }
    }

    /// <summary>
    /// 删除交易记录（增量更新）
    /// </summary>
    public async Task<bool> DeleteTradeAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() => _db.Delete("trades", id), ct).ConfigureAwait(false);
            if (result && _loaded)
            {
                _trades.RemoveAll(t => t.TryGetValue("id", out var v) && v?.ToString() == id?.ToString());
            }
            return result;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[TradeRepository] 删除交易记录失败");
            throw;
        }
    }

    // ============ 计算属性 ============

    public List<Dictionary<string, object?>> ClearedTrades =>
        _trades.Where(t => t.TryGetValue("positionStatus", out var v) && v?.ToString() == "已清仓").ToList();

    public List<Dictionary<string, object?>> ActiveTrades =>
        _trades.Where(t => t.TryGetValue("positionStatus", out var v) && v?.ToString() == "持仓中").ToList();

    public int TotalCount => _trades.Count;
    public int ClearedCount => ClearedTrades.Count;
    public int ActiveCount => ActiveTrades.Count;

    /// <summary>
    /// 所有年月（降序）
    /// </summary>
    public List<string> AllYearMonths =>
        _trades
            .Where(t => t.TryGetValue("tradeDate", out var d) && d != null)
            .Select(t => t["tradeDate"]!.ToString()!.Substring(0, 7))
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();

    /// <summary>
    /// 所有年份（降序）
    /// </summary>
    public List<string> AllYears =>
        _trades
            .Where(t => t.TryGetValue("tradeDate", out var d) && d != null)
            .Select(t => t["tradeDate"]!.ToString()!.Substring(0, 4))
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();

    // ============ 内部辅助 ============

    private static Dictionary<string, object?> DeserializeRecord(IDictionary<string, object> row)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kv in row)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }
}
