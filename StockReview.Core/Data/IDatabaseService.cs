// DatabaseService 接口（2026-09-02 优化报告 A1）。
// 目的：消费方（服务/ViewModel）依赖接口而非具体类，为 mock 与单测铺路。
// CreateConnection 已随 P5 收尾降为 internal：跨程序集唯一消费方（WebBridge orderBy
// 变体）已下沉为 OrderByRawRows，自由 SQL 需求走 Query/Execute。
namespace StockReview.Core.Data;

/// <summary>SQLite 数据访问服务接口。实现：<see cref="DatabaseService"/>。</summary>
public interface IDatabaseService
{
    void SetDataDir(string dataDir);

    void Initialize();

    // ===== 通用表操作（表名走白名单校验） =====
    List<Dictionary<string, object?>> GetAll(string table);
    Dictionary<string, object?>? GetById(string table, object id);
    object Add(string table, IDictionary<string, object?> data);
    bool Update(string table, object id, IDictionary<string, object?> data);
    bool Delete(string table, object id);
    object Put(string table, IDictionary<string, object?> data);
    void BulkPut(string table, IEnumerable<IDictionary<string, object?>> items);
    void BulkAdd(string table, IEnumerable<IDictionary<string, object?>> items);
    void Clear(string table);
    long Count(string table);
    void DeleteDatabase();

    // ===== 条件查询 =====
    List<Dictionary<string, object?>> WhereEquals(string table, string field, object value);
    List<Dictionary<string, object?>> WhereStartsWith(string table, string field, string value);
    List<Dictionary<string, object?>> WhereAnyOf(string table, string field, IEnumerable<object> values);
    Dictionary<string, object?>? WhereCompoundFirst(string table, IDictionary<string, object> conditions);
    List<Dictionary<string, object?>> WhereCompound(string table, IDictionary<string, object> conditions);
    List<Dictionary<string, object?>> WhereBetween(string table, string field, object lower, object upper);
    Dictionary<string, object?>? WhereFirst(string table, string field, object value);
    List<Dictionary<string, object?>> GetPage(string table, int limit = 100, int offset = 0,
        string? orderField = null, string orderDir = "ASC", string? where = null, object? whereValue = null);
    List<Dictionary<string, object?>> OrderByLimit(string table, string field, int limit, bool reverse = false);

    /// <summary>ORDER BY 原始行查询（WebBridge orderBy 变体专用，不做 DeserializeRecord 值转换）</summary>
    List<IDictionary<string, object>> OrderByRawRows(string table, string field, string dir, int? limit);

    // ===== 领域查询 =====
    (List<Dictionary<string, object?>> data, long total) QueryCasesPaginated(
        string caseType = "all", string entryType = "", List<string>? entryTypes = null,
        string keyword = "", string sortBy = "date_desc", int page = 1, int pageSize = 30);
    Dictionary<string, long> GetCaseTypeCounts();
    List<Dictionary<string, object?>> GetDailySummariesInRange(string startDate, string endDate, string summaryType);
    List<Dictionary<string, object?>> GetActiveEntryTypes();
    List<Dictionary<string, object?>> GetActiveProblemTags();
    List<Dictionary<string, object?>> GetTradesByYearPrefix(string yearPrefix);
    List<Dictionary<string, object?>> GetStrongStocksByYearPrefix(string yearPrefix);
    object GetStatisticsSummary(string? yearMonth = null, string? year = null);
    List<object> GetMonthlyWinRateStats(int months = 6);
    List<object> GetTypeWinRateStats();
    object GetTradeDistribution();

    // ===== 导入导出 / 备份恢复 =====
    object ExportAll();
    (int added, int updated, int replaced) ImportAll(Dictionary<string, object?> data);
    string Backup(string? suffix = null);
    void ValidateDbFile(string path);
    string RestoreFromSnapshot(string snapshotPath);

    // ===== 自由 SQL（标识符经 AssertIdentifier 校验，值走参数化） =====
    List<T> Query<T>(string sql, object? param = null);
    T? QueryFirstOrDefault<T>(string sql, object? param = null);
    int Execute(string sql, object? param = null);
    int ExecuteBatch(string sql, IEnumerable<object> paramsList);
}
