using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Dapper;
using Serilog;
using StockReview.Core.Data;

namespace StockReviewWpf.WebBridge;

/// <summary>
/// WebView2 host object 桥：把 WPF DatabaseService 暴露给内嵌的前端页面。
///
/// 命名约定：公开方法名使用 camelCase（与前端 db/index.js 的 ipc channel 名完全一致），
/// 避免 WebView2 host object 成员名大小写解析差异导致 JS 调用落空。
/// 所有方法返回 Task&lt;string&gt;（JSON），由 JS 侧 P() 解析；
/// 统一通过 Wrap 包装异常：失败时返回 {"__error":"..."} 且写 Serilog 日志，
/// 保证 JS 侧拿到结构化错误而非悬挂 Promise / 未观察异常。
/// </summary>
public class DbHostObject
{
    /// <summary>表名白名单：table 参数来自 JS（不可信输入），未列入即拒绝（防 SQL 注入）</summary>
    private static readonly HashSet<string> AllowedTables = new(StringComparer.Ordinal)
    {
        "trades", "entryTypes", "strongStocks", "problemTags", "monthlySummaries",
        "dailySummaries", "todoTemplates", "dailyPicks", "appConfig", "patternCases", "insights"
    };

    private static readonly JsonSerializerOptions JsOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IDatabaseService _db;

    public DbHostObject(IDatabaseService db) => _db = db;

    // ============ 通用工具 ============

    /// <summary>统一异常包装：成功返回 f() 的 JSON；失败写日志并返回 {"__error":...}</summary>
    private Task<string> Wrap(string op, Func<string> f)
    {
        try
        {
            return Task.FromResult(f());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DbHostObject] {Op} 失败", op);
            return Task.FromResult(Error(ex.Message));
        }
    }

    /// <summary>表名校验：白名单外的表名直接拒绝（表名会被拼接进 SQL）</summary>
    private static string AssertTable(string table)
    {
        if (table == null || !AllowedTables.Contains(table))
            throw new ArgumentException($"Invalid table: {table ?? "(null)"}");
        return table;
    }

    /// <summary>
    /// SQL 标识符（字段名）清洗：剥离引号与空白后必须形如合法标识符。
    /// 字段名会被拼接进 ORDER BY 子句，绝不能透传原始输入。
    /// </summary>
    private static string SafeIdent(string? field)
    {
        var f = (field ?? string.Empty).Replace("\"", "").Trim();
        if (f.Length == 0 || !f.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"Invalid field: {field}");
        return f;
    }

    /// <summary>ID 解析：能转 long 则转数字（SQLite 整型主键），否则按字符串（appConfig.key）</summary>
    private static object ParseId(string id) =>
        long.TryParse(id, out var l) ? l : id;

    /// <summary>安全 JSON 反序列化：失败返回 null 并写调试日志（数据层容错，不中断页面）</summary>
    private static T? FromJson<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsOpts); }
        catch (Exception ex) { Log.Debug(ex, "[DbHostObject] JSON 反序列化失败"); return default; }
    }

    private static string ToJson<T>(T? value)
    {
        if (value == null) return "null";
        try { return JsonSerializer.Serialize(value, JsOpts); }
        catch (Exception ex) { Log.Debug(ex, "[DbHostObject] JSON 序列化失败"); return "null"; }
    }

    private static string Error(string msg) => $"{{\"__error\":\"{HttpUtility.JavaScriptStringEncode(msg)}\"}}";

    private static object? ConvertJsonElement(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            JsonValueKind.Array => je.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : (object)je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static Dictionary<string, object?>? ToDict(string? json)
    {
        var obj = FromJson<Dictionary<string, JsonElement>>(json);
        if (obj == null) return null;
        return obj.ToDictionary(kv => kv.Key, kv => ConvertJsonElement(kv.Value));
    }

    private static Dictionary<string, object>? ToStringDict(string? json)
    {
        var conds = FromJson<Dictionary<string, string>>(json);
        if (conds == null) return null;
        return conds.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
    }

    // ============ 基础 CRUD（对应原版 db:* channel）============

    public Task<string> getAll(string table) =>
        Wrap(nameof(getAll), () => ToJson(_db.GetAll(AssertTable(table))));

    public Task<string> getById(string table, string id) =>
        Wrap(nameof(getById), () => ToJson(_db.GetById(AssertTable(table), ParseId(id))));

    public Task<string> add(string table, string jsonData) =>
        Wrap(nameof(add), () => ToJson(new { id = _db.Add(AssertTable(table), ToDict(jsonData)!) }));

    public Task<string> update(string table, string id, string jsonData) =>
        Wrap(nameof(update), () => ToJson(new { success = _db.Update(AssertTable(table), ParseId(id), ToDict(jsonData)!) }));

    public Task<string> delete(string table, string id) =>
        Wrap(nameof(delete), () => ToJson(new { success = _db.Delete(AssertTable(table), ParseId(id)) }));

    public Task<string> put(string table, string jsonData) =>
        Wrap(nameof(put), () => ToJson(new { id = _db.Put(AssertTable(table), ToDict(jsonData)!) }));

    public Task<string> bulkPut(string table, string jsonItems) =>
        Wrap(nameof(bulkPut), () =>
        {
            var items = FromJson<List<Dictionary<string, object?>>>(jsonItems)
                ?? throw new ArgumentException("items 反序列化失败");
            _db.BulkPut(AssertTable(table), items);
            return "true";
        });

    public Task<string> bulkAdd(string table, string jsonItems) =>
        Wrap(nameof(bulkAdd), () =>
        {
            var items = FromJson<List<Dictionary<string, object?>>>(jsonItems)
                ?? throw new ArgumentException("items 反序列化失败");
            _db.BulkAdd(AssertTable(table), items);
            return "true";
        });

    public Task<string> clear(string table) =>
        Wrap(nameof(clear), () => { _db.Clear(AssertTable(table)); return "true"; });

    public Task<string> count(string table) =>
        Wrap(nameof(count), () => _db.Count(AssertTable(table)).ToString());

    public Task<string> deleteDatabase() =>
        Wrap(nameof(deleteDatabase), () => { _db.DeleteDatabase(); return "true"; });

    // ============ 查询（where / order / page）============

    public Task<string> whereEquals(string table, string field, string value) =>
        Wrap(nameof(whereEquals), () => ToJson(_db.WhereEquals(AssertTable(table), SafeIdent(field), ParseId(value))));

    public Task<string> whereStartsWith(string table, string field, string value) =>
        Wrap(nameof(whereStartsWith), () => ToJson(_db.WhereStartsWith(AssertTable(table), SafeIdent(field), value)));

    public Task<string> whereAnyOf(string table, string field, string jsonValues) =>
        Wrap(nameof(whereAnyOf), () =>
        {
            var vals = FromJson<List<string>>(jsonValues)
                ?? throw new ArgumentException("values 反序列化失败");
            return ToJson(_db.WhereAnyOf(AssertTable(table), SafeIdent(field), vals.Cast<object>()));
        });

    public Task<string> whereCompound(string table, string jsonConditions) =>
        Wrap(nameof(whereCompound), () => ToJson(_db.WhereCompound(AssertTable(table), ToStringDict(jsonConditions) ?? new())));

    public Task<string> whereBetween(string table, string field, string lower, string upper) =>
        Wrap(nameof(whereBetween), () => ToJson(_db.WhereBetween(AssertTable(table), SafeIdent(field), lower, upper)));

    public Task<string> whereFirst(string table, string field, string value) =>
        Wrap(nameof(whereFirst), () => ToJson(_db.WhereFirst(AssertTable(table), SafeIdent(field), ParseId(value))));

    public Task<string> whereCompoundFirst(string table, string jsonConditions) =>
        Wrap(nameof(whereCompoundFirst), () => ToJson(_db.WhereCompoundFirst(AssertTable(table), ToStringDict(jsonConditions) ?? new())));

    public Task<string> whereBetweenFirst(string table, string field, string lower, string upper) =>
        Wrap(nameof(whereBetweenFirst), () => ToJson(_db.WhereBetween(AssertTable(table), SafeIdent(field), lower, upper).FirstOrDefault()));

    // ============ 排序（原生 SQL，表名/字段名均已白名单+清洗）============

    public Task<string> orderBy(string table, string field) =>
        Wrap(nameof(orderBy), () => QueryRows(AssertTable(table), SafeIdent(field), "ASC", null));

    public Task<string> orderByFirst(string table, string field) =>
        Wrap(nameof(orderByFirst), () => QueryRows(AssertTable(table), SafeIdent(field), "ASC", 1));

    public Task<string> orderByReverse(string table, string field) =>
        Wrap(nameof(orderByReverse), () => QueryRows(AssertTable(table), SafeIdent(field), "DESC", null));

    public Task<string> orderByReverseFirst(string table, string field) =>
        Wrap(nameof(orderByReverseFirst), () => QueryRows(AssertTable(table), SafeIdent(field), "DESC", 1));

    /// <summary>ORDER BY 查询：limit=null 取全量，limit=1 取首行（对应 JS 的 First 变体）。
    /// 走 Core 的 OrderByRawRows（原始行，不做 DeserializeRecord 值转换），与直连 SQL 时代行为一致。</summary>
    private string QueryRows(string table, string field, string dir, int? limit)
    {
        var rows = _db.OrderByRawRows(table, field, dir, limit);
        // First 变体返回单对象而非数组（与原版 版行为一致）
        return limit == 1 ? ToJson(rows.FirstOrDefault()) : ToJson(rows);
    }

    public Task<string> orderByLimit(string table, string field, string limit, string reverse) =>
        Wrap(nameof(orderByLimit), () =>
        {
            if (!int.TryParse(limit, out var n) || n < 0)
                throw new ArgumentException($"Invalid limit: {limit}");
            return ToJson(_db.OrderByLimit(AssertTable(table), SafeIdent(field), n, string.Equals(reverse, "true", StringComparison.OrdinalIgnoreCase)));
        });

    public Task<string> getPage(string table, string jsonOptions) =>
        Wrap(nameof(getPage), () =>
        {
            var opts = FromJson<Dictionary<string, JsonElement>>(jsonOptions);
            if (opts == null) return "[]";
            int limit = GetInt(opts, "limit", 100);
            int offset = GetInt(opts, "offset", 0);
            var orderField = GetString(opts, "orderField");
            var orderDir = GetString(opts, "orderDir") is { } d && string.Equals(d, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            return ToJson(_db.GetPage(AssertTable(table), limit, offset,
                orderField != null ? SafeIdent(orderField) : null, orderDir,
                GetString(opts, "where") is { } wf ? SafeIdent(wf) : null, GetString(opts, "whereValue")));
        });

    private static int GetInt(Dictionary<string, JsonElement> opts, string key, int fallback) =>
        opts.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static string? GetString(Dictionary<string, JsonElement> opts, string key) =>
        opts.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ============ 聚合统计（SQL 聚合，避免全量拉取）============

    public Task<string> getStatisticsSummary(string? jsonOptions) =>
        Wrap(nameof(getStatisticsSummary), () =>
        {
            string? ym = null, yr = null;
            if (!string.IsNullOrEmpty(jsonOptions))
            {
                var o = FromJson<Dictionary<string, string>>(jsonOptions);
                o?.TryGetValue("yearMonth", out ym);
                o?.TryGetValue("year", out yr);
            }
            return ToJson(_db.GetStatisticsSummary(ym, yr));
        });

    public Task<string> getMonthlyWinRateStats(string months) =>
        Wrap(nameof(getMonthlyWinRateStats), () =>
        {
            if (!int.TryParse(months, out var m) || m < 1 || m > 60)
                throw new ArgumentException($"Invalid months: {months}");
            return ToJson(_db.GetMonthlyWinRateStats(m));
        });

    public Task<string> getTypeWinRateStats() =>
        Wrap(nameof(getTypeWinRateStats), () => ToJson(_db.GetTypeWinRateStats()));

    public Task<string> getTradeDistribution() =>
        Wrap(nameof(getTradeDistribution), () => ToJson(_db.GetTradeDistribution()));

    // ============ 导入导出 ============

    public Task<string> exportAll() =>
        Wrap(nameof(exportAll), () => ToJson(_db.ExportAll()));

    public Task<string> importAll(string jsonData) =>
        Wrap(nameof(importAll), () =>
        {
            var data = FromJson<Dictionary<string, JsonElement>>(jsonData)
                ?? throw new ArgumentException("data 反序列化失败");
            var converted = data.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var (added, updated, replaced) = _db.ImportAll(converted);
            return ToJson(new { added, updated, replaced });
        });
}
