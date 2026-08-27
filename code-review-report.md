# 代码审查报告 · TradingAssistantWpf（Core 层）

**审查人**：火眼眼（Code Reviewer）　**日期**：2026-08-27
**范围**：`StockReview.Core`（引擎 / 服务 / 数据访问），对照 README 中 Electron JS → C# 迁移基准。
**前提**：交易算法回测已与 JS 基准对齐，故本次聚焦**质量 / 性能 / 安全 / 可维护性**，并为后续「架构通」重构扫雷。

> **✅ 修复记录（2026-08-27 同日完成）**：除 🟡#6（DateTime 时区语义，需产品决策、留架构通）与 💭#7-10（重构项）外，
> 🔴#1、🟡#2/#3/#4/#5 已全部修复并通过 `dotnet build`（0 警告 0 错误）+ 48/48 单元测试（含 6 个新增 DB 写入回归测试）。
> 修改文件：`DatabaseService.cs`、`Sources.cs`、`FutuAdapter.cs`、6 个引擎/服务文件（JsRound）、新增 `StockReview.Core/JsCompat.cs`、
> 新增 `StockReview.Tests/Data/DatabaseServiceWriteTests.cs`。详见文末「修复对照表」。


---

## 一、总体印象
工程素养不差：异步卫生好（无 `async void`、无空 `catch`、无 `.Wait()/.Result` 死锁）、DB 写入用事务+回滚、表名有白名单 `AssertTable`、数值类型（用 `REAL`/double）正确对齐 JS `number`。
主要短板：**文化区相关的数值解析**、**取整语义与 JS 不一致**、**富途连接阻塞**、**Dapper 占位符混用**，以及巨文件可维护性。

## 二、做得好的（建议保留）
- ✅ 索引计算全程用 `Math.Floor`，正确对齐 JS `Math.floor`，避开 `(int)` 截断的经典坑。
- ✅ SQLite 用 `REAL`（double）存储，与 JS `number` 精度一致，避免 decimal 错配。
- ✅ `BulkPut`/`BulkAdd` 用事务 + `try { } catch { rollback; throw }`，数据安全。
- ✅ `AssertTable`（`DatabaseService.cs:1260`）用 `TableSet` 白名单拦截非法表名，杜绝表名注入。
- ✅ 行 842 注释已明确规避 `?` 与 `@` 混用，说明作者有意关注该陷阱。
- ✅ 大量 `CultureInfo.InvariantCulture` 已用于 `DateTime`/`int.Parse`/`ToString`，文化区意识在线。

## 三、问题清单

### 🔴 待验证阻断项
**1. Dapper `?` 占位符与 Dictionary 参数混用，可能写入失败**
- 位置：`DatabaseService.cs` `Add()` 549-552、`BulkAdd()` 685-688、`BulkPut()` 652-655。
- 现象：`VALUES (?, ?, ?)` 用位置占位，但 `Add()` 传的是 `serialized`（Dictionary）。Dapper 对 Dictionary 按**命名** `@key` 绑定，不认 `?`；对数组才按位置绑定。`BulkAdd` 传数组、`Add` 传字典却共用同一 `?` 模板，两处写法不一致。
- 风险：若 `Add()` 该路径确实不绑定，每次插入会抛 `SQLiteException` 或写入错值——属**数据写入失败/静默错误**。
- 建议：统一改命名参数（与作者行 843 `entryType IN (@e{i})` 做法一致）：
  ```csharp
  var cols = string.Join(", ", keys.Select(k => $"\"{k}\""));
  var ph   = string.Join(", ", keys.Select(k => $"@{k}"));
  conn.Execute($"INSERT INTO \"{table}\" ({cols}) VALUES ({ph})", serialized);
  ```
- 动作：**用单元测试对 `Add`/`BulkAdd`/`BulkPut` 各跑一次真实落库验证**，确认后再决定重构。

### 🟡 应当修复
**2. 行情解析 `decimal.Parse` 缺 `CultureInfo.InvariantCulture`（潜在崩溃）**
- 位置：`MarketData/Sources/Sources.cs` 72-78、273-280、433-439（十余处 `decimal.Parse(parts[n])`）。
- 为什么：腾讯/新浪行情是 en-US 格式（`"12.34"`），同文件其它处（318/370/383/474/516）已用 `InvariantCulture`，此处漏了。zh-CN 下当前不报错；但 Windows 区域若设为用 `,` 作小数位的语言（de-DE/fr-FR 等），`decimal.Parse("12.34")` 会 `FormatException` 或误读——**换区域即崩**。
- 建议：全部改为 `decimal.Parse(parts[n], CultureInfo.InvariantCulture)`；顺带确认 `parts` 不含千分位。

**3. `Math.Round` 默认「银行家舍入」与 JS `Math.round`（四舍五入）不一致**
- 位置：`SellPointDetectorService.cs` 2850/3429/3581/3625/3686、`PlanSchedulerService.cs` 1899/1900/3609-3720、`SignalEventService.cs` 1039、`MultiFactorEngineService.cs` 429、`FutuIntradaySource.cs` 92。
- 为什么：C# `Math.Round(2.5)`=2（Banker's），JS `Math.round(2.5)`=3（half-up）。回测已对齐说明精确 .5 临界点大概率未命中热路径，但属**潜伏正确性偏差**。
- 建议：封装 `static double JsRound(double x, int d = 0) => Math.Round(x, d, MidpointRounding.AwayFromZero);`（`BuyPointDetectorService.cs:718` 已如此），全局替换 `Math.Round` 为 `JsRound`。

**4. 富途连接用 `Thread.Sleep(1000)` 同步阻塞**
- 位置：`Futu/FutuAdapter.cs` 85。
- 为什么：`InitConnect` 异步（回调置 `_connectErrCode`），`Connect` 用 `Thread.Sleep(1000)` 轮询等待——阻塞调用线程 1s；若在 UI 线程会卡界面，且超时不可控。
- 建议：用 `TaskCompletionSource` + `ManualResetEventSlim.Wait(TimeSpan)` 设超时，或改 `async Task<bool>` + `await Task.Delay` + 回调信号。

**5. SQL 标识符由记录键拼接，未做白名单校验**
- 位置：`DatabaseService.cs` `Add/Update`（`serialized.Keys` 拼列名）、`BuildWhereClause` 1272 `"{key}" = @{key}`。
- 为什么：值与 `whereClause` 均已参数化（✅），但列名/键名直接插值。当前键来自 C# 属性名（可信），风险低；若日后有外部字典流入即存注入面。
- 建议：加 `IsValidIdentifier(key)` 校验（字母数字下划线），或复用 `AssertTable` 思路建 `AssertColumn`。

**6. `DateTime.Now` 与 `DateTime.UtcNow` 混用**
- 位置：持久化用 `DateTime.UtcNow` + `"o"` ISO（566/636/674，好）；但 `DateTime.Now` 出现在 `TradePlanService`(6)、`ReminderHistoryService`(2)、`MarketTimeService`(2)、`Sources.cs`(3)、`ImageService`(1)。
- 为什么：JS 版跑在本地时区；C# 写库 UTC、逻辑本地，混用可能在跨时区/午夜边界产生细微偏差。回测已对齐说明当前一致，属脆弱点。
- 建议：约定「存储一律 UTC，展示再转本地」，涉及交易日切分的逻辑统一用同一时钟。

### 💭 锦上添花 / 重构就绪（交「架构通」）
- **7. 巨文件耦合**：`PlanSchedulerService.cs`(4622)、`SellPointDetectorService.cs`(3882)、`SignalEventService.cs`(2033) 是拆分首要目标。
- **8. 半成品**：`Microsoft.ML.OnnxRuntime` 已引用未接入，OCR 仍走百度云 API（`StockOcrService`）——离线能力缺失，建议「架构通」阶段落地本地推理。
- **9. 测试覆盖极低**：Tests 1256 行 / 业务 5.2 万行；第 1、3 项改动须在补测试后做，交「软件工坊」。
- **10. 魔法数字**：涨跌幅阈值、`limitPct`、窗口时长等散布各引擎，建议抽到配置/常量。

## 四、下一步
1. ~~先验证 🔴 项~~ ✅ 已修复并新增 6 个真实落库回归测试（`DatabaseServiceWriteTests`）锁死。
2. ~~修复 🟡 #2、#3~~ ✅ 已修复。
3. #6（DateTime 时区语义）随「架构通」重构一并处理（涉及交易日切分逻辑的产品决策，不宜盲改）。
4. 测试补充与 OCR 接管交「软件工坊」。

## 五、修复对照表

| 问题 | 修复方式 | 验证 |
|------|----------|------|
| 🔴#1 Dapper `?`+Dictionary | `Add/BulkPut/BulkAdd` 统一改 `@{k}` 命名参数；`IN (?)`+List 改 Dapper 列表展开 `IN @ids/@vals`；与 ImportAll 已验证写法对齐 | 新增 `DatabaseServiceWriteTests`（6 测试：Add 落库/BulkAdd/BulkPut upsert/WhereAnyOf/Update/非法键拒绝） |
| 🟡#2 decimal.Parse 无文化区 | 新增 `InvParse`（InvariantCulture），`Sources.cs` 全部 `decimal/long/DateTime.Parse(parts[...])` 改走 `InvParse` | 构建 0 警告 |
| 🟡#3 Math.Round 银行家舍入 | 新增 `JsMath.JsRound`（double+decimal 重载，AwayFromZero），替换 6 文件 23 处默认 `Math.Round`（`SellPoint/BuyPoint Detector`、`PlanScheduler`、`SignalEvent`、`SchedulerEngineAdapters`、`FutuIntradaySource`） | 既有 42 测试全过（回测对齐行为未受扰动） |
| 🟡#4 Thread.Sleep(1000) | `FutuAdapter` 新增 `ManualResetEventSlim _connectDone`：`OnInitConnect/OnDisconnect` 回调 Set，`Connect` Wait(5s) 超时兜底 | 构建通过 |
| 🟡#5 SQL 标识符未校验 | 新增 `AssertIdentifier`（正则 `^[A-Za-z_][A-Za-z0-9_]*$`），覆盖 `Add/Update/BulkPut/BulkAdd/BuildWhereClause/WhereStartsWith/WhereAnyOf` 拼接点 | 测试：非法键（含引号）被拒抛 `ArgumentException` |
| 🟡#6 DateTime 混用 | **未改**（留架构通） | — |

**遗留（后续）**：`Sources.cs` 尚有约 13 处 `TryParse` 无文化区（软失败、解析为 0，非崩溃级），建议随架构重构统一改 `InvParse.TryX`；💭#7-10 重构项同前。
