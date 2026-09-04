# TradingAssistantWpf 深度优化报告（性能 / 架构 / 逻辑）

**审查日期**：2026-09-02
**审查范围**：性能、架构、逻辑正确性 三个维度
**代码基线**：144 个 .cs / 51,372 行（Core 40 / Wpf 84 / Tests 20），113 个测试方法（`[Theory]` 展开后 142 个用例）
**方法**：三路独立并行扫描 + 主审查人对每条高严重度结论回查源码原文验证
**与 `deep-review-2026-09-02.md` 的关系**：那份是「重命名重构后」的专项验证报告，本报告是全维度优化审查，范围不同、结论不重叠。

---

## 执行摘要

整体判断：这是一个**工程质量明显超出个人项目平均水平**的 codebase。日志统一（全项目 `Console.WriteLine` 零命中）、SQL 注入面封堵到位、列表虚拟化已正确开启、连接池/WAL/busy_timeout 已修复、JS→C# 取整语义用 `JsMath.JsRound` 系统对齐。上一轮（2026-09-01）B1–B6 / P1–P3 / R1–R3 的修复是扎实的。

**但存在 1 个会真实造成金钱损失的问题（F1）和 1 个必然触发的功能性 bug（L1）**，建议优先处理。

| 级别 | 数量 | 代表问题 |
|---|---|---|
| 🔴 紧急 | 2 | 静默丢交易信号、统计页按年月筛选必崩 |
| 🟠 高 | 5 | 写放大、数据撕裂、无事务批量写、无界增长表、UI 层写 SQL |
| 🟡 中 | 6 | 接口缺失、消息框耦合、去重竞态、God Object |
| 🟢 低 | 4 | 显示除零、指标无缓存、节假日表边界 |

---

## 一、🔴 紧急（建议本周修复）

### F1. 富途推送检测路径无 catch → 交易信号静默丢失

**文件**：`StockReview.Core/Services/PlanSchedulerService.Futu.cs:191`、`:200-223`
**调用链**：`OnFutuPush`（富途 SDK 回调线程）→ `_ = RunPushDrivenDetectAsync(...)` → `DetectForStockAsync` → `CheckPlanSignals`

```csharp
:191      _ = RunPushDrivenDetectAsync(stockCode, quote);   // fire-and-forget，未 await

:200  private async Task RunPushDrivenDetectAsync(string stockCode, StockQuote pushQuote)
:202      if (!_pushDetectRunning.TryAdd(stockCode, 1)) return;
:204      try
:205      {
:206          await DetectForStockAsync(stockCode, pushQuote);
...
:219      finally                                            // ⚠ 只有 finally，没有 catch
:220      {
:221          _pushDetectRunning.TryRemove(stockCode, out _);
:222      }
```

**为什么致命**：项目的定时器主循环对同一套检测逻辑有完善的异常隔离——

```csharp
// PlanSchedulerService.cs:326-338 —— 定时器路径
private async Task RunTask(string name, Func<Task> task)
{
    try { await task(); }
    catch (Exception ex) { Log.Warning(ex, "[计划调度] 子任务 {Name} 异常", name); }
}
```

而 `CheckPlanSignals`（`Checking.cs:110`）内部**自身无 try/catch**。于是两条路径保护严重不对称：

| 路径 | 异常兜底 | 结果 |
|---|---|---|
| 定时器路径 | `RunTask` 捕获 → 记日志 → 下一 tick 重试 | ✅ 自愈 |
| 推送路径 | 无 → 逃逸为未观察 Task 异常 | ❌ 永久丢失 |

`App.xaml.cs` 的 `TaskScheduler_UnobservedTaskException` 虽已 `SetObserved()`（不会崩进程），但该钩子**在 GC 终结 Task 时才触发、且无法恢复**。

**净结果：该股该轮信号永久丢失，无 UI 提示、无重试、无告警。**

对交易辅助程序，"静默失效"比崩溃更危险——崩溃你立刻知道，静默丢信号你会以为系统在正常工作。

**触发条件（任一即可）**：
- F4 的集合枚举被并发修改，抛 `InvalidOperationException`
- 指标计算遇到退化数据（停牌 / 一字板 / 除权）抛异常
- 任意空引用

**修复方案**（改动极小）：

```csharp
private async Task RunPushDrivenDetectAsync(string stockCode, StockQuote pushQuote)
{
    if (!_pushDetectRunning.TryAdd(stockCode, 1)) return;
    try
    {
        await DetectForStockAsync(stockCode, pushQuote);
        while (_pushDetectQueued.TryRemove(stockCode, out _))
        {
            var latest = pushQuote;
            if (_batchQuoteCache.TryGetValue(stockCode, out var cached) && cached.Data.CurrentPrice > 0)
                latest = cached.Data;
            await DetectForStockAsync(stockCode, latest);
        }
    }
    catch (Exception ex)
    {
        // 与定时器路径对齐：让丢信号可观测，而不是静默
        Log.Error(ex, "[计划调度] 推送检测异常 stock={Code} price={Price}",
                  stockCode, pushQuote?.CurrentPrice);
    }
    finally
    {
        _pushDetectRunning.TryRemove(stockCode, out _);
    }
}
```

**风险**：极低。只加 catch 分支，不改变正常路径行为。

---

### L1. 统计接口 SQL 拼接出双 WHERE → 按年月筛选必然报错

**文件**：`StockReview.Core/Data/DatabaseService.cs:936` 与 `:966`

```csharp
:936  var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
...
:966  var problemRows = conn.Query(
          $"SELECT problemTags FROM trades {whereClause} WHERE problemTags IS NOT NULL " +
          "AND problemTags != '' AND problemTags != '[]'", param);
```

三段 SQL（946 / 961 / 966）中，前两段的 `WHERE` 由 `whereClause` 自带、语法正确，**唯独 966 行在模板里又硬编码了一个 `WHERE`**。当筛选条件非空时：

```sql
SELECT problemTags FROM trades WHERE "tradeDate" LIKE @tradeDate WHERE problemTags IS NOT NULL ...
--                                                              ^^^^^ 语法错误
```

**可触发性已确认**：入口是 `DbHostObject.cs:256-270`：

```csharp
:265  o?.TryGetValue("yearMonth", out ym);
:266  o?.TryGetValue("year", out yr);
:268  return ToJson(_db.GetStatisticsSummary(ym, yr));
```

即**用户在统计页选择具体年月时，问题标签统计必然抛异常**（被 `Wrap` 捕获，表现为该模块数据为空或错误提示）。

**修复方案**：

```csharp
var tagFilter = string.IsNullOrEmpty(whereClause)
    ? "WHERE problemTags IS NOT NULL AND problemTags != '' AND problemTags != '[]'"
    : "AND problemTags IS NOT NULL AND problemTags != '' AND problemTags != '[]'";
var problemRows = conn.Query($"SELECT problemTags FROM trades {whereClause} {tagFilter}", param);
```

**建议**：顺手补一个回归测试（传 `yearMonth` 调用 `GetStatisticsSummary` 不抛异常），防止再犯。

---

## 二、🟠 高优先级

### P1. 提醒历史每产生一次就全量重写 appConfig（写放大）

**文件**：`StockReview.Core/Services/ReminderHistoryService.cs:151-152`、`:96-105`

```csharp
:151  _history.Insert(0, record);   // List.Insert(0) 本身 O(n)
:152  SaveToStorage();              // 每次提醒都触发

:96   private void SaveToStorage()
:98       var json = JsonSerializer.Serialize(_history);   // 全量序列化（保留 3 天）
:99       _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = StorageKey, ["value"] = json });
```

**调用频率**：`SchedulerPetStore.AddReminder` → `AddRecord`，每次信号提醒（含快速涨跌、涨停等高频类型）都调用。

**量化**：盘中监控 30 只股，快速涨跌提醒每分钟可能触发数次 → 每次数百条历史 JSON 序列化 + `appConfig` 整行 UPDATE。更糟的是该写入与 `SignalEventService.SaveEvents`（5s 节流）、`FlushSnapshotsAsync`（60s）**争抢 SQLite 唯一的写锁**（WAL 下写-写仍互斥）。

**修复方案**：给 `SaveToStorage` 加节流合批（1–3s）：

```csharp
private readonly TimeSpan _saveThrottle = TimeSpan.FromSeconds(2);
private DateTime _lastSave = DateTime.MinValue;
private bool _dirty;

private void SaveToStorage()
{
    _dirty = true;
    var now = DateTime.UtcNow;
    if (now - _lastSave < _saveThrottle) return;
    Flush();   // Flush(): 序列化落盘 + 清 _dirty + 更新 _lastSave
}
// 需配兜底定时器 / 进程退出时 Flush，确保 _dirty 最终落盘
```

**风险**：中。需确保进程退出前有 flush，否则丢最近 2s 历史（可接受）。

---

### P2. 快照批量落地未用事务 → N 次 fsync

**文件**：`StockReview.Core/Services/PlanSchedulerService.Snapshots.cs:600-620`

```csharp
:602  using var conn = _db.CreateConnection();
:603  const string sql = @"INSERT INTO price_snapshots (...) VALUES (...)";
:607  conn.Execute(sql, allSnapshots.Select(s => new { ... }));   // ⚠ 无 BeginTransaction
```

Dapper 对 `IEnumerable` 参数会**逐行执行**，每行独立自动提交 → 一次 flush 数百~数千行 = 数百次 fsync。该方法每 60s 调用一次（`PlanSchedulerService.cs:321`）。

**修复方案**：

```csharp
using var conn = _db.CreateConnection();
using var tx = conn.BeginTransaction();
try
{
    conn.Execute(sql, allSnapshots.Select(...), tx);
    tx.Commit();
}
catch { tx.Rollback(); throw; }
```

**风险**：极低。这是本报告里**性价比最高的一条**——改动 3 行，收益显著。

---

### P3. 跨线程共享可变 StockQuote → 脏读与 decimal 撕裂

**文件**：`PlanSchedulerService.Futu.cs:138-158`（写）、`:212`（读）

```csharp
:139  if (_batchQuoteCache.TryGetValue(stockCode, out var cached))
:141      quote = cached.Data;             // 取到缓存里的活对象
:142      quote.CurrentPrice = price;      // 富途线程原地改写字段
:143      quote.Volume = volume;
:144      quote.Amount = amount;
...
:191  _ = RunPushDrivenDetectAsync(stockCode, quote);   // 同一引用交给线程池

:212  if (_batchQuoteCache.TryGetValue(stockCode, out var cached) && cached.Data.CurrentPrice > 0)
:214      latest = cached.Data;            // 检测线程读的仍是同一个被持续改写对象
```

**两层问题**：

1. **逻辑脏读（更常见）**：`DetectForStockAsync` 是 `async`，`await` 之后会切到线程池继续执行。期间富途线程下一次推送到达 → 改写同一对象的 `CurrentPrice`。于是**同一次检测内部，先读到的价格与后读到的价格不一致**——前一半指标用旧价、后一半用新价，产生不自洽的判断。

2. **decimal 撕裂读（罕见但后果严重）**：`decimal` 是 16 字节结构体，.NET 中读写**非原子**。极端情况下可能读到一个"半新半旧"的荒谬价格（如 1e±30）喂入涨跌幅/突破计算 → 偶发误报卖点。

**修复方案**：进入检测前做值快照，不共享活引用：

```csharp
// OnFutuPush 中：缓存存不可变快照（每次新建，而非复用改写）
_batchQuoteCache[stockCode] = (new StockQuote {
    Code = stockCode, CurrentPrice = price, Volume = volume,
    Amount = amount, DateTime = now
}, now.Add(cacheTtl));
```

分配成本（每推送一个小对象）远低于正确性收益。

**风险**：中。需先全局搜索 `_batchQuoteCache` 的所有读写点，确认没有其他代码依赖"复用同一实例"的语义。

---

### P4. `price_snapshots` 表无清理，无界增长

全仓检索仅发现 `CREATE TABLE` / `CREATE INDEX`（`PlanSchedulerService.Evolution.cs:291-302`），**无任何 DELETE 或保留期清理**。

**量化**：10 只监控股 × ~240 tick/日 ≈ 2,400 行/日 → 数月即数十万行。查询虽已按 `date(timestamp)` + 索引限定当日（Reminders.cs:421），不至于拖慢主查询，但会持续撑大 `data.db`，拖慢备份 / 恢复 / 云同步。

**修复方案**：每日盘后执行 `DELETE FROM price_snapshots WHERE timestamp < date('now','-7 days')`，可挂在 `OnDayChanged` 或盘后任务里。

---

### P5. UI 层（ViewModel）直接写 SQL

**文件**（共 13 处）：

| 文件 | 行号 |
|---|---|
| `InsightsViewModel.cs` | 940-941 |
| `YearMonthViewModel.cs` | 180, 181, 196, 268, 270, 280, 290, 921, 922, 1261, 1262, 1266, 1272 |

其中 `YearMonthViewModel.cs:1266/1272` 直接 `conn.Execute("INSERT INTO appConfig ...")`，绕过了已有的 `SettingsService`。

**根因**：`DatabaseService.CreateConnection()` 是 `public`（`DatabaseService.cs:62`），把 `SqliteConnection` 直接暴露给了 UI 层。

**影响**：ViewModel 无法脱离数据库单测；业务逻辑散落在 UI 层，Core 层无法复用；配置读写双轨（`SettingsService` 与直接 SQL 并存）易不一致。

**修复方案**（渐进式，不必一次做完）：
1. 将 VM 里的 SQL 逐批下沉为 Core 层仓储方法
2. 同时把 `CreateConnection()` 访问级别降为 `internal`（配合 `InternalsVisibleTo` 给测试项目）

**风险**：中。建议按 VM 逐个推进，每个 VM 下沉时补 1–2 个测试。

---

## 三、🟡 中优先级（架构与可维护性）

### A1. `DatabaseService` 无接口 → 无法 mock

`DatabaseService` 是具体类，36 个公共方法、4 类混杂职责：

| 职责 | 行号区间 |
|---|---|
| 通用文档存储（JS document-DB 直译） | 主体 |
| 原始 SQL 逃生口 `Query<T>/Execute/ExecuteBatch` | 1360-1388 |
| 业务聚合查询（统计） | 920-1087 |
| 备份恢复 | 1409-1447 |

现存 `Tests/Data/DatabaseServiceWriteTests.cs` 只能接真实 SQLite，这不是好测试。

**建议**：抽象 `IDatabase`（文档存储 + 仓储方法），把 `Query<T>` 逃生口降为 `internal`。聚合查询与备份恢复可后续拆为 `TradeStatisticsQuery` / `DbBackupService`。

---

### A2. `FutuAdapter` 无接口 → 行情源不可替换 / 不可测

`Core/Futu/FutuAdapter.cs:19` 是具体类，`MarketDataAggregator` 直接接收它（`App.xaml.cs:437`）。

**值得肯定**：`MarketDataAggregator` 本身已有 `IMarketDataSource` 抽象 + `InsertPrimarySource/AddIntradaySource` 多源降级链，设计是对的。缺的只是最外层 `IFutuAdapter` 这层壳。

---

### A3. 40 处 `MessageBox.Show` 直接写在 ViewModel

分布：`InsightsViewModel` / `PatternOptimizeViewModel` / `SettingsViewModel` / `YearMonthViewModel` / `PetGalleryPanelViewModel`。

**影响**：VM 强耦合 WPF UI、无法自动化确认流程、无法单测。

**建议**：引入轻量 `IDialogService`（1 接口 + 1 实现，约 40 行），VM 改调 `await _dialogs.ShowAsync(...)` 返回 `bool`。这是本报告**风险最低、收益明确**的架构改进项。

---

### A4. 服务定位器反模式（**已修正误报，实际仅 5 处**）

全项目共 39 处 `GetRequiredService`，但其中 **34 处在 `App.xaml.cs`** —— 那是应用的**组合根（Composition Root）**，在那里解析服务是标准且正确的用法，**不算反模式**。

真正需要治理的是业务层的 5 处：

| 文件 | 行号 |
|---|---|
| `MainViewModel.cs` | 117, 120 |
| `TrayService.cs` | 67, 73 |
| `CommonConverters.cs` | 399 |

这 3 个类本身已由 DI 创建，完全可改为构造函数注入。顺带一提，`PetWindowManager.cs:64` 的注释里团队已经写了"避免 ServiceLocator 反模式"——意识到位，只是没清理干净。

---

### A5. `PlanSchedulerService` 职责膨胀（6 个 partial / ~4000 行 / ~170 方法）

共享状态清单：15+ 个 `ConcurrentDictionary`，可清晰分两组：

- **行情缓存组**：`_dailyKlineCache` / `_capitalFlowCache` / `_batchQuoteCache` / `_snapshotCache` —— 仅被行情拉取方法读写
- **信号状态组**：`_signalStates` / `_rateLimiter` / `_waveGateStates` / `_levelHitNotified` / `_actionEmittedToday` —— 仅被信号检测方法读写

**建议**：这两组内聚性强、与主调度循环无交叉，可分别提取为 `MarketDataCache` 与 `SignalStateStore` 两个独立服务，由主类持有调用。Checking / Evolution / Reminders 三个 partial 可暂时保留。

**风险**：中，需调整 12 处注入依赖。**建议在 A1 之后推进**，避免二次返工。

---

### A6. 提醒去重 check-then-act 非原子 → 潜在重复提醒

**文件**：`PlanSchedulerService.Checking.cs:205-234`

```csharp
:205  if (!_signalStates.ContainsKey(key))     // 读
:206  {
:207      _signalStates[key] = new SignalStateEntry { ... };   // 写
...
:223      _petStore.AddReminder(new ReminderRequest { ... });  // 弹气泡
:232  }
```

虽 `_signalStates` 是 `ConcurrentDictionary`（这点做得对），但"**读-改-写**"整体不是原子操作。推送线程与 1 秒定时器线程会对同一计划执行这段，可能双双通过检查 → **同一事件弹两次提醒**。

`CheckRateLimit`（:221）提供了一定缓解，但不是严格保证。

**建议**：对同一 `stockCode` 的检测加 `SemaphoreSlim` 串行化，或改用 `GetOrAdd` + 原子提交模式。

---

## 四、🟢 低优先级

| 项 | 位置 | 说明 |
|---|---|---|
| 计划集合懒枚举 | `Futu.cs:231-234` | `.Concat().Where().ToList()` 枚举底层 List 期间若 UI 增删计划会抛 `Collection was modified`。修法：先 `new List<T>(_tradePlanStore.TodayPlans)` 快照。（与 F1 联动，会落入未观察异常） |
| 指标无增量缓存 | `SellPointDetectorService.Analyze.cs` | 每次推送全量重算，`GetSnapshots().ToList()` 全量复制。若 `EnablePatternSimilarity` 默认开启，DTW 是最大 CPU 消耗点（Analyze 内调用 8 次）。建议先**确认该开关默认值**，关闭可立省大头 |
| 节假日表止于 2028 | `MarketTimeService.cs:93` | 超表后降级为"仅周末休市"并打 `Log.Warning`——**已有防护意识，非静默失败**。2029-01-01 才会触发。可加"超年份即阻断监控"的硬保护 |
| 显示用除零 | `TradeFormView.xaml.cs:409/415` | `(close-prev)/prev*100` 当 `prev==0` 得 `Infinity`，显示"∞"。仅影响 UI 显示，不影响信号 |

---

## 五、已排查、确认无问题的方向

列出这些是为了**避免重复排查**——以下方向已系统性扫描过：

- ✅ **SQL 注入**：`DbHostObject` 表名走 `AllowedTables` 白名单 + `SafeIdent` 清洗；`DatabaseService.BuildWhereClause` 有 `AssertIdentifier`（正则 `^[A-Za-z_][A-Za-z0-9_]*$`）；所有值经 Dapper 参数化。**无注入面**
- ✅ **日志规范**：全项目 `Console.WriteLine` **0 处**，统一 Serilog
- ✅ **全局异常**：`App.xaml.cs` 三层未处理异常钩子完备
- ✅ **列表虚拟化**：`ReminderHistoryPanel` / `YearMonthView` / `StrongStocksView` / `DailyPickView` / `CasesView` 均已开启 `IsVirtualizing` + `Recycling` + `CanContentScroll`
- ✅ **Dispatcher 死锁**：`IntradayChartPanel.OnFutuQuotePush` 用 `BeginInvoke(Background)` 而非同步 `Invoke`
- ✅ **图表节流**：推送刷新被 `_lastLiveRender` 节流到 500ms，`_points` 上限 241
- ✅ **ObservableCollection**：各 VM 多为整体 `new ObservableCollection<T>(list)` 替换，无逐条 Add 的 N 次刷新问题
- ✅ **引擎除零**：`CalculateRSI`（avgLoss==0 早返）、`CalculateWR`（high==low 返 -50）、`CalculateMFI`（negFlow==0 返 100）、`GetPositionFactor`（max20==min20 返 0.5）等均已防御
- ✅ **JS/C# 取整对齐**：`Core/Engines` 内零直接 `Math.Round`，全部走 `JsMath.JsRound`（Floor(x+0.5)）。UI 层 `Math.Round` 仅用于显示，方向偏差可忽略
- ✅ **JS 数组越界差异**：C# `List` 索引会抛异常，但各入口均有 `Count` 前置（如 `snapshots.Count < 5` 直接返回）
- ✅ **连接池 / PRAGMA**：Microsoft.Data.Sqlite 默认 `Pooling=true`，上轮 P1（每查询 6 条 PRAGMA）已被池化摊薄，**无需再优化**
- ✅ **状态机跨天**：`_signalStates` 等在 `OnDayChanged` 统一清空，未发现中间态卡死
- ✅ **DI 生命周期**：除 `PetViewModel`（Transient）外全 Singleton，无 Singleton 依赖 Transient 的 captive dependency 问题
- ✅ **`async void`**：全项目 4 处均为事件处理器或方法体内已有 try/catch

---

## 六、建议执行路线图

按「**收益 ÷ 风险**」排序，不是按问题编号：

### 第一批（本周末，约 2 小时，几乎零风险）
1. **P2** `FlushSnapshotsAsync` 包事务 —— 3 行改动，收益最大
2. **F1** `RunPushDrivenDetectAsync` 加 catch —— 消除静默丢信号
3. **L1** 修双 WHERE + 补回归测试

### 第二批（下周，中等工作量）
4. **P1** 提醒历史落盘节流
5. **P3** `StockQuote` 改为不可变快照（配合全局搜索 `_batchQuoteCache` 所有访问点）
6. **P4** `price_snapshots` 保留期清理
7. **A3** 引入 `IDialogService` 替换 40 处 MessageBox

### 第三批（中长期，架构）
8. **A1** `DatabaseService` 接口化 + `CreateConnection()` 降 internal
9. **P5** VM 内联 SQL 下沉 Core 仓储（按 VM 逐个推进，每个补测试）
10. **A2** `IFutuAdapter` 接口化
11. **A6** 检测串行化 / 原子去重
12. **A5** `PlanSchedulerService` 抽 `MarketDataCache` + `SignalStateStore`（依赖 8、9 完成后做，避免返工）

> **关于测试**：当前 113 个测试方法（展开后 142 用例）全部集中在 Core 的检测器与工具层。上述重构的真正价值不在于"代码更好看"，而在于**让这些模块变得可测**——目前 `SettingsService`、`MarketDataAggregator`、全部 ViewModel、全部 WPF 服务、OCR 均无测试覆盖，根因就是接口缺失 + UI 层耦合。建议每完成一项重构，同步补 2–3 个测试锁住行为。

---

## 附：审查方法与局限

- **方法**：三路独立并行扫描（性能 / 架构 / 正确性），结论汇总后由主审查人对**每一条高严重度条目**回查源码原文验证行号与上下文。
- **已修正的误报 2 处**：
  1. 初判"服务定位器在 `SettingsViewModel` 有约 10 处"——实际为 0 处。真实情况是全项目 39 处中 34 处在组合根 `App.xaml.cs`（合法用法），仅 5 处在业务层。
  2. 初判"节假日表 2028 是定时炸弹"——实际代码已有 `Log.Warning` 降级提示，非静默失败，严重度已下调。
- **局限**：本次为**静态代码审查**，未做性能剖析。P1/P2/P4 的量化影响基于代码路径推算，实际收益建议用 SQLite 锁等待指标或 dotTrace 验证一次。

---

## 附 2：处置结果（2026-09-02 深夜，已全部完成）

逐条核实结论：**7 条属实并修复，2 条低优先级为误报**；架构级条目（A3/A1/A2/P5/A5）同日稍后全部完成，见文末附 3。**本报告全部条目处置完毕。**
验证基线：编译 0 警告 0 错误、**145/145 单测全过**（新增 3 个）、**12/12 跨语言基线全过**。

| 编号 | 处置 | 改动 |
|---|---|---|
| F1 | ✅ 已修 | `PlanSchedulerService.Futu.cs` `RunPushDrivenDetectAsync` 补 `catch`（Log.Error 含 stockCode/price），与定时器路径 `RunTask` 兜底对齐 |
| L1 | ✅ 已修+回归测试 | `DatabaseService.GetStatisticsSummary`：`tagFilter` 连接词随 `whereClause` 是否为空切换 AND/WHERE；新增 `StockReview.Tests/Data/StatisticsSummaryTests.cs`（按年月/按年不抛异常 + 无筛选聚合计数语义断言） |
| P1 | ✅ 已修 | `ReminderHistoryService` 落盘加 2 秒节流合批 + 定时兜底 flush，不再每次提醒全量重写 appConfig |
| P2 | ✅ 已修 | `PlanSchedulerService.Snapshots.cs` 批量 INSERT 包进事务（N 次 fsync → 1 次） |
| P3 | ✅ 已修 | `OnFutuPush` 每次推送新建不可变 `StockQuote` 快照写入缓存，消除检测线程脏读与 decimal 撕裂读 |
| P4 | ✅ 已修 | `OnDayChanged` 跨天回调清理 7 天前 `price_snapshots` |
| A6 | ✅ 已修 | Checking.cs×2 + Reminders.cs×1 共 3 处去重 check-then-act 改 `ConcurrentDictionary.TryAdd` 原子占位 |
| 低优先级-除零 | ❌ 误报 | `TradeFormView.xaml.cs:403` 已有 `prev <= 0` 提前返回保护 |
| 低优先级-懒枚举 | ❌ 误报 | `TodayPlans` 属性本身已返回 `ToList()` 副本 |

## 附 3：Phase 2 架构条目处置结果（2026-09-02，A3/A1/A2/P5/A5 全部完成）

在上述 7 条修复验证通过后，同日推进了路线图中的全部架构级条目。每步完成后均重新验证：
**编译 0 警告 0 错误、155/155 单测全过（本日累计新增 10 个）、12/12 跨语言基线全过**。

| 编号 | 处置 | 改动 |
|---|---|---|
| A3 | ✅ 已完成 | 新建 `StockReviewWpf/Services/DialogService.cs`（`IDialogService` 接口 + 默认实现，静态 `Instance`）。5 个 ViewModel 共 **38 处 `MessageBox.Show`** 替换为 `_dialogs.Info/Warn/Error/Confirm/ConfirmYesNo/ConfirmDanger`，构造函数加可选 `IDialogService? dialogs = null` 参数默认 `DialogService.Instance`，行为与原 MessageBox 完全一致。`SettingsViewModel` 清库双重确认改用 `ConfirmDanger`（Stop 图标）。2 处 View code-behind（PetSettingsPanel/CustomReminderPanel）属视图层合法用法保留 |
| A1 | ✅ 已完成 | 新建 `StockReview.Core/Data/IDatabaseService.cs`（~38 成员完整接口），`DatabaseService : IDatabaseService`；32 个消费文件类型引用改为 `IDatabaseService`（脚本批量替换 + 人工核对）；DI 注册 `AddSingleton<IDatabaseService>(sp => sp.GetRequiredService<DatabaseService>())` 保持单例。`CreateConnection()` 暂保留在接口（见 P5 备注） |
| A2 | ✅ 已完成 | 新建 `StockReview.Core/Futu/IFutuAdapter.cs`（事件 OnQuotePush/OnConnectionChanged + IsConnected + 7 方法），`FutuAdapter : IFutuAdapter`；3 个消费方（`FutuIntradaySource`、`PlanSchedulerService`、`IntradayChartPanel`）改依赖接口；DI 注册同 A1 模式 |
| P5 | ✅ 已完成 | `DatabaseService` 新增 5 个领域方法：`GetDailySummariesInRange` / `GetActiveEntryTypes` / `GetActiveProblemTags` / `GetTradesByYearPrefix` / `GetStrongStocksByYearPrefix`（行转换语义与原 VM 内联 SQL 完全一致）。`YearMonthViewModel`（LoadFormOptions/LoadDataAsync/SaveDiary）与 `InsightsViewModel`（SaveDiary）共 13 处内联 SQL 全部下沉；`SaveConfig` 手写 upsert 改为等价 `_db.Put("appConfig", …)`。**ViewModels 目录已零 `CreateConnection/conn.Query/conn.Execute`**。新增 `StockReview.Tests/Data/DomainQueryTests.cs` 6 个回归测试（注意：`Initialize()` 会预置 6 条默认 entryTypes/problemTags，断言按语义而非固定总数）。`CreateConnection` 降 internal 见下方「P5 收尾」行 |
| A5 | ✅ 已完成 | 新建 `Core/MarketData/MarketDataCache.cs`（7 个行情缓存字典：SnapshotCache/LiveTrail/SnapshotBuffer/TrendsCache/DailyKlineCache/CapitalFlowCache/BatchQuoteCache + `CleanupExpired`/`ResetForNewDay`，原 `CleanupExpiredCaches`/`CleanupCache<T>` 一并迁入）与 `Core/Services/SignalStateStore.cs`（5 个信号去重/限频字典 + `ResetForNewDay`）。`PlanSchedulerService` 6 个 partial 共 53 处引用机械替换（脚本批量，零残留），`OnDayChanged` 的 7 连 Clear 收敛为两个 `ResetForNewDay()`。新增 `MarketDataCacheTests.cs` 4 个测试锁定 TTL 清理/分时 10 分钟陈旧清理/跨天重置范围 |
| P5 收尾 | ✅ 已完成 | **`CreateConnection()` 已降 internal 并移出 `IDatabaseService`**：跨程序集唯一消费方 `WebBridge/DbHostObject.QueryRows` 下沉为 `DatabaseService.OrderByRawRows(table, field, dir, limit)`（原始行返回，不做 DeserializeRecord 值转换，与直连 SQL 时代逐字节一致）。Core 内部 3 个原始 SQL 仓储（PlanSchedulerService/StrongStockRepositoryService/TradeRepositoryService）改依赖具体类 `DatabaseService`（同程序集访问 internal 合法）；接口消费方（其余 29 文件）不受影响。**至此报告全部条目处置完毕** |

终态验证（A5 + P5 收尾后）：编译 0 警告 0 错误、**155/155 单测**（本日累计新增 10 个）、12/12 跨语言基线。
