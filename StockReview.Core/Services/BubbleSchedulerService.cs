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
/// 气泡调度服务 - 对应 Electron 版 bubbleSchedulerStore.js
/// 集中式气泡显示调度器：优先级队列 + 租约选举 + 去重 + 持久化
/// 持久化到 appConfig 表（对应 localStorage 的 pet_bubble_queue_v2 键）
/// </summary>
public class BubbleSchedulerService
{
    private readonly DatabaseService _db;
    private const string QueueKey = "pet_bubble_queue_v2";

    // 租约 TTL 8 秒
    private const long LeaseTtlMs = 8 * 1000;
    // 租约续约间隔 4 秒
    private const long LeaseRenewIntervalMs = 4 * 1000;
    // 持久气泡最长滞留时间：超时自动过期，防止无人点击的 Persistent 气泡永久堵死队列
    private const long PersistentBubbleMaxAgeMs = 30 * 60 * 1000;
    // 队列项最长滞留时间：加载持久化状态时丢弃更旧的项（重启后旧提醒无展示价值）
    private const long QueueItemMaxAgeMs = 10 * 60 * 1000;
    // 出队时丢弃阈值：行情类提醒入队超过该时长后已无展示价值（价格早已变化），
    // 弹出只会让用户看到"过时价格"，被误判为监控延迟
    private const long DequeueStaleDropMs = 120 * 1000;

    // 队列状态
    private BubbleQueueState _state = new();
    // 租约持有时间戳
    private long _leaseClaimedAt;
    // 锁
    private readonly object _lock = new();

    // 定时器
    private Timer? _displayTimer;

    public BubbleQueueItem? CurrentBubble { get; private set; }
    public bool IsStarted { get; private set; }

    /// <summary>Tick 结果回调：show/hide/idle，由 SchedulerPetStore 订阅转发到宠物窗口</summary>
    public event Action<TickResult>? OnTick;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null
    };

    public BubbleSchedulerService(DatabaseService db)
    {
        _db = db;
        LoadFromStorage();
    }

    // ============ 启动/停止 ============

    /// <summary>
    /// 启动气泡调度循环（对应 bubbleSchedulerStore.js 的 setInterval tick）。
    /// 每 500ms 调用 Tick() 检查当前气泡过期/取队列下一项。
    /// </summary>
    public void Start()
    {
        if (IsStarted) return;
        IsStarted = true;
        _displayTimer = new Timer(_ => SafeTick(), null, 500, 500);
        Log.Information("[BubbleScheduler] 调度循环已启动 (500ms tick)");
    }

    /// <summary>停止调度循环</summary>
    public void Stop()
    {
        IsStarted = false;
        _displayTimer?.Dispose();
        _displayTimer = null;
        Log.Information("[BubbleScheduler] 调度循环已停止");
    }

    private void SafeTick()
    {
        try { var result = Tick(); OnTick?.Invoke(result); }
        catch (Exception ex) { Log.Warning(ex, "[BubbleScheduler] tick 异常"); }
    }

    // ============ 持久化 ============

    private void LoadFromStorage()
    {
        try
        {
            var row = _db.GetById("appConfig", QueueKey);
            if (row != null && row.TryGetValue("value", out var val) && val != null)
            {
                _state = JsonSerializer.Deserialize<BubbleQueueState>(val.ToString()!, JsonOpts) ?? new();
                // 防御同源数据损坏：备份导入的 JSON 数组可能混入 null 元素
                //（实测 pet_reminder_history 存在 "},null]"，同库同来源），null 项会炸后续处理
                if (_state.Queue != null)
                {
                    var removed = _state.Queue.RemoveAll(q => q == null);
                    if (removed > 0)
                    {
                        Log.Warning("[BubbleScheduler] 队列数据含 {Count} 个 null 元素，已剔除", removed);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[BubbleScheduler] 加载失败");
            _state = new();
        }

        // 重启恢复：持久化的 Current 是上一会话遗留（气泡早已不可见），
        // 若为等待用户点击的 Persistent 项会永久堵死队列 → 直接丢弃；
        // 队列中超过 MaxAge 的陈旧项一并清理
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_state.Current != null)
        {
            Log.Information("[BubbleScheduler] 丢弃上一会话遗留 Current: {Title}", _state.Current.Title);
            _state.Current = null;
        }
        if (_state.Queue != null && _state.Queue.Count > 0)
        {
            var stale = _state.Queue.RemoveAll(q => now - (q.Timestamp ?? 0) > QueueItemMaxAgeMs);
            if (stale > 0)
            {
                Log.Information("[BubbleScheduler] 清理 {Count} 条过期队列项", stale);
                SaveToStorage();
            }
        }
    }

    private void SaveToStorage()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = QueueKey, ["value"] = json });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[BubbleScheduler] 保存失败");
        }
    }

    // ============ 租约管理 ============

    /// <summary>
    /// 尝试抢租约
    /// </summary>
    public bool ClaimDisplay()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 无租约或租约已过期 → 抢占
            if (_leaseClaimedAt == 0 || now - _leaseClaimedAt > LeaseTtlMs)
            {
                _leaseClaimedAt = now;
                Log.Information("[BubbleScheduler] 已获取气泡显示租约");
                return true;
            }
            // 自己已持有租约 → 续约
            if (now - _leaseClaimedAt < LeaseTtlMs)
            {
                _leaseClaimedAt = now;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 仅做去重判断（不改动队列状态）：同标题+同类型 10 秒内是否已存在。
    /// 供展示流水线在真正入队前作为去重闸门。
    /// </summary>
    public bool IsDuplicateRecently(string title, string type)
    {
        if (string.IsNullOrEmpty(title)) return false;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_state.Current != null && !_state.Current.Persistent &&
                _state.Current.Title == title && _state.Current.Type == type)
                return true;
            return _state.Queue?.Any(q =>
                q.Title == title &&
                q.Type == type &&
                now - (q.Timestamp ?? 0) < 60000) == true;
        }
    }

    /// <summary>
    /// 释放租约
    /// </summary>
    public void ReleaseDisplay()
    {
        lock (_lock)
        {
            _leaseClaimedAt = 0;
            Log.Information("[BubbleScheduler] 已释放气泡显示租约");
        }
    }

    /// <summary>
    /// 检查是否持有租约
    /// </summary>
    public bool HasLease()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _leaseClaimedAt > 0 && now - _leaseClaimedAt < LeaseTtlMs;
        }
    }

    // ============ 入队 ============

    /// <summary>
    /// 入队气泡项
    /// </summary>
    public bool Enqueue(BubbleQueueItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Title)) return false;
        item.Id ??= Guid.NewGuid().ToString();
        item.Timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_lock)
        {
            _state.Queue ??= new();

            // 去重：同标题 + 同类型在 60 秒内不重复入队（对齐 Electron DEDUPE_WINDOW_MS）
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var dup = _state.Queue.FirstOrDefault(q =>
                q.Title == item.Title &&
                q.Type == item.Type &&
                now - (q.Timestamp ?? 0) < 60000);
            if (dup != null)
            {
                Log.Debug("[BubbleScheduler] 去重拦截: {Title}", item.Title);
                return false;
            }

            // 按优先级插入（importance 降序 → timestamp 升序）
            int insertIdx = _state.Queue.Count;
            for (int i = 0; i < _state.Queue.Count; i++)
            {
                if ((item.Importance ?? 3) > (_state.Queue[i].Importance ?? 3))
                {
                    insertIdx = i;
                    break;
                }
            }
            _state.Queue.Insert(insertIdx, item);
            SaveToStorage();
            Log.Information("[BubbleScheduler] 入队: {Title} (importance={Importance}, queue={Count})",
                item.Title, item.Importance ?? 3, _state.Queue.Count);
            return true;
        }
    }

    // ============ Tick（取下一个/检查过期） ============

    /// <summary>
    /// 执行一次调度 tick
    /// </summary>
    public TickResult Tick()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 检查当前气泡是否过期
            if (_state.Current != null)
            {
                var current = _state.Current;
                bool expired = false;

                // persistent 项 / 带动作按钮的项不过期（等用户 ack：动作气泡需操作后才能消失）
                var hasActions = current.Actions is { Count: > 0 };
                if (!current.Persistent && !hasActions && current.DurationMs > 0)
                {
                    if (now - current.StartedAt >= current.DurationMs)
                        expired = true;
                }

                // 安全阀：等待用户操作的气泡超 30 分钟仍未处理则放行队列，防止永久堵死
                if ((current.Persistent || hasActions) &&
                    now - current.StartedAt >= PersistentBubbleMaxAgeMs)
                    expired = true;

                if (expired)
                {
                    _state.Current = null;
                    SaveToStorage();
                    CurrentBubble = null;
                    return new TickResult { Action = "hide", Current = null, NewItem = null };
                }

                // 当前气泡仍在显示
                return new TickResult { Action = "idle", Current = current, NewItem = null };
            }

            // 无当前气泡 → 取队列首个（跳过陈旧项：入队超 2 分钟的行情提醒已无展示价值）
            if (_state.Queue != null && _state.Queue.Count > 0)
            {
                int dropped = 0;
                while (_state.Queue.Count > 0)
                {
                    var age = now - (_state.Queue[0].Timestamp ?? now);
                    if (age > DequeueStaleDropMs && _state.Queue[0].Persistent != true)
                    {
                        _state.Queue.RemoveAt(0);
                        dropped++;
                        continue;
                    }
                    break;
                }
                if (dropped > 0)
                {
                    Log.Information("[BubbleScheduler] 丢弃 {Count} 条过时队列项（入队超 {Sec}s，行情已变化）",
                        dropped, DequeueStaleDropMs / 1000L);
                    SaveToStorage();
                }

                if (_state.Queue.Count > 0)
                {
                    var next = _state.Queue[0];
                    _state.Queue.RemoveAt(0);
                    next.StartedAt = now;

                    // 积压加速：队列越深显示时长越短（对齐 Electron 三槽位并行消费能力；
                    // 单槽位 12s/条 消费不过来时，堆积的气泡弹出全是过时价格）
                    if (!next.Persistent && next.DurationMs > 0)
                    {
                        var backlog = _state.Queue.Count;
                        if (backlog > 5)
                        {
                            // 12s→4s（轻度积压）；>15 条 → 3s 极速消化
                            long accelerated = backlog > 15 ? 3000L : Math.Max(3000L, (next.DurationMs ?? 0) / 3);
                            if (accelerated < (next.DurationMs ?? 0)) next.DurationMs = accelerated;
                        }
                    }

                    _state.Current = next;
                    SaveToStorage();
                    CurrentBubble = next;
                    return new TickResult { Action = "show", Current = next, NewItem = next };
                }
            }

            // 空闲
            CurrentBubble = null;
            return new TickResult { Action = "idle", Current = null, NewItem = null };
        }
    }

    /// <summary>
    /// 确认当前气泡已处理
    /// </summary>
    public void AckCurrent(string reason = "ack")
    {
        lock (_lock)
        {
            if (_state.Current != null)
            {
                Log.Debug("[BubbleScheduler] ackCurrent: {Title} ({Reason})",
                    _state.Current.Title, reason);
                _state.Current = null;
                SaveToStorage();
                CurrentBubble = null;
            }
        }
    }

    /// <summary>
    /// 取消队列中指定 ID 的项
    /// </summary>
    public void CancelById(string id)
    {
        lock (_lock)
        {
            if (_state.Queue != null)
            {
                var removed = _state.Queue.RemoveAll(q => q.Id == id);
                if (removed > 0)
                {
                    SaveToStorage();
                    Log.Debug("[BubbleScheduler] 取消队列项: {Id}", id);
                }
            }
            if (_state.Current?.Id == id)
            {
                _state.Current = null;
                SaveToStorage();
                CurrentBubble = null;
                Log.Debug("[BubbleScheduler] 取消当前气泡: {Id}", id);
            }
        }
    }

    /// <summary>
    /// 清空队列
    /// </summary>
    public void ClearQueue()
    {
        lock (_lock)
        {
            _state.Queue?.Clear();
            _state.Current = null;
            SaveToStorage();
            CurrentBubble = null;
        }
    }

    /// <summary>
    /// 从存储重载
    /// </summary>
    public void ReloadFromStorage()
    {
        lock (_lock)
        {
            LoadFromStorage();
            CurrentBubble = _state.Current;
        }
    }

    /// <summary>
    /// 获取队列状态
    /// </summary>
    public BubbleQueueState GetState()
    {
        lock (_lock) { return _state; }
    }
}

// ============ 数据模型 ============

public class BubbleQueueState
{
    public List<BubbleQueueItem>? Queue { get; set; } = new();
    public BubbleQueueItem? Current { get; set; }
}

public class BubbleQueueItem
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public string? Level { get; set; }
    public int? Importance { get; set; }
    public List<BubbleAction>? Actions { get; set; }
    public bool Persistent { get; set; }
    public long? DurationMs { get; set; }
    public long? Timestamp { get; set; }
    public long StartedAt { get; set; }
    public string? Type { get; set; }
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
}

public class BubbleAction
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>关联交易计划 ID 列表（收盘提醒批量操作，对齐 Electron action.planIds）</summary>
    public List<string>? PlanIds { get; set; }
    /// <summary>自定义提醒原始 ID（对齐 Electron action.reminderId）</summary>
    public string? ReminderId { get; set; }
}

public class TickResult
{
    public string Action { get; set; } = "idle"; // "show" | "hide" | "idle"
    public BubbleQueueItem? Current { get; set; }
    public BubbleQueueItem? NewItem { get; set; }
}
