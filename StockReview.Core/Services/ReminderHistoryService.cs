using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 提醒历史服务
/// 记录所有触发过的提醒，保留最近 3 天，超期自动清除
/// </summary>
public class ReminderHistoryService
{
    private readonly IDatabaseService _db;
    private const string StorageKey = "pet_reminder_history";
    private const int RetentionDays = 3;

    // 兼容旧版备份的 camelCase 字段与 WPF 自身的 PascalCase 字段
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private List<ReminderHistoryRecord> _history = new();

    // ---- 落盘节流（写放大防护）----
    // 每次提醒触发 AddRecord 都全量序列化 + UPDATE appConfig，盘中高频提醒下与
    // SignalEventService/FlushSnapshotsAsync 争抢 SQLite 唯一写锁。改为 2s 节流合批，
    // 脏数据由定时器兜底 flush，进程退出最多丢最近 2s 历史（可接受）。
    // _saveLock 同时保护 _history 的变更与序列化枚举：flush 定时器在线程池上枚举
    // _history 时，若 AddRecord（推送/定时器线程）并发 Insert，序列化会抛
    // "Collection was modified"（lock 可重入，SaveToStorage 内嵌套获取安全）。
    private const int SaveThrottleMs = 2000;
    private readonly object _saveLock = new();
    private bool _dirty;
    private long _lastSaveMsUtc;
    private readonly Timer _flushTimer;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ============ 标签常量 ============

    public static readonly Dictionary<string, string> ReminderTypeLabels = new()
    {
        ["price_alert"] = "价格提醒",
        ["stop_loss"] = "止损提醒",
        ["target_price"] = "目标价提醒",
        ["limit_move"] = "涨跌停提醒",
        ["sell_point"] = "卖点信号",
        ["signal"] = "信号提醒",
        ["insight"] = "心得提醒",
        ["trade"] = "交易提醒",
        ["after_market"] = "收盘提醒",
        ["after_market_review"] = "盘后复盘",
        ["market_digest"] = "休市摘要",
        ["custom_reminder"] = "自定义提醒",
        ["combined_signals"] = "多信号提醒",
        ["surge"] = "快速拉升",
        ["plunge"] = "快速下跌"
    };

    public static readonly Dictionary<string, string> ReminderLevelLabels = new()
    {
        ["hint"] = "提示",
        ["alert"] = "警告",
        ["critical"] = "严重",
        ["warning"] = "警戒",
        ["force"] = "强制"
    };

    public ReminderHistoryService(IDatabaseService db)
    {
        _db = db;
        _flushTimer = new Timer(_ => FlushPendingSave(), null, SaveThrottleMs, SaveThrottleMs);
        // 历史数据损坏（如备份导入的 JSON 含 null 元素）绝不能阻塞应用启动
        try
        {
            LoadFromStorage();
            CleanupOldEntries();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ReminderHistory] 初始化失败，重置为空历史");
            _history = new();
        }
    }

    // ============ 持久化 ============

    private void LoadFromStorage()
    {
        try
        {
            var row = _db.GetById("appConfig", StorageKey);
            if (row != null && row.TryGetValue("value", out var val) && val != null)
            {
                var json = val.ToString();
                _history = JsonSerializer.Deserialize<List<ReminderHistoryRecord>>(json!, JsonOpts) ?? new();
                // 备份导入的 JSON 数组可能混入 null 元素（实测发现 "},null]" 结尾），
                // 反序列化会得到含 null 的列表 → CleanupOldEntries 谓词 NRE → 启动崩溃
                _history = _history.Where(h => h != null).ToList();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ReminderHistory] 加载提醒历史失败");
            _history = new();
        }
    }

    private void SaveToStorage()
    {
        // 节流入口：标记脏 + 限频落盘；真正的序列化/写库在 DoFlushNow
        lock (_saveLock)
        {
            _dirty = true;
            if (NowMs - _lastSaveMsUtc < SaveThrottleMs) return;
            DoFlushNow();
        }
    }

    /// <summary>定时器兜底：节流窗口内被合掉的写入在此最终落盘</summary>
    private void FlushPendingSave()
    {
        try
        {
            lock (_saveLock)
            {
                if (!_dirty) return;
                DoFlushNow();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ReminderHistory] 兜底落盘失败");
        }
    }

    /// <summary>调用方须持有 _saveLock。序列化抛异常时 _dirty 保持 true，下个周期重试</summary>
    private void DoFlushNow()
    {
        try
        {
            var json = JsonSerializer.Serialize(_history);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = StorageKey,
                ["value"] = json
            });
            _dirty = false;
            _lastSaveMsUtc = NowMs;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ReminderHistory] 保存提醒历史失败");
        }
    }

    // ============ 方法 ============

    /// <summary>
    /// 启动时自动清理过期记录
    /// </summary>
    public void CleanupOldEntries()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RetentionDays * 24L * 60 * 60 * 1000;
        lock (_saveLock)
        {
            var before = _history?.Count ?? 0;
            // null 安全：旧数据/外部导入可能混入 null 元素（构造器兜底之外的二次防线）
            _history = (_history ?? new List<ReminderHistoryRecord>())
                .Where(h => h != null && h.Timestamp >= cutoff)
                .ToList()!;
            if (_history.Count != before)
                SaveToStorage();
        }
    }

    /// <summary>
    /// 添加提醒历史记录
    /// </summary>
    public ReminderHistoryRecord AddRecord(ReminderSnapshot reminder)
    {
        var record = new ReminderHistoryRecord
        {
            Id = Guid.NewGuid().ToString(),
            ReminderId = reminder.Id,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateStr = TradePlanService.FormatLocalDate(DateTime.UtcNow),
            Type = reminder.Type ?? "unknown",
            Level = reminder.Level ?? "hint",
            Importance = reminder.Importance ?? 0,
            Title = reminder.Title ?? "",
            Content = reminder.Content ?? "",
            StockCode = reminder.StockCode,
            StockName = reminder.StockName,
            UserResponse = reminder.UserResponse,
            ResponseTime = reminder.ResponseTime
        };
        lock (_saveLock)
        {
            _history.Insert(0, record);
            SaveToStorage();
        }
        return record;
    }

    /// <summary>
    /// 更新指定提醒的用户响应
    /// </summary>
    public ReminderHistoryRecord? UpdateRecordResponse(string? reminderId, string userResponse)
    {
        lock (_saveLock)
        {
            var record = _history.FirstOrDefault(h => h.ReminderId == reminderId);
            if (record != null)
            {
                record.UserResponse = userResponse;
                record.ResponseTime = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - record.Timestamp) / 1000);
                SaveToStorage();
                return record;
            }
            return null;
        }
    }

    /// <summary>
    /// 清空全部
    /// </summary>
    public void ClearAll()
    {
        lock (_saveLock)
        {
            _history.Clear();
            SaveToStorage();
        }
    }

    /// <summary>
    /// 从存储重新加载（跨窗口同步）
    /// </summary>
    public void ReloadFromStorage()
    {
        lock (_saveLock)
        {
            LoadFromStorage();
        }
    }

    // ============ 计算属性 ============
    // 读取同样持锁取快照：flush 定时器与变更线程并发时，无锁枚举可能抛
    // "Collection was modified"；History 返回副本避免调用方延迟枚举撞上后续 Insert。

    public int TotalCount
    {
        get { lock (_saveLock) return _history.Count; }
    }

    public int TodayCount
    {
        get
        {
            var today = TradePlanService.FormatLocalDate(DateTime.UtcNow);
            lock (_saveLock) return _history.Count(h => h != null && h.DateStr == today);
        }
    }

    public int UnrespondedCount
    {
        get { lock (_saveLock) return _history.Count(h => h != null && string.IsNullOrEmpty(h.UserResponse)); }
    }

    public int RespondedCount
    {
        get { lock (_saveLock) return _history.Count(h => h != null && !string.IsNullOrEmpty(h.UserResponse)); }
    }

    public IReadOnlyList<ReminderHistoryRecord> History
    {
        get { lock (_saveLock) return _history.ToList().AsReadOnly(); }
    }
}

// ============ 数据模型 ============

public class ReminderHistoryRecord
{
    public string Id { get; set; } = "";
    public string? ReminderId { get; set; }
    public long Timestamp { get; set; }
    public string DateStr { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public string Level { get; set; } = "hint";
    public int Importance { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public string? UserResponse { get; set; }
    public int? ResponseTime { get; set; }
}

/// <summary>
/// 提醒快照（用于记录历史）
/// </summary>
public class ReminderSnapshot
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Level { get; set; }
    public int? Importance { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public string? UserResponse { get; set; }
    public int? ResponseTime { get; set; }
}
