using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Serilog;

namespace StockReview.Core.Data;

/// <summary>
/// 数据库服务
/// 使用 Dapper + Microsoft.Data.Sqlite，data.db schema 零改动
/// 提供 CRUD + 聚合查询 + 导入导出 + 自动迁移
/// </summary>
public class DatabaseService : IDatabaseService
{
    private string DbPath => Path.Combine(GetDataDir(), "data.db");
    private string _dataDir = "";

    private static readonly HashSet<string> TableSet = new()
    {
        "trades", "entryTypes", "strongStocks", "problemTags",
        "monthlySummaries", "dailySummaries", "todoTemplates", "dailyPicks",
        "appConfig", "patternCases", "insights"
    };

    private static readonly string[] TableNames =
    {
        "trades", "entryTypes", "strongStocks", "problemTags",
        "monthlySummaries", "dailySummaries", "todoTemplates", "dailyPicks",
        "appConfig", "patternCases", "insights"
    };

    // JSON 数组字段（存为 JSON 字符串）
    private static readonly HashSet<string> ArrayFields = new()
    {
        "followUp", "problemTags", "tags", "relatedCaseIds", "relatedCaseTypes"
    };

    // JSON 对象字段
    private static readonly HashSet<string> ObjectFields = new() { "evaluation" };

    // 前端临时字段前缀（序列化时过滤）
    private static readonly string[] ExcludeFieldPrefixes = { "_display", "_temp", "_v" };

    public void SetDataDir(string dataDir) => _dataDir = dataDir;

    private string GetDataDir()
    {
        if (!string.IsNullOrEmpty(_dataDir)) return _dataDir;
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    }

    /// <summary>
    /// 获取 SQLite 连接（性能 PRAGMA，busy_timeout 防多线程并发写 SQLITE_BUSY）
    /// 对应原版 init() 的 PRAGMA 设置（journal_mode=WAL 为持久化 PRAGMA，
    /// 已移至 Initialize() 一次性设置，不再放每连接热路径）。
    /// internal（2026-09-02 P5 收尾）：跨程序集消费方已清零，业务层自由 SQL 走 Query/Execute。
    /// </summary>
    internal SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA cache_size=-8000;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=67108864;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// 初始化数据库（对应原版 init()）
    /// </summary>
    public void Initialize()
    {
        Log.Information("[SQLite] 数据库初始化: {Path}", DbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // journal_mode=WAL 是持久化 PRAGMA（写入数据库文件头），整个生命周期只需设置一次；
        // 放在每连接热路径上既浪费又要短暂获取排他锁
        using (var bootstrap = new SqliteConnection($"Data Source={DbPath};Mode=ReadWriteCreate"))
        {
            bootstrap.Open();
            using var bc = bootstrap.CreateCommand();
            bc.CommandText = "PRAGMA journal_mode=WAL;";
            bc.ExecuteNonQuery();
        }

        using var conn = CreateConnection();
        CreateTables(conn);
        CreateIndexes(conn);
        MigrateTables(conn);
        InitDefaultData(conn);
        Log.Information("[SQLite] 数据库就绪");
    }

    // ============ 建表 ============

    private void CreateTables(SqliteConnection conn)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS trades (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tradeDate TEXT,
                stockCode TEXT,
                stockName TEXT,
                entryType TEXT,
                parentEntryType TEXT,
                positionStatus TEXT,
                caseType TEXT,
                firstDate TEXT,
                closePrice REAL,
                prevClose REAL,
                highPrice REAL,
                changePct REAL,
                maxChangePct REAL,
                todayPerformance TEXT,
                meetExpectation TEXT,
                exitPrice REAL,
                exitDate TEXT,
                totalReturn REAL,
                remark TEXT,
                problemTags TEXT,
                followUp TEXT,
                followUpDate TEXT,
                sellCalibrationHigh REAL,
                sellCalibrationMaxChange REAL,
                reflection TEXT,
                screenshot TEXT,
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS entryTypes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                typeName TEXT,
                description TEXT,
                sortOrder INTEGER DEFAULT 0,
                isActive INTEGER DEFAULT 1,
                parentId INTEGER,
                standardForm TEXT DEFAULT '',
                notes TEXT DEFAULT '',
                reflections TEXT DEFAULT '',
                plusItems TEXT DEFAULT '',
                minusItems TEXT DEFAULT '',
                typeImage TEXT DEFAULT '',
                standardFormImage TEXT DEFAULT '',
                color TEXT DEFAULT '',
                isStrongType INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS strongStocks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                date TEXT,
                stockCode TEXT,
                stockName TEXT,
                highPrice REAL,
                maxChangePct REAL,
                screenshot TEXT,
                strongType TEXT,
                relatedTradeIds TEXT,
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS problemTags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tagName TEXT,
                description TEXT,
                sortOrder INTEGER DEFAULT 0,
                isActive INTEGER DEFAULT 1,
                color TEXT DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS monthlySummaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                yearMonth TEXT,
                summary TEXT,
                title TEXT DEFAULT '',
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS dailySummaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recordDate TEXT,
                summaryType TEXT DEFAULT 'daily',
                summary TEXT,
                title TEXT DEFAULT '',
                startDate TEXT,
                endDate TEXT,
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS todoTemplates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fieldName TEXT,
                question TEXT,
                options TEXT,
                isActive INTEGER DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS dailyPicks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pickDate TEXT,
                stockName TEXT,
                stockCode TEXT,
                price REAL,
                change REAL,
                pickType TEXT,
                isSelected INTEGER DEFAULT 0,
                remark TEXT,
                screenshot TEXT,
                nextDayHighPrice REAL,
                nextDayMaxChange REAL,
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS appConfig (
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS patternCases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entryType TEXT,
                caseType TEXT,
                stockCode TEXT,
                stockName TEXT,
                tradeDate TEXT,
                totalReturn REAL,
                screenshot TEXT,
                reflection TEXT,
                createdAt TEXT,
                updatedAt TEXT
            );

            CREATE TABLE IF NOT EXISTS insights (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recordDate TEXT,
                title TEXT,
                content TEXT,
                importance INTEGER DEFAULT 0,
                relatedCaseId INTEGER,
                relatedCaseType TEXT,
                relatedCaseIds TEXT,
                relatedCaseTypes TEXT,
                stockCode TEXT,
                stockName TEXT,
                tags TEXT,
                screenshot TEXT,
                isPinned INTEGER DEFAULT 0,
                pinnedAt TEXT,
                createdAt TEXT,
                updatedAt TEXT
            );
        ";
        conn.Execute(sql);
    }

    // ============ 索引 ============

    private void CreateIndexes(SqliteConnection conn)
    {
        const string sql = @"
            CREATE INDEX IF NOT EXISTS idx_trades_tradeDate ON trades(tradeDate);
            CREATE INDEX IF NOT EXISTS idx_trades_stockCode ON trades(stockCode);
            CREATE INDEX IF NOT EXISTS idx_trades_positionStatus ON trades(positionStatus);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_trades_date_code ON trades(tradeDate, stockCode);

            CREATE INDEX IF NOT EXISTS idx_strongStocks_date ON strongStocks(date);
            CREATE INDEX IF NOT EXISTS idx_strongStocks_stockCode ON strongStocks(stockCode);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_strongStocks_date_code ON strongStocks(date, stockCode);

            CREATE INDEX IF NOT EXISTS idx_dailyPicks_pickDate ON dailyPicks(pickDate);

            CREATE INDEX IF NOT EXISTS idx_entryTypes_sortOrder ON entryTypes(sortOrder);
            CREATE INDEX IF NOT EXISTS idx_entryTypes_parentId ON entryTypes(parentId);

            CREATE INDEX IF NOT EXISTS idx_problemTags_sortOrder ON problemTags(sortOrder);

            CREATE INDEX IF NOT EXISTS idx_insights_recordDate ON insights(recordDate);

            CREATE INDEX IF NOT EXISTS idx_dailySummaries_recordDate ON dailySummaries(recordDate);
            CREATE INDEX IF NOT EXISTS idx_dailySummaries_summaryType ON dailySummaries(summaryType);
            CREATE INDEX IF NOT EXISTS idx_monthlySummaries_yearMonth ON monthlySummaries(yearMonth);
        ";
        conn.Execute(sql);
    }

    // ============ 迁移 ============

    private void MigrateTables(SqliteConnection conn)
    {
        // trades 表列迁移
        var tradesCols = GetTableColumns(conn, "trades");
        var tradeMigrations = new (string col, string type)[]
        {
            ("closePrice", "REAL"), ("prevClose", "REAL"), ("changePct", "REAL"),
            ("todayPerformance", "TEXT"), ("meetExpectation", "TEXT"),
            ("exitPrice", "REAL"), ("exitDate", "TEXT"), ("totalReturn", "REAL"),
            ("remark", "TEXT")
        };
        foreach (var (col, type) in tradeMigrations)
        {
            if (!tradesCols.Contains(col))
            {
                try { conn.Execute($"ALTER TABLE trades ADD COLUMN \"{col}\" {type}"); }
                catch (Exception ex) { Log.Warning("[SQLite] 添加 trades.{Col} 失败: {Msg}", col, ex.Message); }
            }
        }

        // dailyPicks 表 evaluation 列
        var pickCols = GetTableColumns(conn, "dailyPicks");
        if (!pickCols.Contains("evaluation"))
        {
            try { conn.Execute("ALTER TABLE dailyPicks ADD COLUMN evaluation TEXT"); }
            catch (Exception ex) { Log.Warning("[SQLite] 添加 evaluation 失败: {Msg}", ex.Message); }
        }

        // strongStocks 表 closePrice / changePct 列（旧版 Dexie 自由存储，SQLite 需显式迁移）
        var strongCols = GetTableColumns(conn, "strongStocks");
        if (!strongCols.Contains("closePrice"))
        {
            try { conn.Execute("ALTER TABLE strongStocks ADD COLUMN closePrice REAL"); }
            catch (Exception ex) { Log.Warning("[SQLite] 添加 strongStocks.closePrice 失败: {Msg}", ex.Message); }
        }
        if (!strongCols.Contains("changePct"))
        {
            try { conn.Execute("ALTER TABLE strongStocks ADD COLUMN changePct REAL"); }
            catch (Exception ex) { Log.Warning("[SQLite] 添加 strongStocks.changePct 失败: {Msg}", ex.Message); }
        }

        // insights 表置顶字段
        MigrateInsightsPinnedFields(conn);

        // entryTypes：拆分加减分为独立字段，notes 变为独立富文本「注意事项」
        MigrateEntryTypeItemFields(conn);

        // 旧版备份兼容列：旧版应用用 Dexie/SQLite 自由存储的额外字段，
        // WPF 表若无这些列，ImportAll 按"备份字段名=列名"直插会整行失败
        var legacyCompatCols = new Dictionary<string, (string col, string type)[]>
        {
            ["trades"] = new[]
            {
                ("caseTag", "TEXT"), ("entryPrice", "REAL"), ("isStrongToday", "INTEGER"),
                ("notes", "TEXT"), ("positionSize", "TEXT"), ("tags", "TEXT")
            },
            ["entryTypes"] = new[] { ("createdAt", "TEXT"), ("updatedAt", "TEXT") },
            ["strongStocks"] = new[] { ("isManual", "INTEGER"), ("reason", "TEXT") },
            ["problemTags"] = new[] { ("createdAt", "TEXT"), ("updatedAt", "TEXT") },
            ["dailyPicks"] = new[] { ("reason", "TEXT") },
            ["dailySummaries"] = new[] { ("content", "TEXT"), ("mood", "TEXT") },
            ["todoTemplates"] = new[]
            {
                ("category", "TEXT"), ("content", "TEXT"), ("createdAt", "TEXT"),
                ("title", "TEXT"), ("updatedAt", "TEXT")
            },
            ["appConfig"] = new[] { ("createdAt", "TEXT"), ("updatedAt", "TEXT") },
            ["insights"] = new[] { ("category", "TEXT") }
        };
        foreach (var (table, cols) in legacyCompatCols)
        {
            var existing = GetTableColumns(conn, table);
            foreach (var (col, type) in cols)
            {
                if (!existing.Contains(col))
                {
                    try { conn.Execute($"ALTER TABLE \"{table}\" ADD COLUMN \"{col}\" {type}"); }
                    catch (Exception ex) { Log.Warning("[SQLite] 添加 {Table}.{Col} 失败: {Msg}", table, col, ex.Message); }
                }
            }
        }
    }

    private void MigrateEntryTypeItemFields(SqliteConnection conn)
    {
        var cols = GetTableColumns(conn, "entryTypes");
        if (!cols.Contains("plusItems"))
        {
            try { conn.Execute("ALTER TABLE entryTypes ADD COLUMN plusItems TEXT DEFAULT ''"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 entryTypes.plusItems 失败: {Msg}", ex.Message); }
        }
        if (!cols.Contains("minusItems"))
        {
            try { conn.Execute("ALTER TABLE entryTypes ADD COLUMN minusItems TEXT DEFAULT ''"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 entryTypes.minusItems 失败: {Msg}", ex.Message); }
        }
        // 设置页读写 color / isStrongType（旧版库无这两列，缺失会导致保存报 no such column）
        if (!cols.Contains("color"))
        {
            try { conn.Execute("ALTER TABLE entryTypes ADD COLUMN color TEXT DEFAULT ''"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 entryTypes.color 失败: {Msg}", ex.Message); }
        }
        if (!cols.Contains("isStrongType"))
        {
            try { conn.Execute("ALTER TABLE entryTypes ADD COLUMN isStrongType INTEGER DEFAULT 0"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 entryTypes.isStrongType 失败: {Msg}", ex.Message); }
        }
        var tagCols = GetTableColumns(conn, "problemTags");
        if (!tagCols.Contains("color"))
        {
            try { conn.Execute("ALTER TABLE problemTags ADD COLUMN color TEXT DEFAULT ''"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 problemTags.color 失败: {Msg}", ex.Message); }
        }
        // 幂等拆分：加减分两列为空而 notes 含内容（旧版以 notes 为唯一存储），
        // 覆盖两种场景：a) 列刚创建（全部行为空）b) 旧版备份导入后（备份无此二列）
        // 用户在 WPF 中编辑过加减分后此二列非空，对应行自动跳过
        {
            var rows = conn.Query(
                "SELECT id, notes FROM entryTypes WHERE (plusItems IS NULL OR plusItems='') AND (minusItems IS NULL OR minusItems='') AND notes IS NOT NULL AND notes != ''").ToList();
            foreach (var r in rows)
            {
                var notes = r.notes as string ?? "";
                var plus = new List<string>();
                var minus = new List<string>();
                var other = new List<string>();
                foreach (var raw in notes.Split('\n'))
                {
                    var line = raw.TrimEnd('\r').Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    var l = line.TrimStart();
                    if (l.StartsWith("+")) plus.Add(l.TrimStart('+').Trim());
                    else if (l.StartsWith("【加分】")) plus.Add(l["【加分】".Length..].Trim());
                    else if (l.StartsWith("加分：") || l.StartsWith("加分项：")) plus.Add(l[(l.IndexOf('：') + 1)..].Trim());
                    else if (l.StartsWith("-")) minus.Add(l.TrimStart('-').Trim());
                    else if (l.StartsWith("【减分】")) minus.Add(l["【减分】".Length..].Trim());
                    else if (l.StartsWith("减分：") || l.StartsWith("减分项：")) minus.Add(l[(l.IndexOf('：') + 1)..].Trim());
                    else if (l.Contains("加分") || l.Contains("优点") || l.Contains("好的")) plus.Add(l);
                    else if (l.Contains("减分") || l.Contains("缺点") || l.Contains("注意")) minus.Add(l);
                    else other.Add(l);
                }
                conn.Execute(
                    "UPDATE entryTypes SET plusItems=@p, minusItems=@m, notes=@n WHERE id=@id",
                    new { p = string.Join("\n", plus), m = string.Join("\n", minus), n = string.Join("\n", other), id = r.id });
            }
        }
    }

    private void MigrateInsightsPinnedFields(SqliteConnection conn)
    {
        var cols = GetTableColumns(conn, "insights");
        if (!cols.Contains("isPinned"))
        {
            try { conn.Execute("ALTER TABLE insights ADD COLUMN isPinned INTEGER DEFAULT 0"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 insights.isPinned 失败: {Msg}", ex.Message); }
        }
        if (!cols.Contains("pinnedAt"))
        {
            try { conn.Execute("ALTER TABLE insights ADD COLUMN pinnedAt TEXT"); }
            catch (Exception ex) { Log.Warning("[SQLite] 迁移 insights.pinnedAt 失败: {Msg}", ex.Message); }
        }
    }

    // ============ 默认数据 ============

    private void InitDefaultData(SqliteConnection conn)
    {
        // 进场类型
        var entryCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM entryTypes");
        if (entryCount == 0)
        {
            var defaults = new[]
            {
                ("突破", "价格突破关键阻力位", 1),
                ("回踩", "突破后回踩支撑位", 2),
                ("低吸", "低位吸纳", 3),
                ("打板", "涨停打板", 4),
                ("趋势", "趋势跟随", 5),
                ("其他", "其他类型", 99)
            };
            conn.Execute("INSERT INTO entryTypes (typeName, description, sortOrder, isActive) VALUES (@a, @b, @c, 1)",
                defaults.Select(d => new { a = d.Item1, b = d.Item2, c = d.Item3 }));
            Log.Information("[SQLite] 初始化默认进场类型");
        }

        // 问题标签
        var tagCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM problemTags");
        if (tagCount == 0)
        {
            var tags = new[]
            {
                ("拖延", "行动迟缓，错过最佳时机", 1),
                ("粗心", "细节处理不当，出现失误", 2),
                ("计划不足", "缺乏详细计划和策略", 3),
                ("情绪干扰", "受情绪影响，理性判断受损", 4),
                ("执行力差", "计划执行不到位", 5),
                ("臆想", "缺乏依据的主观判断", 6)
            };
            conn.Execute("INSERT INTO problemTags (tagName, description, sortOrder, isActive) VALUES (@a, @b, @c, 1)",
                tags.Select(t => new { a = t.Item1, b = t.Item2, c = t.Item3 }));
            Log.Information("[SQLite] 初始化默认问题标签");
        }

        // 待办模板
        var tplCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM todoTemplates");
        if (tplCount == 0)
        {
            var templates = new[]
            {
                ("todayPerformance", "今日表现如何？", JsonSerializer.Serialize(new[] { "超预期", "符合预期", "低于预期", "止损", "止盈" })),
                ("meetExpectation", "是否符合预期？", JsonSerializer.Serialize(new[] { "是", "否", "部分符合" }))
            };
            conn.Execute("INSERT INTO todoTemplates (fieldName, question, options, isActive) VALUES (@a, @b, @c, 1)",
                templates.Select(t => new { a = t.Item1, b = t.Item2, c = t.Item3 }));
            Log.Information("[SQLite] 初始化默认待办模板");
        }
    }

    // ============ 表级操作 ============

    public List<Dictionary<string, object?>> GetAll(string table)
    {
        AssertTable(table);
        using var conn = CreateConnection();
        var rows = conn.Query($"SELECT * FROM \"{table}\"");
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public Dictionary<string, object?>? GetById(string table, object id)
    {
        AssertTable(table);
        var conn = LeaseConnection(out var owns);
        try
        {
            if (table == "appConfig")
            {
                var row = conn.QueryFirstOrDefault("SELECT * FROM appConfig WHERE key = @id", new { id }, _ambientTx);
                return row != null ? DeserializeRecord((IDictionary<string, object>)row) : null;
            }
            var r = conn.QueryFirstOrDefault($"SELECT * FROM \"{table}\" WHERE id = @id", new { id }, _ambientTx);
            return r != null ? DeserializeRecord((IDictionary<string, object>)r) : null;
        }
        finally { if (owns) conn.Dispose(); }
    }

    /// <summary>appConfig KV 表专用写入（INSERT OR REPLACE 语义），Add/Put 共用。</summary>
    private object PutAppConfig(IDictionary<string, object?> data)
    {
        var key = (data.TryGetValue("key", out var kv) ? kv : null)?.ToString() ?? "";
        var val = (data.TryGetValue("value", out var vv) ? vv : null)?.ToString() ?? "";
        using var conn = CreateConnection();
        conn.Execute("INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)", new { key, val });
        return key;
    }

    /// <summary>
    /// 交易/强股数据版本号（进程内累计）：对 trades / strongStocks 的任何写操作自增。
    /// 汇总统计 WebView 页加载时快照该值，导航回来时若已变化则硬刷新页面，
    /// 避免"新增交易后统计页当月分析仍显示旧数据"（页面内 SPA 数据不随导航重载）。
    /// </summary>
    private static long _statsDataVersion;
    public static long StatsDataVersion => System.Threading.Volatile.Read(ref _statsDataVersion);

    /// <summary>仅统计页消费的两张表写入时推进版本号。</summary>
    private static void BumpStatsVersion(string table)
    {
        if (table == "trades" || table == "strongStocks")
            System.Threading.Interlocked.Increment(ref _statsDataVersion);
    }

    // ============ 环境事务（2026-09-04，archify 模式对照 P1：跨表多步写序列的原子性） ============

    /// <summary>事务串行化门闩：同一时刻只允许一个环境事务体在执行（lock 可重入，支持同线程嵌套）。</summary>
    private readonly object _txGate = new();

    /// <summary>环境事务连接：事务体内所有读写复用它（不跨线程流动）。</summary>
    private SqliteConnection? _ambientConn;

    /// <summary>环境事务：事务体内的语句必须显式挂载它，否则各自独立提交、不受回滚控制。</summary>
    private SqliteTransaction? _ambientTx;

    /// <summary>
    /// 在单个 SQLite 事务中执行多步读写：body 内的 Add/Update/Delete/Put/GetById/
    /// WhereCompound(First) 会复用同一连接并挂到该事务，任一步抛异常则整体回滚。
    /// 约束：body 必须为同步、单线程 lambda（内部不得再 Task.Run / await——环境事务不跨线程流动）；
    /// 事务期间持有 _txGate，请勿在 body 内做耗时非 DB 操作。同线程嵌套调用会加入外层事务。
    /// </summary>
    public T RunInTransaction<T>(Func<T> body)
    {
        lock (_txGate)
        {
            if (_ambientTx != null) return body(); // 同线程重入：直接加入外层事务
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            _ambientConn = conn;
            _ambientTx = tx;
            try
            {
                var result = body();
                tx.Commit();
                return result;
            }
            finally
            {
                // 异常未 Commit 时 tx.Dispose 自动回滚
                _ambientConn = null;
                _ambientTx = null;
            }
        }
    }

    /// <summary><see cref="RunInTransaction{T}"/> 的无返回值重载。</summary>
    public void RunInTransaction(Action body) =>
        RunInTransaction<object?>(() => { body(); return null; });

    /// <summary>
    /// 获取命令执行用连接：环境事务内复用事务连接（owned=false，调用方不得 Dispose），
    /// 否则新建并由调用方释放。配套地把 <see cref="_ambientTx"/> 传给 Dapper 的 transaction 参数。
    /// </summary>
    private SqliteConnection LeaseConnection(out bool owned)
    {
        if (_ambientConn != null)
        {
            owned = false;
            return _ambientConn;
        }
        owned = true;
        return CreateConnection();
    }

    public object Add(string table, IDictionary<string, object?> data)
    {
        AssertTable(table);
        var now = DateTime.UtcNow.ToString("o");
        if (table == "appConfig")
            return PutAppConfig(data);
        BumpStatsVersion(table);
        var serialized = SerializeRecord(data);
        serialized["createdAt"] = now;
        serialized["updatedAt"] = now;
        var keys = serialized.Keys.ToList();
        foreach (var k in keys) AssertIdentifier(k);
        var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
        // 命名参数绑定：Dapper 对 Dictionary 按 @key 命名绑定、不认 ? 位置占位
        // （同 ImportAll 中已修复的写法，见其注释）
        var ph = string.Join(", ", keys.Select(k => $"@{k}"));
        var sql = $"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})";
        var c = LeaseConnection(out var ownsConn);
        try
        {
            c.Execute(sql, serialized, _ambientTx);
            return c.ExecuteScalar<long>("SELECT last_insert_rowid()", _ambientTx);
        }
        finally { if (ownsConn) c.Dispose(); }
    }

    public bool Update(string table, object id, IDictionary<string, object?> data)
    {
        AssertTable(table);
        if (table == "appConfig")
        {
            var val = data.ContainsKey("value") ? data["value"] : data;
            using var conn = CreateConnection();
            return conn.Execute("UPDATE appConfig SET value = @val WHERE key = @id", new { val, id }) > 0;
        }
        BumpStatsVersion(table);
        var serialized = SerializeRecord(data);
        serialized["updatedAt"] = DateTime.UtcNow.ToString("o");
        var keys = serialized.Keys.ToList();
        foreach (var k in keys) AssertIdentifier(k);
        var setClause = string.Join(", ", keys.Select(k => $"\"{k}\" = @{k}"));
        serialized["__id"] = id;
        var sql = $"UPDATE \"{table}\" SET {setClause} WHERE id = @__id";
        var c = LeaseConnection(out var ownsConn);
        try { return c.Execute(sql, serialized, _ambientTx) > 0; }
        finally { if (ownsConn) c.Dispose(); }
    }

    public bool Delete(string table, object id)
    {
        AssertTable(table);
        if (table == "appConfig")
        {
            var ac = LeaseConnection(out var ownsAc);
            try { return ac.Execute("DELETE FROM appConfig WHERE key = @id", new { id }, _ambientTx) > 0; }
            finally { if (ownsAc) ac.Dispose(); }
        }
        BumpStatsVersion(table);
        var c = LeaseConnection(out var ownsConn);
        try { return c.Execute($"DELETE FROM \"{table}\" WHERE id = @id", new { id }, _ambientTx) > 0; }
        finally { if (ownsConn) c.Dispose(); }
    }

    public object Put(string table, IDictionary<string, object?> data)
    {
        AssertTable(table);
        if (table == "appConfig")
            return PutAppConfig(data);
        BumpStatsVersion(table);

        var serialized = SerializeRecord(data);
        var now = DateTime.UtcNow.ToString("o");

        // 环境事务内：直接挂到外层事务（由调用方决定提交/回滚）。
        if (_ambientTx != null)
            return PutCore(table, data, serialized, now, _ambientConn!, _ambientTx);

        // 独立调用：先 UPDATE、受影响 0 行再 INSERT 的两语句必须包进本地事务，
        // 否则两个并发 Put 同 id 都走 UPDATE 0 行 → 各自 INSERT → 重复行
        // （旧注释称"单连接原子 upsert"，实际单连接内每条语句独立提交，并不原子）。
        using var c = CreateConnection();
        using var tx = c.BeginTransaction();
        try
        {
            var result = PutCore(table, data, serialized, now, c, tx);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Put 的两语句主体：执行时必须显式挂载给定事务。</summary>
    private object PutCore(string table, IDictionary<string, object?> data,
        Dictionary<string, object?> serialized, string now,
        SqliteConnection c, SqliteTransaction tx)
    {
        if (data.TryGetValue("id", out var idObj) && idObj != null)
        {
            serialized["updatedAt"] = now;
            var cols = serialized.Keys.ToList();
            foreach (var k in cols) AssertIdentifier(k);
            var setClause = string.Join(", ", cols.Select(k => $"\"{k}\" = @{k}"));
            // 独立参数字典：避免把 WHERE 用的 __id 混进下方 INSERT 的列清单
            var updateParams = new Dictionary<string, object?>(serialized) { ["__id"] = idObj };
            var updated = c.Execute($"UPDATE \"{table}\" SET {setClause} WHERE id = @__id", updateParams, tx);
            if (updated > 0) return idObj;
        }

        serialized["createdAt"] = now;
        serialized["updatedAt"] = now;
        var keys = serialized.Keys.ToList();
        foreach (var k in keys) AssertIdentifier(k);
        var colList = string.Join(", ", keys.Select(k => $"\"{k}\""));
        var ph = string.Join(", ", keys.Select(k => $"@{k}"));
        c.Execute($"INSERT INTO \"{table}\" ({colList}) VALUES ({ph})", serialized, tx);
        return c.ExecuteScalar<long>("SELECT last_insert_rowid()", tx);
    }

    public void BulkPut(string table, IEnumerable<IDictionary<string, object?>> items)
    {
        AssertTable(table);
        var list = items.ToList();
        if (list.Count == 0) return;
        BumpStatsVersion(table);
        Log.Information("[SQLite] bulkPut {Table}: {Count} 条", table, list.Count);

        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 批量查询已存在 ID
            var ids = list.Select(i => i.TryGetValue("id", out var v) ? v : null).Where(i => i != null).ToList();
            var existingIds = new HashSet<object>();
            if (ids.Count > 0)
            {
                const int batchSize = 500;
                for (var i = 0; i < ids.Count; i += batchSize)
                {
                    var batch = ids.Skip(i).Take(batchSize).ToList();
                    // Dapper 列表展开：IN @ids 自动重写为 IN (@ids1, @ids2, ...)；
                    // 旧的 IN (?,?...) + List 位置绑定不生效
                    var rows = conn.Query($"SELECT id FROM \"{table}\" WHERE id IN @ids", new { ids = batch });
                    foreach (var r in rows) existingIds.Add(r.id);
                }
            }

            foreach (var item in list)
            {
                var serialized = SerializeRecord(item);
                var now = DateTime.UtcNow.ToString("o");
                serialized["updatedAt"] = now;
                if (!serialized.ContainsKey("createdAt") || serialized["createdAt"] == null)
                    serialized["createdAt"] = now;

                if (serialized.TryGetValue("id", out var idVal) && idVal != null && existingIds.Contains(idVal))
                {
                    var keys = serialized.Keys.Where(k => k != "id").ToList();
                    var setClause = string.Join(", ", keys.Select(k => $"\"{k}\" = @{k}"));
                    serialized["__id"] = idVal;
                    try { conn.Execute($"UPDATE \"{table}\" SET {setClause} WHERE id = @__id", serialized, tx); }
                    catch (Exception ex) { Log.Error("[SQLite] 更新 {Table} id={Id} 失败: {Msg}", table, idVal, ex.Message); }
                }
                else
                {
                    var keys = serialized.Keys.ToList();
                    foreach (var k in keys) AssertIdentifier(k);
                    var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
                    // 命名参数绑定：Dapper 把数组参数当作批量执行而非位置绑定，
                    // 旧的 VALUES(?) + ToArray() 写法从未真正插入过数据（异常被吞）
                    var ph = string.Join(", ", keys.Select(k => $"@{k}"));
                    try { conn.Execute($"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})", serialized, tx); }
                    catch (Exception ex) { Log.Error("[SQLite] 插入 {Table} 失败: {Msg}", table, ex.Message); }
                }
            }
            tx.Commit();
            Log.Information("[SQLite] bulkPut {Table} 完成", table);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void BulkAdd(string table, IEnumerable<IDictionary<string, object?>> items)
    {
        AssertTable(table);
        var list = items.ToList();
        if (list.Count == 0) return;
        BumpStatsVersion(table);
        var now = DateTime.UtcNow.ToString("o");
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var item in list)
            {
                var serialized = SerializeRecord(item);
                serialized["createdAt"] = now;
                serialized["updatedAt"] = now;
                var keys = serialized.Keys.ToList();
                foreach (var k in keys) AssertIdentifier(k);
                var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
                // 命名参数绑定：旧 VALUES(?) + ToArray() 位置绑定不生效（同 BulkPut 修复）
                var ph = string.Join(", ", keys.Select(k => $"@{k}"));
                conn.Execute($"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})", serialized, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void Clear(string table)
    {
        AssertTable(table);
        BumpStatsVersion(table);
        using var conn = CreateConnection();
        conn.Execute($"DELETE FROM \"{table}\"");
    }

    public long Count(string table)
    {
        AssertTable(table);
        using var conn = CreateConnection();
        return conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM \"{table}\"");
    }

    public void DeleteDatabase()
    {
        if (File.Exists(DbPath)) File.Delete(DbPath);
        var wal = DbPath + "-wal";
        var shm = DbPath + "-shm";
        if (File.Exists(wal)) File.Delete(wal);
        if (File.Exists(shm)) File.Delete(shm);
    }

    // ============ 高级查询 ============

    public List<Dictionary<string, object?>> WhereEquals(string table, string field, object value)
    {
        AssertTable(table);
        using var conn = CreateConnection();
        var rows = conn.Query($"SELECT * FROM \"{table}\" WHERE \"{field}\" = @val", new { val = value });
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public List<Dictionary<string, object?>> WhereStartsWith(string table, string field, string value)
    {
        AssertTable(table);
        AssertIdentifier(field);
        using var conn = CreateConnection();
        var rows = conn.Query($"SELECT * FROM \"{table}\" WHERE \"{field}\" LIKE @val", new { val = value + "%" });
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public List<Dictionary<string, object?>> WhereAnyOf(string table, string field, IEnumerable<object> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return new List<Dictionary<string, object?>>();
        AssertTable(table);
        AssertIdentifier(field);
        using var conn = CreateConnection();
        // Dapper 列表展开：IN @vals 自动重写为 IN (@vals1, ...)；旧 IN (?,?..) + List 位置绑定不生效
        var rows = conn.Query($"SELECT * FROM \"{table}\" WHERE \"{field}\" IN @vals", new { vals = list });
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public Dictionary<string, object?>? WhereCompoundFirst(string table, IDictionary<string, object> conditions)
    {
        var (where, param) = BuildWhereClause(conditions);
        var sql = $"SELECT * FROM \"{table}\" WHERE {where} LIMIT 1";
        var conn = LeaseConnection(out var owns);
        try
        {
            var row = conn.QueryFirstOrDefault(sql, param, _ambientTx);
            return row != null ? DeserializeRecord((IDictionary<string, object>)row) : null;
        }
        finally { if (owns) conn.Dispose(); }
    }

    public List<Dictionary<string, object?>> WhereCompound(string table, IDictionary<string, object> conditions)
    {
        var (where, param) = BuildWhereClause(conditions);
        var sql = $"SELECT * FROM \"{table}\" WHERE {where}";
        var conn = LeaseConnection(out var owns);
        try
        {
            var rows = conn.Query(sql, param, _ambientTx);
            return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
        }
        finally { if (owns) conn.Dispose(); }
    }

    public List<Dictionary<string, object?>> WhereBetween(string table, string field, object lower, object upper)
    {
        AssertTable(table);
        using var conn = CreateConnection();
        var rows = conn.Query($"SELECT * FROM \"{table}\" WHERE \"{field}\" BETWEEN @lo AND @hi", new { lo = lower, hi = upper });
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public Dictionary<string, object?>? WhereFirst(string table, string field, object value)
    {
        AssertTable(table);
        using var conn = CreateConnection();
        var row = conn.QueryFirstOrDefault($"SELECT * FROM \"{table}\" WHERE \"{field}\" = @val LIMIT 1", new { val = value });
        return row != null ? DeserializeRecord((IDictionary<string, object>)row) : null;
    }

    public List<Dictionary<string, object?>> GetPage(string table, int limit = 100, int offset = 0,
        string? orderField = null, string orderDir = "ASC", string? where = null, object? whereValue = null)
    {
        AssertTable(table);
        var conditions = new List<string>();
        var param = new ExpandoObject() as IDictionary<string, object>;
        if (where != null && whereValue != null)
        {
            conditions.Add($"\"{where}\" = @wv");
            param["wv"] = whereValue;
        }
        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var orderClause = orderField != null ? $"ORDER BY \"{orderField}\" {(orderDir == "DESC" ? "DESC" : "ASC")}" : "";
        param["limit"] = limit;
        param["offset"] = offset;
        var sql = $"SELECT * FROM \"{table}\" {whereClause} {orderClause} LIMIT @limit OFFSET @offset";
        using var conn = CreateConnection();
        var rows = conn.Query(sql, param);
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    public List<Dictionary<string, object?>> OrderByLimit(string table, string field, int limit, bool reverse = false)
    {
        AssertTable(table);
        var dir = reverse ? "DESC" : "ASC";
        using var conn = CreateConnection();
        var rows = conn.Query($"SELECT * FROM \"{table}\" ORDER BY \"{field}\" {dir} LIMIT @limit", new { limit });
        return rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList();
    }

    /// <summary>
    /// ORDER BY 原始行查询（WebBridge orderBy 变体专用）。
    /// 与 <see cref="OrderByLimit"/> 的区别：不做 DeserializeRecord 值转换
    /// （is* 字段保持 0/1、ArrayFields 保持 JSON 字符串），返回 Dapper 原始行，
    /// 与下沉前 DbHostObject.QueryRows 直连 CreateConnection 的行为逐字节一致。
    /// </summary>
    /// <param name="dir">排序方向，仅接受 "ASC"/"DESC"</param>
    /// <param name="limit">null=全量；1=LIMIT 1（First 变体）</param>
    public List<IDictionary<string, object>> OrderByRawRows(string table, string field, string dir, int? limit)
    {
        AssertTable(table);
        if (dir != "ASC" && dir != "DESC")
            throw new ArgumentException($"Invalid dir: {dir}");
        using var conn = CreateConnection();
        var sql = $"SELECT * FROM \"{table}\" ORDER BY \"{field}\" {dir}" + (limit == 1 ? " LIMIT 1" : "");
        return conn.Query(sql).Cast<IDictionary<string, object>>().ToList();
    }

    // ============ 聚合查询 ============

    /// <summary>
    /// 分页查询案例（带筛选、搜索、排序）
    /// 对应原版 queryCasesPaginated
    /// </summary>
    public (List<Dictionary<string, object?>> data, long total) QueryCasesPaginated(
        string caseType = "all", string entryType = "", List<string>? entryTypes = null,
        string keyword = "", string sortBy = "date_desc", int page = 1, int pageSize = 30)
    {
        var conditions = new List<string> { "(caseType IS NOT NULL AND caseType != '' AND caseType != '未归类')" };
        var param = new ExpandoObject() as IDictionary<string, object>;

        if (caseType == "success")
            conditions.Add("caseType = '成功案例'");
        else if (caseType == "fail")
            conditions.Add("caseType = '失败案例'");
        else if (caseType == "calibration")
            // 卖点校准：仅收录真正做过校准复盘的案例——有校准日期（followUpDate）或有反思内容；
            // 仅勾了后续追踪标签（followUp）但两者皆无的案例不计入（与 CasesViewModel LINQ 筛选同口径）。
            conditions.Add("((followUpDate IS NOT NULL AND TRIM(followUpDate) != '') OR (reflection IS NOT NULL AND TRIM(reflection) != ''))");

        if (!string.IsNullOrEmpty(entryType))
        {
            conditions.Add("entryType = @et");
            param["et"] = entryType;
        }
        else if (entryTypes != null && entryTypes.Count > 0)
        {
            // 统一命名参数（与同语句的 @et/@kw/@limit 保持一致），禁止 ? 位置占位与 @ 命名参数混用
            var ph = string.Join(",", entryTypes.Select((_, i) => $"@e{i}"));
            conditions.Add($"entryType IN ({ph})");
            for (var i = 0; i < entryTypes.Count; i++) param[$"e{i}"] = entryTypes[i];
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            conditions.Add("(stockCode LIKE @kw OR stockName LIKE @kw OR (remark IS NOT NULL AND remark LIKE @kw) OR (reflection IS NOT NULL AND reflection LIKE @kw))");
            param["kw"] = $"%{keyword}%";
        }

        var whereClause = string.Join(" AND ", conditions);
        var orderBy = sortBy switch
        {
            "change_desc" => "CAST(totalReturn AS REAL) DESC",
            "change_asc" => "CAST(totalReturn AS REAL) ASC",
            "date_asc" => "tradeDate ASC",
            _ => "tradeDate DESC"
        };

        using var conn = CreateConnection();
        var total = conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM trades WHERE {whereClause}", param);
        param["limit"] = pageSize;
        param["offset"] = (page - 1) * pageSize;
        var rows = conn.Query($"SELECT * FROM trades WHERE {whereClause} ORDER BY {orderBy} LIMIT @limit OFFSET @offset", param);
        return (rows.Select(r => DeserializeRecord((IDictionary<string, object>)r)).ToList(), total);
    }

    // ===== P5 下沉的领域查询（原 ViewModel 内联 SQL，2026-09-02 移入 Core） =====
    // 注意：以下方法保持原 VM 的行转换语义（纯字典复制，不走 DeserializeRecord 的 JSON 还原），零行为偏差。

    /// <summary>日记/总结查重：同区间同类型记录（Insights/YearMonth 保存日记前共用）。</summary>
    public List<Dictionary<string, object?>> GetDailySummariesInRange(string startDate, string endDate, string summaryType)
    {
        using var conn = CreateConnection();
        var rows = conn.Query(
            "SELECT * FROM dailySummaries WHERE recordDate >= @start AND recordDate <= @end AND summaryType = @type",
            new { start = startDate, end = endDate, type = summaryType });
        return rows.Select(r => (IDictionary<string, object>)r)
            .Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    /// <summary>启用中的进场类型（录入表单/年月页下拉共用）。</summary>
    public List<Dictionary<string, object?>> GetActiveEntryTypes()
    {
        using var conn = CreateConnection();
        var rows = conn.Query("SELECT * FROM entryTypes WHERE isActive = 1 ORDER BY sortOrder");
        return rows.Select(r => (IDictionary<string, object>)r)
            .Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    /// <summary>启用中的问题标签（录入表单下拉）。</summary>
    public List<Dictionary<string, object?>> GetActiveProblemTags()
    {
        using var conn = CreateConnection();
        var rows = conn.Query("SELECT * FROM problemTags WHERE isActive = 1 ORDER BY sortOrder");
        return rows.Select(r => (IDictionary<string, object>)r)
            .Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    /// <summary>按年份前缀取交易（tradeDate LIKE 'yyyy-%'，createdAt 倒序）。</summary>
    public List<Dictionary<string, object?>> GetTradesByYearPrefix(string yearPrefix)
    {
        using var conn = CreateConnection();
        var rows = conn.Query(
            "SELECT * FROM trades WHERE tradeDate LIKE @pattern ORDER BY createdAt DESC",
            new { pattern = $"{yearPrefix}%" });
        return rows.Select(r => (IDictionary<string, object>)r)
            .Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    /// <summary>按年份前缀取强股（date LIKE 'yyyy-%'，createdAt 倒序）。</summary>
    public List<Dictionary<string, object?>> GetStrongStocksByYearPrefix(string yearPrefix)
    {
        using var conn = CreateConnection();
        var rows = conn.Query(
            "SELECT * FROM strongStocks WHERE date LIKE @pattern ORDER BY createdAt DESC",
            new { pattern = $"{yearPrefix}%" });
        return rows.Select(r => (IDictionary<string, object>)r)
            .Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    /// <summary>
    /// 主进程聚合统计摘要（避免全量加载到渲染进程）
    /// </summary>
    public object GetStatisticsSummary(string? yearMonth = null, string? year = null)
    {
        var conditions = new List<string>();
        var param = new ExpandoObject() as IDictionary<string, object>;

        if (!string.IsNullOrEmpty(yearMonth))        {
            conditions.Add("tradeDate LIKE @ym");
            param["ym"] = yearMonth + "%";
        }
        if (!string.IsNullOrEmpty(year))
        {
            conditions.Add("tradeDate LIKE @y");
            param["y"] = year + "%";
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        using var conn = CreateConnection();
        var baseStats = conn.QueryFirstOrDefault($@"
            SELECT
                COUNT(*) as totalTrades,
                SUM(CASE WHEN positionStatus = '已清仓' THEN 1 ELSE 0 END) as clearedCount,
                SUM(CASE WHEN positionStatus = '已清仓' AND CAST(totalReturn AS REAL) >= 0 THEN 1 ELSE 0 END) as wins,
                SUM(CASE WHEN positionStatus = '已清仓' AND CAST(totalReturn AS REAL) < 0 THEN 1 ELSE 0 END) as losses,
                AVG(CASE WHEN positionStatus = '已清仓' THEN CAST(totalReturn AS REAL) ELSE NULL END) as avgReturn
            FROM trades {whereClause}", param);

        var clearedCount = (long)(baseStats?.clearedCount ?? 0);
        var wins = (long)(baseStats?.wins ?? 0);
        var winRate = clearedCount > 0 ? ((double)wins / clearedCount * 100).ToString("F1") : "0.0";
        var avgReturn = baseStats?.avgReturn != null ? ((double)baseStats.avgReturn).ToString("F2") : "0.00";

        var entryTypeRows = conn.Query($@"
            SELECT
                COALESCE(parentEntryType, entryType, '其他') as parentType,
                COALESCE(entryType, '其他') as entryType,
                COUNT(*) as count,
                SUM(CASE WHEN positionStatus = '已清仓' THEN 1 ELSE 0 END) as clearedCount,
                SUM(CASE WHEN positionStatus = '已清仓' AND CAST(totalReturn AS REAL) >= 0 THEN 1 ELSE 0 END) as wins,
                AVG(CASE WHEN positionStatus = '已清仓' THEN CAST(totalReturn AS REAL) ELSE NULL END) as avgReturn
            FROM trades {whereClause}
            GROUP BY parentType, entryType
            ORDER BY parentType, entryType", param);

        // 问题标签统计
        // 注意：whereClause 自带 WHERE，此处只能追加 AND，否则按年/月筛选时生成
        // "WHERE tradeDate LIKE @ym WHERE problemTags ..." 双 WHERE 语法错误（回归测试见 StatisticsSummaryTests）
        var tagFilter = conditions.Count > 0
            ? "AND problemTags IS NOT NULL AND problemTags != '' AND problemTags != '[]'"
            : "WHERE problemTags IS NOT NULL AND problemTags != '' AND problemTags != '[]'";
        var problemRows = conn.Query($"SELECT problemTags FROM trades {whereClause} {tagFilter}", param);
        var problemCount = new Dictionary<string, int>();
        var totalProblems = 0;
        foreach (var row in problemRows)
        {
            try
            {
                var tags = JsonSerializer.Deserialize<List<string>>((string)row.problemTags);
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        problemCount[tag] = problemCount.GetValueOrDefault(tag, 0) + 1;
                        totalProblems++;
                    }
                }
            }
            catch { }
        }

        var problemStats = problemCount
            .Select(kv => new { problem = kv.Key, count = kv.Value, percentage = totalProblems > 0 ? ((double)kv.Value / totalProblems * 100).ToString("F1") : "0.0" })
            .OrderByDescending(x => x.count)
            .ToList();

        return new
        {
            overview = new
            {
                totalTrades = (long)(baseStats?.totalTrades ?? 0),
                clearedCount,
                wins,
                losses = (long)(baseStats?.losses ?? 0),
                winRate,
                avgReturn
            },
            entryTypeStats = entryTypeRows,
            problemStats
        };
    }

    /// <summary>
    /// 按月份 x 进场类型统计胜率（近 N 个月）
    /// </summary>
    public List<object> GetMonthlyWinRateStats(int months = 6)
    {
        var tz = StockReview.Core.Services.CnTimeZone.Get;
        var nowSh = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var likeClauses = new List<string>();
        var param = new ExpandoObject() as IDictionary<string, object>;
        for (var i = months - 1; i >= 0; i--)
        {
            var d = nowSh.AddMonths(-i);
            var ym = $"{d:yyyy-MM}";
            likeClauses.Add("tradeDate LIKE @m" + i);
            param["m" + i] = ym + "%";
        }

        using var conn = CreateConnection();
        var rows = conn.Query($@"
            SELECT
                substr(tradeDate, 1, 7) as yearMonth,
                COALESCE(entryType, '其他') as entryType,
                COUNT(*) as total,
                SUM(CASE WHEN CAST(totalReturn AS REAL) > 0 THEN 1 ELSE 0 END) as wins
            FROM trades
            WHERE positionStatus = '已清仓'
                AND ({string.Join(" OR ", likeClauses)})
            GROUP BY yearMonth, entryType
            ORDER BY yearMonth, entryType", param);

        return rows.Select(r =>
        {
            var total = (long)r.total;
            var wins = (long)(r.wins ?? 0);
            return (object)new
            {
                yearMonth = (string?)r.yearMonth,
                entryType = (string?)r.entryType,
                total,
                wins,
                winRate = total > 0 ? ((double)wins / total * 100).ToString("F1") : "0"
            };
        }).ToList();
    }

    /// <summary>
    /// 按进场类型统计总胜率（含父级归类）
    /// </summary>
    public List<object> GetTypeWinRateStats()
    {
        using var conn = CreateConnection();
        var rows = conn.Query(@"
            SELECT
                COALESCE(parentEntryType, entryType, '其他') as parentType,
                COALESCE(entryType, '其他') as entryType,
                COUNT(*) as count,
                SUM(CASE WHEN positionStatus = '已清仓' AND CAST(totalReturn AS REAL) > 0 THEN 1 ELSE 0 END) as wins,
                AVG(CASE WHEN positionStatus = '已清仓' THEN CAST(totalReturn AS REAL) ELSE NULL END) as avgReturn
            FROM trades
            GROUP BY parentType, entryType
            ORDER BY parentType, entryType");
        return rows.Select(r =>
        {
            var count = (long)r.count;
            var wins = (long)(r.wins ?? 0);
            return (object)new
            {
                parentType = (string?)r.parentType,
                entryType = (string?)r.entryType,
                count,
                wins,
                winRate = count > 0 ? ((double)wins / count * 100).ToString("F1") : "0",
                avgReturn = r.avgReturn != null ? ((double)r.avgReturn).ToString("F2") : "0.00"
            };
        }).ToList();
    }

    /// <summary>
    /// 交易分布统计（按月 + 按状态）
    /// </summary>
    public object GetTradeDistribution()
    {
        using var conn = CreateConnection();
        var byMonth = conn.Query(@"
            SELECT
                substr(tradeDate, 1, 7) as yearMonth,
                COUNT(*) as total,
                SUM(CASE WHEN positionStatus = '已清仓' THEN 1 ELSE 0 END) as clearedCount,
                SUM(CASE WHEN positionStatus = '持仓中' THEN 1 ELSE 0 END) as holdCount
            FROM trades
            WHERE tradeDate IS NOT NULL AND tradeDate != ''
            GROUP BY yearMonth
            ORDER BY yearMonth DESC");

        var byStatus = conn.Query("SELECT positionStatus, COUNT(*) as count FROM trades GROUP BY positionStatus");

        return new
        {
            byMonth = byMonth.Select(r => new
            {
                yearMonth = (string?)r.yearMonth,
                total = (long)r.total,
                clearedCount = (long)(r.clearedCount ?? 0),
                holdCount = (long)(r.holdCount ?? 0)
            }),
            byStatus = byStatus.ToDictionary(r => (string?)r.positionStatus ?? "未知", r => (long)r.count)
        };
    }

    // ============ 导入导出 ============

    public object ExportAll()
    {
        var data = new Dictionary<string, object>
        {
            ["version"] = 23,
            ["exportDate"] = DateTime.UtcNow.ToString("o"),
            ["exportType"] = "sqlite"
        };
        foreach (var table in TableNames)
        {
            data[table] = GetAll(table);
        }
        return data;
    }

    public (int added, int updated, int replaced) ImportAll(Dictionary<string, object?> data)
    {
        var replaceTables = new HashSet<string> { "entryTypes", "problemTags", "todoTemplates" };
        var totalAdded = 0;
        var totalUpdated = 0;
        var totalReplaced = 0;

        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in TableNames)
            {
                if (!data.ContainsKey(table)) continue;
                var itemsRaw = data[table];
                if (itemsRaw is not JsonElement je || je.GetArrayLength() == 0) continue;

                var items = new List<Dictionary<string, object?>>();
                // 列过滤兜底：目标表不存在的列直接剔除，避免整行 INSERT/UPDATE 失败被吞
                // （旧版备份字段比 WPF schema 多时，曾在无迁移列的情况下整表导入失败）
                var tableCols = new HashSet<string>(GetTableColumns(conn, table), StringComparer.OrdinalIgnoreCase);
                foreach (var item in je.EnumerateArray())
                {
                    var d = JsonElementToDict(item);
                    if (d.Count == 0) continue;
                    var filtered = new Dictionary<string, object?>();
                    foreach (var kv in d)
                    {
                        if (tableCols.Contains(kv.Key)) filtered[kv.Key] = kv.Value;
                        else Log.Warning("[SQLite] 导入 {Table}：忽略未知列 {Col}", table, kv.Key);
                    }
                    if (filtered.Count > 0) items.Add(filtered);
                }

                if (replaceTables.Contains(table))
                {
                    conn.Execute($"DELETE FROM \"{table}\"", transaction: tx);
                    var replaced = 0;
                    foreach (var item in items)
                    {
                        var keys = item.Keys.ToList();
                        var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
                        // 命名参数：Dapper 把数组参数当作批量执行而非位置绑定，
                        // 旧的 VALUES(?) + ToArray() 写法从未真正插入过数据（异常被吞）
                        var ph = string.Join(", ", keys.Select(k => $"@{k}"));
                        // 替换模式下 INSERT 失败必须抛出回滚：DELETE 已清空原表，
                        // 吞异常会造成"导入成功"但表为空的数据丢失
                        conn.Execute($"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})", item, tx);
                        replaced++;
                    }
                    totalReplaced += replaced;
                    continue;
                }

                foreach (var item in items)
                {
                    try
                    {
                        object? existingId = null;
                        if (table == "appConfig" && item.ContainsKey("key"))
                        {
                            var existing = conn.QueryFirstOrDefault("SELECT key FROM appConfig WHERE key = @key", new { key = item["key"] }, tx);
                            existingId = existing?.key;
                        }
                        else if (item.ContainsKey("id") && item["id"] != null)
                        {
                            var existing = conn.QueryFirstOrDefault($"SELECT id FROM \"{table}\" WHERE id = @id", new { id = item["id"] }, tx);
                            existingId = existing?.id;
                        }

                        if (existingId != null)
                        {
                            var keys = item.Keys.Where(k => k != "id" && k != "key").ToList();
                            if (keys.Count > 0)
                            {
                                var setClause = string.Join(", ", keys.Select(k => $"\"{k}\" = @{k}"));
                                var whereCol = table == "appConfig" ? "key" : "id";
                                item["__id"] = existingId;
                                conn.Execute($"UPDATE \"{table}\" SET {setClause} WHERE \"{whereCol}\" = @__id", item, tx);
                                totalUpdated++;
                            }
                        }
                        else
                        {
                            var keys = item.Keys.ToList();
                            var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
                            var ph = string.Join(", ", keys.Select(k => $"@{k}"));
                            conn.Execute($"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})", item, tx);
                            totalAdded++;
                        }
                    }
                    catch (Exception ex) { Log.Error("[SQLite] 导入 {Table} 记录失败: {Msg}", table, ex.Message); }
                }
            }
            // 旧版备份的 entryTypes 以 notes 存储 +/- 加减分行，导入后拆分到独立列
            MigrateEntryTypeItemFields(conn);
            tx.Commit();
            Log.Information("[SQLite] 导入完成: 新增 {A} 条, 更新 {U} 条, 替换 {R} 条", totalAdded, totalUpdated, totalReplaced);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return (totalAdded, totalUpdated, totalReplaced);
    }

    // ============ 序列化/反序列化 ============

    private Dictionary<string, object?> SerializeRecord(IDictionary<string, object?> record)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in record)
        {
            if (ExcludeFieldPrefixes.Any(p => key.StartsWith(p))) continue;
            result[key] = value switch
            {
                null => null,
                bool b => b ? 1 : 0,
                Array a => JsonSerializer.Serialize(a),
                System.Collections.IList list => JsonSerializer.Serialize(list),
                System.Collections.IDictionary d => JsonSerializer.Serialize(d),
                DateTime dt => dt.ToString("o"),
                int or long or double or float or decimal or string => value,
                _ => value.ToString()
            };
        }
        return result;
    }

    private Dictionary<string, object?> DeserializeRecord(IDictionary<string, object> record)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in record)
        {
            if (value is long l && (l == 0 || l == 1) && key.StartsWith("is"))
            {
                result[key] = l == 1;
            }
            else if (ArrayFields.Contains(key) && value is string s)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<object?>>(s);
                    result[key] = parsed != null ? (object)parsed : s;
                }
                catch { result[key] = value; }
            }
            else if (ObjectFields.Contains(key) && value is string os)
            {
                try { result[key] = JsonSerializer.Deserialize<object>(os); }
                catch { result[key] = value; }
            }
            else
            {
                result[key] = value;
            }
        }
        return result;
    }

    // ============ 辅助方法 ============

    private void AssertTable(string table)
    {
        if (!TableSet.Contains(table))
            throw new ArgumentException($"Invalid table: {table}");
    }

    /// <summary>
    /// 列名 / 条件键会以标识符形式拼进 SQL（值均已参数化）。
    /// 此校验挡住外部来源（如 旧版备份导入、动态字典键）流入非法标识符的注入面。
    /// </summary>
    private static void AssertIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !IdentifierRegex.IsMatch(name))
            throw new ArgumentException($"Invalid identifier: {name}");
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierRegex =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private (string where, IDictionary<string, object> param) BuildWhereClause(IDictionary<string, object> conditions)
    {
        var clauses = new List<string>();
        var param = new ExpandoObject() as IDictionary<string, object>;
        foreach (var (key, value) in conditions)
        {
            AssertIdentifier(key);
            clauses.Add($"\"{key}\" = @{key}");
            param[key] = value;
        }
        return (string.Join(" AND ", clauses), param);
    }

    private List<string> GetTableColumns(SqliteConnection conn, string table)
    {
        // PRAGMA table_info 返回 (cid, name, type, notnull, dflt_value, pk) 多列，
        // 必须显式取 name 列；Query<string> 会错误地取第一列 cid（数字）
        var rows = conn.Query<dynamic>($"PRAGMA table_info(\"{table}\")");
        return rows.Select(r => (string)r.name).ToList();
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.False => 0,
                JsonValueKind.True => 1,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : (object)prop.Value.GetDouble(),
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Array => prop.Value.GetRawText(),
                JsonValueKind.Object => prop.Value.GetRawText(),
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }

    /// <summary>
    /// 通用 Dapper 查询（供外部服务调用）
    /// </summary>
    public List<T> Query<T>(string sql, object? param = null)
    {
        using var conn = CreateConnection();
        // Dapper 对 Dictionary<string, object?> 走"无参构造"路径，返回全空字典，
        // 必须特判走动态行 → 字典（与 GetAll 同一转换，含 JSON 字段反序列化）
        if (typeof(T) == typeof(Dictionary<string, object?>))
        {
            var dyn = (IEnumerable<dynamic>)conn.Query(sql, param);
            var list = new List<T>();
            foreach (var row in dyn)
                list.Add((T)(object)DeserializeRecord((IDictionary<string, object>)row));
            return list;
        }
        return conn.Query<T>(sql, param).ToList();
    }

    public T? QueryFirstOrDefault<T>(string sql, object? param = null)
    {
        using var conn = CreateConnection();
        return conn.QueryFirstOrDefault<T>(sql, param);
    }

    public int Execute(string sql, object? param = null)
    {
        using var conn = CreateConnection();
        return conn.Execute(sql, param);
    }

    // ============ .db 快照备份 / 恢复 ============

    private const int KeepSnapshotCount = 14;

    /// <summary>
    /// 备份数据库（SQLite 在线备份 API，含全部表与已提交数据），完成后校验并清理超出保留数的旧快照
    /// </summary>
    public string Backup(string? suffix = null)
    {
        var backupDir = Path.Combine(GetDataDir(), "backups");
        Directory.CreateDirectory(backupDir);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get);
        var fileName = $"data_backup_{now:yyyyMMdd_HHmmss}{suffix ?? ""}.db";
        var backupPath = Path.Combine(backupDir, fileName);
        using (var source = CreateConnection())
        using (var dest = new SqliteConnection($"Data Source={backupPath}"))
        {
            dest.Open();
            source.BackupDatabase(dest);
        }
        ValidateDbFile(backupPath);
        CleanupOldSnapshots();
        Log.Information("[SQLite] 备份完成: {Path}", backupPath);
        return backupPath;
    }

    /// <summary>
    /// 校验 .db 文件可用：quick_check 通过 + 业务表齐全
    /// </summary>
    public void ValidateDbFile(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        var check = conn.ExecuteScalar<string>("PRAGMA quick_check;");
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"数据库完整性校验失败: {check}");
        var tables = new HashSet<string>(conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'"));
        var missing = TableNames.Where(t => !tables.Contains(t)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"数据库缺少业务表: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// 从 .db 快照整库恢复：先自动留存当前数据为 -pre-restore 快照，再经 SQLite 备份 API 覆盖活动库（无需文件交换，天然受写锁保护）
    /// </summary>
    public string RestoreFromSnapshot(string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("快照文件不存在", snapshotPath);
        ValidateDbFile(snapshotPath);

        var safetyPath = Backup("-pre-restore");

        using var snap = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
        snap.Open();
        using var live = CreateConnection();
        using (var cmd = live.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        snap.BackupDatabase(live);
        Log.Information("[SQLite] 已从快照恢复: {Snapshot}，原数据留存于 {Safety}", snapshotPath, safetyPath);
        return safetyPath;
    }

    private void CleanupOldSnapshots()
    {
        var dir = Path.Combine(GetDataDir(), "backups");
        if (!Directory.Exists(dir)) return;
        var outdated = Directory.GetFiles(dir, "data_backup_*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(KeepSnapshotCount);
        foreach (var file in outdated)
        {
            try
            {
                File.Delete(file);
                Log.Information("[SQLite] 清理旧快照: {File}", file);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SQLite] 清理旧快照失败: {File}", file);
            }
        }
    }
}
