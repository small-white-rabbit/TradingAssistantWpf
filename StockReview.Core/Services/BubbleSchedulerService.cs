using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 气泡调度服务 - 对应 Electron 版 bubbleSchedulerStore.js（多区域 v4）
/// 三槽位并行显示（上/左/右）+ 优先级排序 + 持久项抢占 + 三重去重 + 持久化。
/// 队列密集时三条提醒并行展示，高优先级持久项可抢占最低优先级槽位，
/// 解决单槽位串行消费导致的高优先级提醒延迟问题（问题②）。
/// 持久化到 appConfig 表（键 pet_bubble_queue_v2，兼容旧单槽格式迁移）。
/// </summary>
public class BubbleSchedulerService
{
    private readonly DatabaseService _db;
    private const string QueueKey = "pet_bubble_queue_v2";

    // 三个显示槽位（对齐 Electron SLOT_NAMES）
    private static readonly string[] SlotNames = { "top", "left", "right" };

    // 自定义提醒优先级加成（对齐 Electron CUSTOM_PRIORITY_BOOST：用户手动设置的提醒最优先）
    private const int CustomPriorityBoost = 1000;

    // 去重窗口（对齐 Electron DEDUPE_WINDOW_MS）
    private const long DedupeWindowMs = 60 * 1000;

    // 持久气泡（无动作按钮）最长滞留：5 分钟（对齐 Electron PERSISTENT_MAX_LIFE_MS）
    private const long PersistentMaxLifeMs = 5 * 60 * 1000;

    // 持久气泡（带动作按钮）最长滞留：30 分钟绝对上限。
    // 无上限时槽位会被永久占据（用户不点按钮），后续气泡永远排队不显示
    private const long PersistentActionMaxLifeMs = 30 * 60 * 1000;

    // 队列项最长滞留时间：加载持久化状态时丢弃更旧的项（重启后旧提醒无展示价值）
    private const long QueueItemMaxAgeMs = 10 * 60 * 1000;

    // 出队时丢弃阈值：行情类提醒入队超过该时长后已无展示价值（价格早已变化），
    // 弹出只会让用户看到"过时价格"，被误判为监控延迟
    private const long DequeueStaleDropMs = 120 * 1000;

    private BubbleQueueState _state = new();
    private readonly object _lock = new();
    private Timer? _displayTimer;

    public bool IsStarted { get; private set; }

    /// <summary>Tick 结果回调：逐槽位 show/hide 事件（仅在状态变化时触发），由 SchedulerPetStore 订阅转发到宠物窗口</summary>
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
    /// 每 500ms 调用 Tick() 检查各槽位过期/填充空槽位/执行抢占。
    /// </summary>
    public void Start()
    {
        if (IsStarted) return;
        IsStarted = true;
        _displayTimer = new Timer(_ => SafeTick(), null, 500, 500);
        Log.Information("[BubbleScheduler] 调度循环已启动 (500ms tick, 三槽位 top/left/right)");
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
        try
        {
            var result = Tick();
            if (result.Events is { Count: > 0 })
                OnTick?.Invoke(result);
        }
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
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[BubbleScheduler] 加载失败");
            _state = new();
        }

        var now = Now();
        var dirty = false;

        // 防御同源数据损坏：备份导入的 JSON 数组可能混入 null 元素
        //（实测 pet_reminder_history 存在 "},null]"，同库同来源），null 项会炸后续处理
        if (_state.Queue != null)
        {
            var removedNull = _state.Queue.RemoveAll(q => q == null);
            if (removedNull > 0)
            {
                Log.Warning("[BubbleScheduler] 队列数据含 {Count} 个 null 元素，已剔除", removedNull);
                dirty = true;
            }
        }
        if (_state.RecentShown != null)
        {
            var removedRecent = _state.RecentShown.RemoveAll(r => r == null);
            if (removedRecent > 0)
            {
                Log.Warning("[BubbleScheduler] recentShown 含 {Count} 个 null 元素，已剔除", removedRecent);
                dirty = true;
            }
        }

        // 旧格式（v2 单槽 Current）迁移 + 新格式槽位恢复：
        // 重启后 PetWindow 不恢复气泡状态，保留槽位会形成「调度器认为已显示、UI 实际为空」
        // 的幽灵槽位（占位直到过期）→ 一律丢弃，等 tick 从队列重新填充
        if (_state.Current != null)
        {
            Log.Information("[BubbleScheduler] 迁移：丢弃上一会话遗留 Current: {Title}", _state.Current.Title);
            _state.Current = null;
            dirty = true;
        }
        foreach (var s in SlotNames)
        {
            var slotItem = _state.Slots?.GetValueOrDefault(s)?.Item;
            if (slotItem != null)
            {
                Log.Information("[BubbleScheduler] 丢弃上一会话遗留槽位 {Slot}: {Title}", s, slotItem.Title);
                dirty = true;
            }
        }
        _state.Slots = NewSlots();

        // 队列陈旧项清理（超 10 分钟的遗留项无展示价值）
        if (_state.Queue is { Count: > 0 })
        {
            var stale = _state.Queue.RemoveAll(q => now - (q?.Timestamp ?? 0) > QueueItemMaxAgeMs);
            if (stale > 0)
            {
                Log.Information("[BubbleScheduler] 清理 {Count} 条过期队列项", stale);
                dirty = true;
            }
        }

        // 旧数据补齐 DedupeKey / EnqueuedAt（v2 格式无这两个字段）
        if (_state.Queue != null)
        {
            foreach (var q in _state.Queue)
            {
                if (q == null) continue;
                EnsureDedupeKey(q, now);
                if (q.EnqueuedAt == 0) q.EnqueuedAt = q.Timestamp ?? now;
            }
        }

        // recentShown 过期清理
        _state.RecentShown?.RemoveAll(r => now - r.Ts >= DedupeWindowMs);

        if (dirty) SaveToStorage();
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

    // ============ 入队 ============

    /// <summary>
    /// 入队气泡项。三重去重（对齐 Electron）：recentShown 60s 窗口 / 正在任意槽位显示 / 已在队列中；
    /// 另保留 WPF 原有的 标题+类型 去重（提醒源 Id 语义不一，防同标题轰炸）。
    /// 入队后按优先级排序（custom_reminder +1000，其余按 importance）。
    /// </summary>
    public bool Enqueue(BubbleQueueItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Title)) return false;
        var now = Now();
        item.Id ??= Guid.NewGuid().ToString();
        item.Timestamp ??= now;
        item.EnqueuedAt = now;
        EnsureDedupeKey(item, now);

        lock (_lock)
        {
            EnsureState();

            // 去重闸门 1：recentShown 60s 窗口（刚显示过/刚被处理过）
            var recent = _state.RecentShown!;
            recent.RemoveAll(r => now - r.Ts >= DedupeWindowMs);
            if (recent.Any(r => MatchesItem(r.DedupeKey, r.Title, r.Type, item)))
            {
                Log.Debug("[BubbleScheduler] 去重拦截(recentShown): {Title}", item.Title);
                return false;
            }

            // 去重闸门 2：正在任意槽位显示
            foreach (var s in SlotNames)
            {
                var slotItem = _state.Slots![s]?.Item;
                if (slotItem != null && MatchesItem(slotItem.DedupeKey, slotItem.Title, slotItem.Type, item))
                {
                    Log.Debug("[BubbleScheduler] 去重拦截(槽位 {Slot} 显示中): {Title}", s, item.Title);
                    return false;
                }
            }

            // 去重闸门 3：已在队列中
            if (_state.Queue!.Any(q => MatchesItem(q.DedupeKey, q.Title, q.Type, item)))
            {
                Log.Debug("[BubbleScheduler] 去重拦截(已在队列): {Title}", item.Title);
                return false;
            }

            _state.Queue!.Add(item);
            SortByPriority(_state.Queue);
            SaveToStorage();
            Log.Information("[BubbleScheduler] 入队: {Title} (type={Type}, importance={Importance}, queue={Count})",
                item.Title, item.Type ?? "-", item.Importance ?? 0, _state.Queue.Count);
            return true;
        }
    }

    // ============ Tick（三槽位调度核心） ============

    /// <summary>
    /// 执行一次调度 tick（对齐 Electron tick 三阶段）：
    /// 1. 各槽位过期检测（普通项按时长 / 持久项按绝对上限）→ hide 事件
    /// 2. 按优先级填充空槽位 → show 事件
    /// 3. 抢占：全部槽位满时，高优先级持久项可替换最低优先级槽位（旧持久项重新入队）
    /// </summary>
    public TickResult Tick()
    {
        lock (_lock)
        {
            EnsureState();
            var now = Now();
            var events = new List<BubbleSlotEvent>();
            var changed = false;

            // ---- 1. 各槽位过期检测 ----
            foreach (var slotName in SlotNames)
            {
                var slot = _state.Slots![slotName];
                if (slot?.Item == null) continue;

                var elapsed = now - slot.StartedAt;
                if (IsSlotItemExpired(slot.Item, elapsed))
                {
                    PushRecent(slot.Item, now);
                    _state.Slots[slotName] = null;
                    events.Add(new BubbleSlotEvent { Slot = slotName, Action = "hide", Item = slot.Item });
                    changed = true;
                    Log.Debug("[BubbleScheduler] tick→hide: slot={Slot} \"{Title}\" 过期(elapsed={Elapsed}s)",
                        slotName, slot.Item.Title, elapsed / 1000);
                }
            }

            // ---- 2. 填充空槽位 ----
            SortByPriority(_state.Queue!);

            // 陈旧项丢弃（WPF 保留特性：入队超 120s 的非持久行情提醒已无展示价值）
            var dropped = 0;
            while (_state.Queue!.Count > 0)
            {
                var head = _state.Queue[0];
                if (head == null) { _state.Queue.RemoveAt(0); dropped++; continue; }
                if (!IsEffectivelyPersistent(head) && now - (head.Timestamp ?? now) > DequeueStaleDropMs)
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
                changed = true;
            }

            foreach (var slotName in SlotNames)
            {
                if (_state.Slots![slotName]?.Item != null) continue;
                if (_state.Queue.Count == 0) break;

                var next = _state.Queue[0];
                _state.Queue.RemoveAt(0);
                _state.Slots[slotName] = new BubbleSlot { Item = next, StartedAt = now };
                events.Add(new BubbleSlotEvent { Slot = slotName, Action = "show", Item = next });
                changed = true;
                Log.Debug("[BubbleScheduler] tick→show: slot={Slot} \"{Title}\" (importance={Importance}, queue剩余={Count})",
                    slotName, next.Title, next.Importance ?? 0, _state.Queue.Count);
            }

            // ---- 3. 抢占：全部槽位满时，高优先级持久项替换最低优先级槽位 ----
            var allOccupied = SlotNames.All(s => _state.Slots![s]?.Item != null);
            if (allOccupied && _state.Queue.Count > 0)
            {
                var queueFront = _state.Queue[0];
                if (queueFront != null && IsEffectivelyPersistent(queueFront))
                {
                    // 找优先级最低的槽位
                    var lowestSlot = SlotNames[0];
                    var lowestScore = PriorityScore(_state.Slots![lowestSlot]!.Item);
                    foreach (var s in SlotNames)
                    {
                        var score = PriorityScore(_state.Slots![s]!.Item);
                        if (score < lowestScore)
                        {
                            lowestScore = score;
                            lowestSlot = s;
                        }
                    }

                    if (PriorityScore(queueFront) > lowestScore)
                    {
                        var oldItem = _state.Slots[lowestSlot]!.Item!;
                        // 旧持久项重新入队（enqueuedAt=now+1 排到同分项后面，对齐 Electron）
                        if (IsEffectivelyPersistent(oldItem))
                        {
                            var reQueued = CloneItem(oldItem);
                            reQueued.EnqueuedAt = now + 1;
                            _state.Queue.Add(reQueued);
                        }
                        // 清除旧项的 recentShown 记录，允许后续重新显示
                        _state.RecentShown!.RemoveAll(r => r.DedupeKey == oldItem.DedupeKey);

                        _state.Queue.RemoveAt(0);
                        _state.Slots[lowestSlot] = new BubbleSlot { Item = queueFront, StartedAt = now };
                        SortByPriority(_state.Queue);
                        events.Add(new BubbleSlotEvent { Slot = lowestSlot, Action = "show", Item = queueFront });
                        changed = true;
                        Log.Information("[BubbleScheduler] 抢占: slot={Slot} \"{New}\" 替换 \"{Old}\" (queue剩余={Count})",
                            lowestSlot, queueFront.Title, oldItem.Title, _state.Queue.Count);
                    }
                }
            }

            // 清理过期 recentShown
            _state.RecentShown!.RemoveAll(r => now - r.Ts >= DedupeWindowMs);

            if (changed) SaveToStorage();

            return new TickResult
            {
                Events = events,
                HasActive = SlotNames.Any(s => _state.Slots![s]?.Item != null),
                Action = events.Count == 0 ? "idle" : (events.Any(e => e.Action == "show") ? "show" : "hide")
            };
        }
    }

    // ============ 槽位操作 ============

    /// <summary>
    /// 用户响应后结束指定槽位气泡（对齐 Electron ackSlot）：
    /// 记录 recentShown 防止 60s 内重复弹出。
    /// </summary>
    public void AckSlot(string? slotName, string reason = "ack")
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(slotName) || !SlotNames.Contains(slotName)) return;
            var slot = _state.Slots?.GetValueOrDefault(slotName);
            if (slot?.Item == null) return;

            var now = Now();
            PushRecent(slot.Item, now);
            _state.Slots![slotName] = null;
            SaveToStorage();
            Log.Debug("[BubbleScheduler] ackSlot: slot={Slot} \"{Title}\" ({Reason})",
                slotName, slot.Item.Title, reason);
        }
    }

    /// <summary>
    /// 结束所有槽位气泡（手动隐藏 / 退出清理）：逐槽位记录 recentShown 后清空。
    /// </summary>
    public void AckAllSlots(string reason = "manual_hide")
    {
        lock (_lock)
        {
            var now = Now();
            var any = false;
            foreach (var s in SlotNames)
            {
                var slot = _state.Slots?.GetValueOrDefault(s);
                if (slot?.Item == null) continue;
                PushRecent(slot.Item, now);
                _state.Slots![s] = null;
                any = true;
            }
            if (any)
            {
                SaveToStorage();
                Log.Debug("[BubbleScheduler] ackAllSlots: {Reason}", reason);
            }
        }
    }

    /// <summary>
    /// 按去重键撤销提醒（对齐 Electron cancel）：队列移除 + 清除匹配槽位 + recentShown 标记。
    /// </summary>
    public void CancelByDedupeKey(string? dedupeKey)
    {
        if (string.IsNullOrEmpty(dedupeKey)) return;
        lock (_lock)
        {
            var now = Now();
            var removed = _state.Queue?.RemoveAll(q => q != null && q.DedupeKey == dedupeKey) ?? 0;
            foreach (var s in SlotNames)
            {
                var slot = _state.Slots?.GetValueOrDefault(s);
                if (slot?.Item?.DedupeKey == dedupeKey)
                {
                    PushRecent(slot.Item, now);
                    _state.Slots![s] = null;
                    removed++;
                }
            }
            if (removed > 0)
            {
                SaveToStorage();
                Log.Debug("[BubbleScheduler] cancel: 已撤销 dedupeKey={DedupeKey}", dedupeKey);
            }
        }
    }

    /// <summary>读取指定槽位当前显示项（PetWindowManager 动作回写用）</summary>
    public BubbleQueueItem? GetSlotItem(string? slotName)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(slotName) || !SlotNames.Contains(slotName)) return null;
            return _state.Slots?.GetValueOrDefault(slotName)?.Item;
        }
    }

    /// <summary>从存储重载</summary>
    public void ReloadFromStorage()
    {
        lock (_lock) { LoadFromStorage(); }
    }

    // ============ 内部工具 ============

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Dictionary<string, BubbleSlot?> NewSlots() => new()
    {
        ["top"] = null,
        ["left"] = null,
        ["right"] = null
    };

    private void EnsureState()
    {
        _state ??= new();
        _state.Queue ??= new();
        _state.RecentShown ??= new();
        _state.Slots ??= NewSlots();
    }

    /// <summary>去重键（对齐 Electron：type:stockCode:id；无 Id 时用 Title 兜底保证同标题项可去重）</summary>
    private static void EnsureDedupeKey(BubbleQueueItem item, long now)
    {
        item.DedupeKey ??= $"{item.Type ?? "unknown"}:{item.StockCode ?? ""}:{item.Id ?? item.Title}";
        if (item.Timestamp == null) item.Timestamp = now;
    }

    /// <summary>去重匹配：dedupeKey 精确匹配 或 标题+类型匹配（WPF 原有行为保留）</summary>
    private static bool MatchesItem(string? dedupeKey, string? title, string? type, BubbleQueueItem item) =>
        (!string.IsNullOrEmpty(dedupeKey) && dedupeKey == item.DedupeKey)
        || (title == item.Title && type == item.Type);

    /// <summary>
    /// 有效持久判定：Persistent 标记 / 无时长 / 带动作按钮。
    /// WPF 侧自定义提醒（DurationMs=8000+Actions）与收盘提醒（Persistent+Actions）
    /// 在 Electron 中均为 persistent 类型（durationMs=0），故带 Actions 一并视为持久。
    /// </summary>
    private static bool IsEffectivelyPersistent(BubbleQueueItem item) =>
        item.Persistent || (item.DurationMs ?? 0) <= 0 || item.Actions is { Count: > 0 };

    /// <summary>
    /// 槽位项过期判定：
    /// 普通项到时过期；持久项按绝对上限（无动作 5 分钟 / 带动作 30 分钟，对齐 Electron）。
    /// </summary>
    private static bool IsSlotItemExpired(BubbleQueueItem item, long elapsed)
    {
        if (IsEffectivelyPersistent(item))
        {
            var hasActions = item.Actions is { Count: > 0 };
            return elapsed >= (hasActions ? PersistentActionMaxLifeMs : PersistentMaxLifeMs);
        }
        return elapsed >= (item.DurationMs ?? 8000);
    }

    /// <summary>优先级分数（对齐 Electron _priorityScore）：custom_reminder +1000，加 importance</summary>
    private static int PriorityScore(BubbleQueueItem? item)
    {
        if (item == null) return 0;
        var boost = item.Type == "custom_reminder" ? CustomPriorityBoost : 0;
        return boost + (item.Importance ?? 0);
    }

    /// <summary>按优先级排序：分数降序 → 入队时间升序（对齐 Electron _sortByPriority）</summary>
    private static void SortByPriority(List<BubbleQueueItem> queue) =>
        queue.Sort(CompareByPriority);

    private static int CompareByPriority(BubbleQueueItem? a, BubbleQueueItem? b)
    {
        var byScore = PriorityScore(b) - PriorityScore(a);
        if (byScore != 0) return byScore;
        return EffectiveEnqueuedAt(a).CompareTo(EffectiveEnqueuedAt(b));
    }

    private static long EffectiveEnqueuedAt(BubbleQueueItem? item) =>
        item?.EnqueuedAt != 0 ? item!.EnqueuedAt : (item?.Timestamp ?? 0);

    private void PushRecent(BubbleQueueItem item, long now)
    {
        _state.RecentShown ??= new();
        _state.RecentShown.Add(new BubbleRecentShown
        {
            DedupeKey = item.DedupeKey,
            Title = item.Title,
            Type = item.Type,
            Ts = now
        });
    }

    private static BubbleQueueItem CloneItem(BubbleQueueItem item) => new()
    {
        Id = item.Id,
        DedupeKey = item.DedupeKey,
        Title = item.Title,
        Content = item.Content,
        Level = item.Level,
        Importance = item.Importance,
        Actions = item.Actions,
        Persistent = item.Persistent,
        DurationMs = item.DurationMs,
        Timestamp = item.Timestamp,
        EnqueuedAt = item.EnqueuedAt,
        Type = item.Type,
        StockCode = item.StockCode,
        StockName = item.StockName
    };
}

// ============ 数据模型 ============

public class BubbleQueueState
{
    public int Version { get; set; } = 4;
    public List<BubbleQueueItem>? Queue { get; set; } = new();
    /// <summary>三槽位显示状态：top / left / right</summary>
    public Dictionary<string, BubbleSlot?>? Slots { get; set; }
    /// <summary>60s 窗口去重记录（对齐 Electron recentShown）</summary>
    public List<BubbleRecentShown>? RecentShown { get; set; } = new();
    /// <summary>旧版（v2 单槽）遗留字段：仅读取迁移用，新写入恒为 null</summary>
    public BubbleQueueItem? Current { get; set; }
}

public class BubbleSlot
{
    public BubbleQueueItem? Item { get; set; }
    public long StartedAt { get; set; }
}

public class BubbleRecentShown
{
    public string? DedupeKey { get; set; }
    public string? Title { get; set; }
    public string? Type { get; set; }
    public long Ts { get; set; }
}

public class BubbleQueueItem
{
    public string? Id { get; set; }
    /// <summary>去重键（type:stockCode:id），入队时自动生成</summary>
    public string? DedupeKey { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public string? Level { get; set; }
    public int? Importance { get; set; }
    public List<BubbleAction>? Actions { get; set; }
    public bool Persistent { get; set; }
    public long? DurationMs { get; set; }
    public long? Timestamp { get; set; }
    /// <summary>入队时间戳（优先级同分时按 FIFO 排序）</summary>
    public long EnqueuedAt { get; set; }
    /// <summary>旧版遗留字段（新逻辑的显示起始时间在 BubbleSlot.StartedAt）</summary>
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
    /// <summary>聚合动作（兼容语义）：show=任一槽位有显示事件 / hide=仅有隐藏事件 / idle=无变化</summary>
    public string Action { get; set; } = "idle";
    /// <summary>逐槽位事件列表（仅状态变化时非空）</summary>
    public List<BubbleSlotEvent>? Events { get; set; }
    /// <summary>是否仍有槽位在显示</summary>
    public bool HasActive { get; set; }
}

/// <summary>单槽位状态变化事件</summary>
public class BubbleSlotEvent
{
    /// <summary>槽位名：top / left / right</summary>
    public string Slot { get; set; } = "";
    /// <summary>show / hide</summary>
    public string Action { get; set; } = "";
    public BubbleQueueItem? Item { get; set; }
}

/// <summary>三槽位常量（对应 Electron currentBubbles 的 top/left/right 键）</summary>
public static class BubbleSlots
{
    public const string Top = "top";
    public const string Left = "left";
    public const string Right = "right";

    public static readonly string[] All = { Top, Left, Right };

    public static bool IsValid(string? slot) => slot == Top || slot == Left || slot == Right;
}
