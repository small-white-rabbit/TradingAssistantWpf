# TradingAssistantWpf 代码审查报告

**审查日期**：2026-09-01
**范围**：`StockReview.Core`（44 个 .cs）+ `StockReviewWpf`（~90 个 .cs），共 143 个文件 / 50,984 行
**方法**：全量模式扫描定位候选 → 逐条回读源码核实 → 关键逻辑对照原版 JS 基准（已内嵌于 CrossLanguageBaseline）

> **✅ 修复状态（2026-09-01 晚更新）**：全部问题已逐条二次核实并**全部修复完毕（含 B4）**；
> 编译零警告，**140 个测试全部通过**。
> - 已修复：B1 / B2 / B3 / B4 / B5 / B6 / P1 / P2 / P3 / R1 / R2 / R3
> - **B4 已于 2026-09-01 晚完成 JS/C# 双侧同步修正**（等价简化，保留 `prev0` 除零防御，
>   零行为偏差）：JS `sellPointDetector.js` 与 C# `SellPointDetectorService.Scoring.cs` 同步修改，
>   11 个 CrossLanguageBaseline 基准脚本全部正常运行。
> - B6 修复方案有升级：因面板由 `PetWindow.ShowPanel` 动态挂载/摘除且缓存复用，改为
>   **Loaded（先 -= 再 += 幂等）/ Unloaded 配对订阅**，比原文的「构造订阅 + Unloaded 退订」更安全。
> - R4 为 B4 死计算的同一条目，已随 B4 一并修复。

> **核实声明**：以下每一条都经过源码回读确认，行号为实际位置。凡代理报告中发现但核实为误判的，已在文末「排除项」中说明。

---

## 一、Bug（6 项）

### B1 [P1] 提醒状态在门槛检查之前提交 —— 被限流/波门拦截后当日永久丢失

**位置**：`StockReview.Core/Services/PlanSchedulerService.Checking.cs:452-474`（目标价）、`:584-597`（止损价）

**问题描述**：去重标记 `MarkLevelHitNotified(...)` 与 `_actionEmittedToday[actionKey] = true` 写在了 `WaveGateAllows`（波内限发）和 `CheckRateLimit`（限频）**之前**。这两个门槛任意一个 return，状态已经落盘，该信号当天再也不会触发。

止损分支尤其严重——止损限频是「10 分钟 3 次」，一旦用户在 10 分钟内被触发 3 次（例如价格在止损位反复穿插），第 4 次的 `_actionEmittedToday` 已被写入，即使 10 分钟窗口早已过期、即使这是真正的跌破，当天也不会再有任何止损提醒。

**原始代码**（目标价分支，452-474 行）：
```csharp
452:        // 同状态冷却（15分钟）+ 状态持久化（pullback/wasAboveTarget 判定依赖）
453:        if (!CanEmitSignal(key, newState, 15 * 60 * 1000)) return;
454:
455:        // 级别去重
456:        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;
457:        MarkLevelHitNotified(plan.Id, newState);              // ← 提前写状态
458:
459:        // 动作型提醒当日一次去重
460:        var actionKey = $"{plan.Id}:target_{newState}";
461:        if (_actionEmittedToday.ContainsKey(actionKey)) return;
462:        _actionEmittedToday[actionKey] = true;                // ← 提前写状态
463:
464:        // 波内限发检查
465:        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;  // ← 被拦截则状态已污染
466:
467:        if (plan.PlanType == "watch")
468:        {
469:            // 数据收集模式：仅记录不弹气泡
470:            return;
471:        }
472:
473:        if (!CheckRateLimit(plan.StockCode, "target_price")) return;  // ← 被限流则状态已污染
474:        CommitSignalState(key, newState);
```

**修复代码**（目标价分支，替换 455-474 行，保留 452-453 的 `CanEmitSignal`）：
```csharp
        // ---- 只读去重判定：不写任何状态，避免被下方门槛拦截后当日永久丢失 ----
        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;

        var actionKey = $"{plan.Id}:target_{newState}";
        if (_actionEmittedToday.ContainsKey(actionKey)) return;

        // ---- 门槛检查：全部通过后才允许落状态 ----
        // 波内限发检查
        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch")
        {
            // 数据收集模式：仅记录不弹气泡
            return;
        }

        if (!CheckRateLimit(plan.StockCode, "target_price")) return;

        // ---- 所有门槛通过 → 提交去重状态与信号状态 ----
        MarkLevelHitNotified(plan.Id, newState);
        _actionEmittedToday[actionKey] = true;
        CommitSignalState(key, newState);
```

**修复代码**（止损价分支，替换 584-597 行）：
```csharp
        // ---- 只读去重判定 ----
        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;

        var actionKey = $"{plan.Id}:stop_{newState}";
        if (_actionEmittedToday.ContainsKey(actionKey)) return;

        // ---- 门槛检查 ----
        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch") return;

        // 止损使用 10 分钟窗口 3 次限频
        if (!CheckRateLimit(plan.StockCode, "stop_loss", 3, 10 * 60 * 1000)) return;

        // ---- 门槛全通过 → 提交状态 ----
        MarkLevelHitNotified(plan.Id, newState);
        _actionEmittedToday[actionKey] = true;
        CommitSignalState(key, newState);
```

> 注：`CanEmitSignal`（453 / 582 行）是**冷却 + 状态持久化**逻辑，`pullback`/`wasAboveTarget` 判定依赖它，属于状态机输入而非纯门槛，**保持在原位不移动**。

---

### B2 [P1] SQLite 未设置 busy_timeout —— 多线程并发写直接抛锁异常

**位置**：`StockReview.Core/Data/DatabaseService.cs:61-75`

**问题描述**：`CreateConnection()` 开启了 WAL 模式。WAL 下**读-写可以并发**，但**写-写仍然互斥**。本项目是典型的多线程写场景：

- 调度线程通过 `Task.Run` 写（`TradeRepositoryService.cs:129/150/179`、`StrongStockRepositoryService.cs:98/118/146`）
- UI 线程写（`SignalEventService.SaveEvents`、`PetWindow.SavePosition`）
- 备份线程写（`DatabaseService.Backup`）
- `BulkPut` 长事务（`DatabaseService.cs:618-621`）

全项目 grep `busy_timeout` **零命中**。冲突时 SQLite 立即返回 `SQLITE_BUSY`，Microsoft.Data.Sqlite 抛 `SqliteException: SQLite Error 5: 'database is locked'`。备份或批量导入期间跑调度写入时极易触发。

**原始代码**：
```csharp
61:    public SqliteConnection CreateConnection()
62:    {
63:        var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWriteCreate");
64:        conn.Open();
65:        using var cmd = conn.CreateCommand();
66:        cmd.CommandText = @"
67:            PRAGMA journal_mode=WAL;
68:            PRAGMA foreign_keys=ON;
69:            PRAGMA cache_size=-64000;
70:            PRAGMA synchronous=NORMAL;
71:            PRAGMA temp_store=MEMORY;
72:            PRAGMA mmap_size=268435456;";
73:        cmd.ExecuteNonQuery();
74:        return conn;
75:    }
```

**修复代码**：
```csharp
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA cache_size=-64000;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;";
        cmd.ExecuteNonQuery();
        return conn;
    }
```

`journal_mode=WAL` 是**持久化** PRAGMA（写进数据库文件头，只需设置一次），不应放在每次开连接的热路径上——它是最贵的一条，需要短暂获取排他锁。在 `Initialize()` 里设置一次即可：

```csharp
    public void Initialize()
    {
        Log.Information("[SQLite] 数据库初始化: {Path}", DbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 持久化 PRAGMA：写入数据库文件头，整个生命周期只需执行一次
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
```

---

### B3 [P2] SignalEventService 内存字典无锁跨线程读写

**位置**：`StockReview.Core/Services/SignalEventService.cs:75`（字段）、`:291-327`（`RecordEvent` 写）、`:340-345`（`GetTodayEvents` 读）

**问题描述**：`_events` 是普通 `Dictionary<string, List<SignalEvent>>`。写入发生在调度线程（7 个调用点全在 `PlanSchedulerService.Checking.cs:176/507/632/1121/1228/1276/1368`），读取发生在 UI 线程（`GetTodayEvents`）。无锁同时读写会抛 `InvalidOperationException: Collection was modified`，.NET 下 `Dictionary` 并发写甚至可能导致桶数组损坏进入死循环。

**原始代码**：
```csharp
75:    private Dictionary<string, List<SignalEvent>> _events = new();

291:    public SignalEvent RecordEvent(SignalEventInput input)
292:    {
293:        var today = TodayKey();
294:        if (!_events.ContainsKey(today))
295:            _events[today] = new List<SignalEvent>();
...
324:        _events[today].Add(record);
325:        SaveEvents();
326:        return record;
327:    }

340:    public List<SignalEvent> GetTodayEvents(string? stockCode = null)
341:    {
342:        var today = TodayKey();
343:        var events = _events.TryGetValue(today, out var list) ? list : new List<SignalEvent>();
344:        return stockCode != null ? events.Where(e => e.StockCode == stockCode).ToList() : events;
345:    }
```

注意 344 行还有一个额外缺陷：未传 `stockCode` 时**直接返回内部 List 引用**，调用方拿到后可被外部修改，且枚举时若字典被改会崩。

**修复代码**：
```csharp
    private readonly object _eventsLock = new();
    private Dictionary<string, List<SignalEvent>> _events = new();

    public SignalEvent RecordEvent(SignalEventInput input)
    {
        var today = TodayKey();
        lock (_eventsLock)
        {
            if (!_events.ContainsKey(today))
                _events[today] = new List<SignalEvent>();
            // ...（中间构造 record 的逻辑保持不变，无共享状态访问）...

            // 去重：同一股票同一信号类型在30秒内只记录一次
            var existing = _events[today].Find(e =>
                e.StockCode == record.StockCode &&
                e.SignalType == record.SignalType &&
                Math.Abs(e.Timestamp - record.Timestamp) < 30000);
            if (existing != null) return existing;

            _events[today].Add(record);
        }   // 锁内不做 IO：SaveEvents 移到锁外

        SaveEvents();
        return record;
    }

    public List<SignalEvent> GetTodayEvents(string? stockCode = null)
    {
        var today = TodayKey();
        lock (_eventsLock)
        {
            var events = _events.TryGetValue(today, out var list) ? list : new List<SignalEvent>();
            // 始终返回副本：避免外部持有内部引用造成二次竞争
            return stockCode != null
                ? events.Where(e => e.StockCode == stockCode).ToList()
                : events.ToList();
        }
    }
```

> 注意：`SaveEvents()` 内部有 `SaveThrottleMs` 节流（:180），但仍会序列化 `_events`，建议同步加锁或改为在锁内取快照后锁外序列化。

---

### B4 [P2] CheckMomentumConfirm 逻辑冗余 —— JS 原版同源缺陷

**位置**：`StockReview.Core/Engines/SellPointDetectorService.Scoring.cs:312-323`

**问题描述**：
```csharp
322:        return recent5Change < 0 || (prev5Change > 0.2 && recent5Change < 0);
```
第二个分支被第一个**完全包含**：`A || (B && A)` 恒等于 `A`。因此 `prev5Change`（317、321 行）与 `prev5` 切片（316 行）全是死计算，函数实际等价于 `return recent5Change < 0;`。

**关键核实**：已对照原版 JS 基准（CrossLanguageBaseline 快照）：
```js
return recent5Change < 0 || (prev5Change > 0.2 && recent5Change < 0)
```
**JS 原版完全相同**——所以这不是 C# 翻译错误，是原版就存在的缺陷。JS 注释（4174-4175 行）写的是「动量确认：近5根下跌（与卖点方向一致）或**前5根涨近5根跌（动量转向）**」，从注释意图看第二个分支应是想表达「先涨后跌」的转向，正确写法大概率是 `recent5Change < 0 || (prev5Change > 0.2 && recent5Change < prev5Change)` 之类。

**处理记录（2026-09-01 晚，已完成双侧同步修正）**：采用**等价简化**方案（而非下方猜测的「动量转向」语义改写——那会改变策略行为，需另行决策）：
`A || (B && A)` 恒等于 `A`，删除冗余右支与死计算 `prev5Change`，**保留 `prev0`/`!prev0` 除零防御检查**，实现零行为偏差。

JS 侧（`sellPointDetector.js`）与 C# 侧同步修改，两侧逐字对应：

```csharp
    private bool CheckMomentumConfirm(List<IntradaySnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count < 10) return true;
        var recent5 = snapshots.Skip(snapshots.Count - 5).Take(5).ToList();
        var prev5 = snapshots.Skip(snapshots.Count - 10).Take(5).ToList();
        var recent0 = recent5[0].Price;
        var prev0 = prev5[0].Price;
        if (recent0 <= 0 || prev0 <= 0) return true;
        var recent5Change = (recent5[^1].Price - recent0) / recent0 * 100;
        // 2026-09-01 双侧同步修正（JS sellPointDetector.js:4176 已同步）：原式
        // `recent5Change < 0 || (prev5Change > 0.2 && recent5Change < 0)` 的右支被左支
        // 完全包含，恒等于 `recent5Change < 0`，prev5Change 死计算已移除；
        // prev0<=0 除零防御检查保留（与 JS 原版一致）。
        return recent5Change < 0;
    }
```

验证：11 个 `CrossLanguageBaseline/*.mjs` 基准脚本全部正常运行；`dotnet build` 零警告；140 个测试全部通过。

---

### B5 [P2] Put() 开两次连接 + TOCTOU 竞态

**位置**：`StockReview.Core/Data/DatabaseService.cs:588-609`

**问题描述**：`Put` 先 `GetById`（开连接 ①，读完即关），再 `Update`/`Add`（开连接 ②）。两次往返，且中间无事务——并发下两个线程可能同时读到「不存在」而都走 `Add`，产生重复行。

**原始代码**：
```csharp
588:    public object Put(string table, IDictionary<string, object?> data)
589:    {
590:        AssertTable(table);
...
599:        if (data.TryGetValue("id", out var idObj) && idObj != null)
600:        {
601:            var existing = GetById(table, idObj);   // ← 连接 ①
602:            if (existing != null)
603:            {
604:                Update(table, idObj, data);          // ← 连接 ②
605:                return idObj;
606:            }
607:        }
608:        return Add(table, data);                     // ← 连接 ②
609:    }
```

**修复代码**（单连接 + 单语句 upsert，原子且一次往返）：
```csharp
    public object Put(string table, IDictionary<string, object?> data)
    {
        AssertTable(table);
        if (table == "appConfig")
        {
            var key = (data.TryGetValue("key", out var kv) ? kv : null)?.ToString() ?? "";
            var val = (data.TryGetValue("value", out var vv) ? vv : null)?.ToString() ?? "";
            using var conn = CreateConnection();
            conn.Execute("INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)", new { key, val });
            return key;
        }

        var serialized = SerializeRecord(data);
        var now = DateTime.UtcNow.ToString("o");

        using var c = CreateConnection();
        if (data.TryGetValue("id", out var idObj) && idObj != null)
        {
            serialized["updatedAt"] = now;
            var cols = serialized.Keys.ToList();
            foreach (var k in cols) AssertIdentifier(k);
            var setClause = string.Join(", ", cols.Select(k => $"\"{k}\" = @{k}"));
            // 单语句 UPDATE：受影响行数为 0 表示不存在，再 INSERT
            var updated = c.Execute(
                $"UPDATE \"{table}\" SET {setClause} WHERE id = @__id",
                serialized.Append(new KeyValuePair<string, object?>("__id", idObj))
                          .ToDictionary(x => x.Key, x => x.Value));
            if (updated > 0) return idObj;
        }

        serialized["createdAt"] = now;
        serialized["updatedAt"] = now;
        var keys = serialized.Keys.ToList();
        foreach (var k in keys) AssertIdentifier(k);
        var colList = string.Join(", ", keys.Select(k => $"\"{k}\""));
        var ph = string.Join(", ", keys.Select(k => $"@{k}"));
        c.Execute($"INSERT INTO \"{table}\" ({colList}) VALUES ({ph})", serialized);
        return c.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }
```

> 若担心并发下 UPDATE/INSERT 仍有窗口，可整段包一层 `using var tx = c.BeginTransaction();`。

---

### B6 [P2] IntradayChartPanel 订阅 FutuAdapter.OnQuotePush 后从不退订

**位置**：`StockReviewWpf/Views/Pet/Panels/IntradayChartPanel.xaml.cs:92-93`

**问题描述**：`_futu` 来自 DI 容器（进程级单例，长生命周期），而 panel 是短生命周期控件。订阅在构造函数里，`OnQuotePush` 在整个 WPF 项目中**只出现这一次**（grep 确认），即**全项目没有任何 `-=`**。

**核实后的严重度说明（这点很重要，不要按 P0 处理）**：
- `PetWindow.xaml.cs:418` 用 `_intradayPanel ??= new IntradayChartPanel()` 缓存了面板，`_intradayPanel` 全项目**从未被置 null**（grep 确认）
- `PetWindowManager.cs:59-63` 的 `_petWindow` 同样是 `if (_petWindow == null)` 单例，关闭时走 `Hide()` 隐藏到托盘而非销毁（:457）

所以**当前每进程最多泄漏 1 个面板实例**，不构成活跃泄漏。但这是典型的「定时炸弹」：一旦将来有人加了「切换宠物重置面板」「重建 FutuAdapter 重连」之类的逻辑，就会演变成 N 倍泄漏 + N 倍 `Dispatcher.BeginInvoke` 推送（推送来自富途 SDK 回调线程，`FutuAdapter.cs:432/467`）。

**原始代码**：
```csharp
79:        Loaded += (_, _) => RenderEmpty();
80:
81:        // 提醒列表开关：初始值从 appConfig 恢复（持久化在 ReminderSwitch_Changed）
...
91:        // 富途订阅推送：秒级实时更新分时末端（订阅制优先于轮询刷新）
92:        if (_futu != null)
93:            _futu.OnQuotePush += OnFutuQuotePush;
94:    }
```

**修复代码**：
```csharp
        // 富途订阅推送：秒级实时更新分时末端（订阅制优先于轮询刷新）
        if (_futu != null)
            _futu.OnQuotePush += OnFutuQuotePush;
        // 配对退订：_futu 是 DI 单例，若不退订将永久持有本控件引用
        Unloaded += (_, _) =>
        {
            if (_futu != null)
                _futu.OnQuotePush -= OnFutuQuotePush;
        };
```

---

## 二、性能热点（3 项）

### P1 [P1] 每次开连接都执行 6 条 PRAGMA，含 64MB 页缓存 + 256MB mmap

**位置**：`StockReview.Core/Data/DatabaseService.cs:61-75`

**问题描述**：`Query` / `QueryFirstOrDefault` / `Execute` / `GetById` / `Add` / `Update` 每个方法都 `CreateConnection()` 并 `Dispose()`。`CreateConnection` 每次执行 6 条 PRAGMA。其中：

- `cache_size=-64000` → 每连接 **64 MB** 页缓存配额
- `mmap_size=268435456` → 每连接预留 **256 MB** 地址空间
- `journal_mode=WAL` → 持久化 PRAGMA，重复设置纯属浪费，且需短暂排他锁

Microsoft.Data.Sqlite 默认开启连接池，`Dispose` 会归还池化连接（页缓存不立即释放），但 PRAGMA 语句每次仍重跑一遍。热路径上（调度循环每秒写多次）这是纯粹的 CPU 浪费。

**量化影响**：调度线程每次检测周期写 N 条记录 → N 次 `CreateConnection` → 6N 条 PRAGMA 语句。N=100 时每次周期多执行 600 条语句。

**修复代码**：见 B2 的 `CreateConnection` + `Initialize` 改造（把 `journal_mode=WAL` 移出热路径，加 `busy_timeout`）。若仍需进一步压低单连接内存占用，建议把 `cache_size` 从 `-64000`（64MB）下调到 `-8000`（8MB）——本项目单表数据量不大，64MB 页缓存收益极低：

```csharp
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA cache_size=-8000;        /* 8MB，原 64MB 对本库规模无收益 */
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=67108864;      /* 64MB，原 256MB 预留过大 */
```

---

### P2 [P2] SavePosition 每次 new JsonSerializerOptions + UI 线程同步写文件

**位置**：`StockReviewWpf/Views/Pet/PetWindow.xaml.cs:935-944`

**问题描述**：拖动宠物窗口时 `OnLocationChanged` 每次触发都 `Stop/Start` 300ms 防抖计时器（:931-932），计时器到期后在 **UI 线程**执行 `File.WriteAllText`。同时每次都 `new JsonSerializerOptions { WriteIndented = true }` —— `JsonSerializerOptions` 构造开销不小（内部要做大量反射缓存初始化），且 `WriteIndented` 使输出变大。

**原始代码**：
```csharp
935:    private void SavePosition()
936:    {
937:        try
938:        {
939:            var statePath = Path.Combine(App.DataDir, "pet-window-state.json");
940:            var state = new PetWindowState { X = Left, Y = Top };
941:            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
942:            File.WriteAllText(statePath, json);
943:        }
944:        catch { }
945:    }
```

**修复代码**：
```csharp
    // 静态复用：JsonSerializerOptions 构造含反射缓存初始化，不应每次分配
    private static readonly JsonSerializerOptions WindowStateJsonOpts = new() { WriteIndented = false };

    private void SavePosition()
    {
        try
        {
            var statePath = Path.Combine(App.DataDir, "pet-window-state.json");
            var state = new PetWindowState { X = Left, Y = Top };
            var json = JsonSerializer.Serialize(state, WindowStateJsonOpts);
            File.WriteAllText(statePath, json);
        }
        catch (Exception ex)
        {
            // 原为 catch { } 空捕获，写盘失败会静默丢失窗口位置且无从排查
            Log.Warning(ex, "[宠物] 窗口位置保存失败");
        }
    }
```

> 单条 JSON 很小，同步写可接受；若要彻底移出 UI 线程，可改为 `Task.Run(() => File.WriteAllText(...))`，但需处理重入（拖动中可能并发触发）。当前 300ms 防抖已足够，保持同步 + 复用 Options 即可。

---

### P3 [P2] Core 服务层 `Task.Run` 未加 `ConfigureAwait(false)`

**位置**：`StockReview.Core/Services/TradeRepositoryService.cs:41/61/74/88/101/114/129/150/179`、`StrongStockRepositoryService.cs:40/57/67/78/88/98/118/146`（共 16 处）

**问题描述**：Core 层是类库，不涉及 UI 上下文。但 `await Task.Run(...)` 默认会捕获并回投 `SynchronizationContext`。当这些服务被 UI 线程调用时，每个 await 都会多一次到 UI 线程的消息泵投递——在调度循环高频调用路径上是可观的额外开销，且在 UI 繁忙时会造成延迟堆积。

**原始代码**（`TradeRepositoryService.cs:41`）：
```csharp
41:            _trades = await Task.Run(() => _db.GetAll("trades"), ct);
```

**修复代码**：
```csharp
            _trades = await Task.Run(() => _db.GetAll("trades"), ct).ConfigureAwait(false);
```

其余 15 处同样在 `.ConfigureAwait(false)` 追加即可。注意：**仅在 Core 类库层加**；WPF ViewModel 层调用这些方法时若需要回到 UI 线程更新绑定，由 VM 侧自然捕获上下文，不受影响。

---

## 三、冗余代码（4 项）

### R1 [P2] 10 个一次性 Python 诊断脚本已提交入库

**位置**：`StockReview.Tests/_diag/` 全部 10 个 `.py` 文件

```
StockReview.Tests/_diag/appconfig_keys.py
StockReview.Tests/_diag/dbquery.py
StockReview.Tests/_diag/events.py
StockReview.Tests/_diag/events2.py
StockReview.Tests/_diag/events3.py
StockReview.Tests/_diag/hist2.py
StockReview.Tests/_diag/history.py
StockReview.Tests/_diag/ms.py
StockReview.Tests/_diag/replay.py
StockReview.Tests/_diag/tables.py
```

**问题描述**：`events.py` / `events2.py` / `events3.py` / `hist2.py` 这种编号递进的命名，明显是排查问题时边写边扔的临时脚本，不属于测试资产，也不参与任何 CI（C# 测试项目不会执行 .py）。

**修复**：删除整个 `_diag` 目录。若其中有复用价值的查询逻辑，应先沉淀为正式的诊断工具或测试。

> 注意：`StockReview.Tests/CrossLanguageBaseline/*.mjs`（11 个）**不要删**——那是跨语言对齐验证的 JS 基准，是活的测试资产。

---

### R2 [P2] 根目录 7.6 MB 构建日志已入库

**位置**：
```
msbuild-diag.log   7,496,526 字节 (7.15 MB)
publish-trace.log     58,900 字节
```

**问题描述**：MSBuild 诊断日志与发布跟踪日志，一次性排查产物，与源码无关，且 7MB 会拖慢 git clone / status。

**修复**：删除两个文件，并在 `.gitignore` 中补规则：
```
*.log
msbuild-diag.log
publish-trace.log
```

---

### R3 [P2] Add() 与 Put() 中 appConfig 分支完全重复

**位置**：`StockReview.Core/Data/DatabaseService.cs:536-543` 与 `:591-598`

**问题描述**：两段代码逐字相同，只是所在的公开方法不同。任何一侧改了另一侧不同步就会产生行为分叉。

**原始代码**：
```csharp
536:        if (table == "appConfig")
537:        {
538:            var key = (data.TryGetValue("key", out var kv) ? kv : null)?.ToString() ?? "";
539:            var val = (data.TryGetValue("value", out var vv) ? vv : null)?.ToString() ?? "";
540:            using var conn = CreateConnection();
541:            conn.Execute("INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)", new { key, val });
542:            return key;
543:        }
```
```csharp
591:        if (table == "appConfig")
592:        {
593:            var key = (data.TryGetValue("key", out var kv) ? kv : null)?.ToString() ?? "";
594:            var val = (data.TryGetValue("value", out var vv) ? vv : null)?.ToString() ?? "";
595:            using var conn = CreateConnection();
596:            conn.Execute("INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)", new { key, val });
597:            return key;
598:        }
```

**修复代码**（抽出私有方法，两处改为调用）：
```csharp
    /// <summary>appConfig KV 表专用写入（INSERT OR REPLACE 语义），Add/Put/Update 共用。</summary>
    private object PutAppConfig(IDictionary<string, object?> data)
    {
        var key = (data.TryGetValue("key", out var kv) ? kv : null)?.ToString() ?? "";
        var val = (data.TryGetValue("value", out var vv) ? vv : null)?.ToString() ?? "";
        using var conn = CreateConnection();
        conn.Execute("INSERT OR REPLACE INTO appConfig (key, value) VALUES (@key, @val)", new { key, val });
        return key;
    }
```
`Add` 536-543 行与 `Put` 591-598 行统一替换为：
```csharp
        if (table == "appConfig")
            return PutAppConfig(data);
```

---

### R4 [P3] CheckMomentumConfirm 中的死计算

**位置**：`StockReview.Core/Engines/SellPointDetectorService.Scoring.cs:316-321`

即 B4 中 `prev5Change` 计算（321 行）及冗余条件右支——它们的结果永远不影响返回值。已随 B4 双侧同步修正一并清除（`prev5`/`prev0` 因除零防御检查保留而保留）。

---

## 四、排除项（核实后确认「不是问题」）

诚实记录这几条，避免后续重复排查：

| 项 | 结论 |
|---|---|
| `async void` 崩进程风险 | 全项目 4 处 `async void`，其中 `HtmlEditorControl.OnLoadedOnce`、`WebChartView.OnNavigationCompleted`、`PetWindowManager.CheckOpenDStatus` 是事件处理器（合法）；`MainViewModel.PreWarmViewCache`(:260) 是 public `async void`，但**方法体内已有完整 try/catch**（:295），异常不会逸出，不构成崩溃风险，仅属规范问题 |
| `.Result` / `.Wait()` 死锁 | grep 命中均为属性名（如 `dialog.Result`、`evt.Evaluation.Result`），唯一真实调用在 `App.xaml.cs` 退出路径，且已包裹在 `Task.Run` 内 + 注释说明，不会死锁 |
| 循环内 `string +=` | 全项目零命中，无此问题 |
| 注释掉的死代码块 | 全项目零命中（`^\s*//\s*(var\|if\|for\|return...)` 匹配数为 0），代码整洁度好 |
| 富途推送跨线程改 UI | `IntradayChartPanel.OnFutuQuotePush`(:255-260) 已正确用 `Dispatcher.BeginInvoke` 包装，无跨线程违规 |
| `PetSpriteControl` / `BubbleSlotView` 定时器泄漏 | `PetSpriteControl` 有 `Unloaded → _frameTimer.Stop()`（:174-178），正确 |
| DB 操作阻塞 UI 线程 | `TradeRepositoryService` / `StrongStockRepositoryService` 全部用 `Task.Run` 卸载，正确 |
| `SaveEvents` 每事件落盘 | `:180` 已有 `SaveThrottleMs` 节流，非每事件写盘 |

---

## 五、建议修复顺序

| 优先级 | 项目 | 理由 |
|---|---|---|
| 1 | **B2** SQLite busy_timeout | 改动 6 行，消除并发写崩溃，风险极低 |
| 2 | **B1** 提醒状态提交顺序 | 直接影响止损/目标价提醒的可靠性，改动局部 |
| 3 | **P1** PRAGMA 热路径 | 与 B2 同处修改，一次搞定 |
| 4 | **B3** SignalEventService 加锁 | 消除偶发 `Collection was modified` |
| 5 | **B6** 事件退订 | 2 行，消除定时炸弹 |
| 6 | **R1 / R2** 清理入库垃圾 | 零风险，建议顺手做 |
| 7 | **B5 / R3** Put 与 appConfig 去重 | 涉及写路径，建议补测试后改 |
| 8 | **P3** ConfigureAwait | 16 处机械替换，建议单独一个 commit |
| 9 | **B4 / R4** CheckMomentumConfirm | ✅ 已完成：JS/C# 双侧同步等价简化，基准脚本全过 |

> **B4 特别提醒**：这是原版 JS 就有的缺陷，C# 是忠实翻译。任何修改都必须 JS/C# 双侧同步，并重跑 `StockReview.Tests/CrossLanguageBaseline/` 的跨语言基准，否则会破坏「回测已对齐」这一项目前提。
