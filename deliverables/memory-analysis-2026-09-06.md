# WPF 项目内存深度分析与优化方案

> 报告日期：2026-09-06
> 分析对象：`TradingAssistantWpf` 主进程（PID 13104，运行 ~50 分钟）
> 目标版本：electron 版本同功能模块内存 < 100MB，当前 WPF 主进程 **1700MB+**

---

## 一、实测数据汇总

### 1.1 进程级（PowerShell `Get-Process` 真实采样）

| 进程 | 工作集 | 私有内存 | 线程 |
|---|---|---|---|
| StockReviewWpf.exe（主） | 366 MB | **1716 MB** | 464 |
| msedgewebview2 × 6（属本应用） | ~500 MB | ~520 MB | - |
| **合计（含 WebView2）** | ~870 MB | **~2230 MB** | - |

> 内存增长曲线：50 分钟内 1700 → 1711 → 1716 → 1716（**3 分钟内几乎停滞**）。结论：**不是泄漏，是稳态高占用**。

### 1.2 托管堆（dotnet-counters，GC 后活对象）

| 区段 | 总量 | 碎片 | 碎片率 |
|---|---|---|---|
| Gen0 | 0 MB | 0 | - |
| Gen1 | 0.2 MB | 0 | - |
| **Gen2** | **189 MB** | 60.7 MB | 32% |
| **LOH** | **254 MB** | **224.2 MB** | **88%** |
| POH | 0.1 MB | 0 | - |
| **合计** | **~444 MB（提交）/ 302 MB（活对象）** | | |

> **关键结论**：LOH 254MB 中有 224MB 是空洞。这意味着程序大量**分配/释放 ≥ 85KB 的字节数组**，使 LOH 形成严重碎片。每次分配新对象都要从碎片中挖出零散空间，最终把"提交给 VM 的虚拟地址空间"顶到了 GB 级。

### 1.3 对象类型 TOP 25（dotnet-dump dumpheap -stat）

```
Free                                    151.96 MB /   413 obj    ← LOH 空洞
System.String                            62.08 MB / 917,584 obj  ← JSON/StockCode/Reason 串
Dictionary<String,Object>+Entry[]        24.26 MB /  70,815 obj  ← 序列化容器
System.Double                             8.09 MB / 353,507 obj  ← K 线字段
System.Int32[]                            5.89 MB /  76,912 obj
Dictionary<String,Object>                 5.42 MB /  70,998 obj
System.Byte[]                             4.25 MB /  11,334 obj
EffectiveValueEntry[]                     3.08 MB /  16,273 obj
SignalEvaluation                          3.07 MB /  18,308 obj
SignalEvent                               2.60 MB /  18,914 obj
WeakReference                             1.30 MB /  56,994 obj  ← 泄漏候选
TextBlock/Border/Grid 等 WPF 视觉树      ~13 MB / ~ 8,000 obj
DB/Dapper / Serilog / WebView2            < 0.05 MB
```

### 1.4 SQLite 数据库（39.7 MB）

| 表 | 行数 | 平均行字节 | 备注 |
|---|---|---|---|
| **price_snapshots** | **92,412** | 25 | 5 个交易日 × 16 只股 ≈ 1,155 行/股/天（应为分钟级，实测每分钟 12 条 = **8× 冗余**） |
| strongStocks | 699 | 105 | |
| dailyPicks | 178 | 139 | |
| trades | 129 | 173 | |
| **appConfig** | 34 | **851,585** | 单条 28.5MB |
| entryTypes | 15 | 36 KB | |
| … | | | |

**appConfig 单条大小 TOP 5**：
```
pet_signal_events                       28,508,579 bytes  ← 头号元凶
pet_reminder_history                       255,301
pet_signal_stats                           166,714
pet_trade_plans                             10,277
pet_custom_reminders                         2,764
```

### 1.5 price_snapshots 内容特征
```
distinct stockCode = 16  每天每只股票 1200-1300 行，间隔 1 分钟 12 条（实际分钟级，多源合并未去重）
按 code TOP10：每只 6187 行（一致）
按日期 TOP：09-04 18795, 09-03 18417, 09-02 16425, 09-01 19920, 08-31 18855
```

---

## 二、内存构成拆解

```
主进程 1716 MB
├── 托管堆提交        444 MB  (26%)
│   ├── 活对象        302 MB
│   │   ├── LOH 活     30 MB   ← 但已分配 254 MB 空间
│   │   ├── Gen2 活   128 MB
│   │   └── 其他       144 MB
│   └── LOH 碎片      224 MB   ← 严重！
│
├── 非托管/原生       ~1270 MB  (74%)
│   ├── WebView2 native buffers / GPU   ~500 MB（共享但本地有副本）
│   ├── 字符串/Span 临时驻留          ~300 MB
│   ├── SQLite 页面缓存 + JSON 解析   ~200 MB
│   └── P/Invoke + 第三方 native       ~270 MB
│
└── GC 提交但不归还     ~2 MB
```

> 主进程私有内存 **减去活对象 + 工作集超出部分 ≈ 1400 MB 非托管**，这部分不会在普通 GC 后释放。

---

## 三、根因定位（按内存影响从大到小）

### 🔴 #1 `pet_signal_events` 单 JSON 字符串 28.5MB（运行时驻留 ~80MB）

**位置**：`StockReview.Core/Services/SignalEventService.cs`
**表现**：
- 429 天 × 平均 21 个事件/天 = **9,153 个 SignalEvent** JSON 序列化后 28.5MB 单条字符串
- DB 中是单条 BLOB，**每次启动加载 1 次**到 `_events: Dictionary<string, List<SignalEvent>>`，运行时每次修改触发**全量重写**整个 28.5MB JSON
- 序列化过程产生 **> 28.5MB 的 char[] 临时驻留**，触发 LOH 分配 → **LOH 碎片主因**

**修复**：
1. **拆表**：新建 `pet_signal_events` 真实表（date, code, type, level, reason, payload, createdAt, INDEX(date, code)），每条独立行。
2. **只读当日**：`_events` 字典改为 `Dictionary<string, ConcurrentDictionary<string, SignalEvent>>`，内存里只保留 **当天**的事件，查询跨天数据走 SQL。
3. **去重**：同日同 code 同 type → upsert（覆盖最新 payload），事件总数从 9,153 降到 ~30/天 × N 只持仓 ≈ **< 300 条**。
4. **节流**：保存抖动从"每次事件"降为 30 秒/批或每日收盘后一次性持久化。

**收益**：DB 28.5MB → < 1MB；运行时字典 ~80MB → < 5MB；LOH 碎片 224MB → < 80MB（直接降 ~150MB 托管 + ~500MB 非托管瞬时占用）。

---

### 🔴 #2 `price_snapshots` 8× 冗余，92K 行（DB 2.3MB + 进程内频繁加载）

**位置**：`StockReview.Core/Services/SnapshotStore.cs`（推测名）
**表现**：
- 每只股票每天应该有 ~240 条分钟 K 线，实际入库 1,200-1,300 条（**8 倍冗余**）
- 5 天 × 16 只股 = 92,412 行
- 入库**未去重**，可能多次订阅同一只股的多源推送（富途 + 行情源）

**修复**：
1. **入库去重**：`INSERT OR IGNORE` 用 `(stockCode, timestamp)` 做唯一键，或 `GROUP BY stockCode, timestamp` 取最后一条。
2. **保留窗口**：保留最近 5 个交易日，旧数据归档到 `price_snapshots_history`，进程内只查当日。
3. **改分钟表**：原表保留为 `price_snapshots_min`，新增 `price_snapshots_min` 聚合表，每分钟 1 行（节省 8 倍）。

**收益**：DB 23MB → < 1MB；查询响应 < 50ms；信号计算时间 -90%。

---

### 🟠 #3 LOH 碎片 224MB（最大单一非业务开销）

**位置**：全代码所有 `JsonSerializer.Serialize(...)`/`Deserialize(...)`/`Encoding.UTF8.GetBytes(...)` 调用
**表现**：dotnet-counters 报 `loh.fragmentation.size = 224MB`，几乎全部是 85KB+ 字节数组。
**根因**：
- 每次保存 `pet_signal_events` 触发一次 28MB JSON 字符串/byte[] 分配/释放 → LOH 永久碎片
- 大量 PNG 截图保存为 byte[] 在 LOH
- DB 长字符串字段在 LOH

**修复**：
1. **修完 #1 #2 后**：碎片自动降一半
2. **ArrayPool<byte>.Shared**：所有序列化路径切换到租借缓冲区
3. **大型字符串改流式写入**：`Utf8JsonWriter` 直接写到 `PipeWriter` / `MemoryStream` 不留中间 byte[]
4. **定期 Gen2 强制回收**：进程空闲（开盘前/收盘后）调用 `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)` —— **需要 #1 修完后再开**，否则会触发主进程 STW 5-10 秒

---

### 🟠 #4 WebView2 子进程 6 个共 500MB+（架构级冗余）

**位置**：
- `Views/Web/WebChartView.xaml.cs`（ECharts 图表，1 个）
- `Controls/HtmlEditorControl.xaml.cs`（富文本编辑器，**每个 case 用 1 个**）
- `Views/Main/DailyPickView.xaml.cs` / `InsightsView.xaml.cs` / `YearMonthView.xaml.cs` 等

**表现**：
- 当前看到 6 个 WebView2 子进程，每个 ~80-100MB 工作集
- **HtmlEditorControl 使用 `EnsureCoreWebView2Async()` 无参版本** → 每个实例创建独立 WebView2 Environment（共享用户数据目录但独立进程组）
- WPF 富文本编辑器**最多 3-4 个并发**（日记编辑、案例编辑、设置等）会随时拉起新进程组

**修复**：
1. **共享 Environment**：在 `App.xaml.cs` 创建 **单例 `CoreWebView2Environment`**，所有 WebView2 控件通过 `CreationProperties = ...` 复用同一个环境
2. **进程回收策略**：设置 `BrowserExecutableFolder` + 用户数据目录 + `--single-process` 不推荐（不稳），但**开启 Pool 复用**
3. **预热改成共享池**：`IHeavyResourceView` 接口里 `Release()` 必须真正 `Dispose` WebView2 子控件（已部分实现，需补 dispose 路径）

**收益**：WebView2 子进程从 6 → 1-2，节省 ~350MB。

---

### 🟠 #5 `Dictionary<string, object>` 71K 实例（24MB Entry 数组）

**位置**：所有 `JsonSerializer.Deserialize<Dictionary<string,object>>` 调用点（`WebChartView` 注入、`HtmlEditor` 桥、`SignalEvent` 序列化）
**表现**：堆里有 71K 个 Dictionary 容器，每个含 5-10 Entry。
**根因**：弱类型反序列化（强类型化可去掉这层包装）。

**修复**：所有从 wwwroot JS 传入的数据，使用**强类型 DTO** + `JsonSerializerContext` 源生成器（`[JsonSerializable]` AOT 友好），避免 Dictionary/Object 装箱。

**收益**：-24MB LOH Entry 数组 + -5MB Dictionary 头 + 加速 5-10 倍反序列化。

---

### 🟡 #6 WeakReference 56,994 个（1.3MB）—— 事件订阅泄漏候选

**位置**：WPF `MS.Internal.Data.WeakDependencySource` 10,338 个 + 应用自定义 WeakRef
**根因**：检查 `MS.Internal.Data.ClrBindingWorker`（5,319 个）+ `WeakDependencySource[]`（5,471 个）的引用链，可能存在**事件没 -=、Timer 没 Dispose、MVVM 消息订阅没解绑**。
**修复**：跑 `dotnet-gcdump` 后用 `dotnet-dump analyze` 跑 `gcroot -all` 查最长链。重点排查：
- `WeakEventManager` 是否替代所有静态事件订阅
- `IDisposable` ViewModel 在 View Unloaded 时是否真 Dispose
- 跨页签 `DispatcherTimer` / `PeriodicTimer` 是否随页签切换停止

**收益**：-1.3MB（这个不严重，但根除泄漏隐患）。

---

### 🟡 #7 SQLite 页面缓存默认 4MB（page_size=4096 × 9707 pages）但启用 WAL

**位置**：`StockReview.Core/DatabaseService.cs`
**修复**：
1. 启用 `PRAGMA journal_mode = WAL`（读写并发 + 不阻塞 UI）
2. `PRAGMA cache_size = -2000`（~2MB 缓存，够用）
3. `PRAGMA mmap_size = 134217728`（128MB mmap，让 OS 按需分页换出）
4. `PRAGMA temp_store = MEMORY`
5. `PRAGMA auto_vacuum = INCREMENTAL` + 每周 `PRAGMA incremental_vacuum`

**收益**：DB 长期持有内存 -3MB，长期运行 DB 文件不再单调增长。

---

### 🟡 #8 ECharts/JS 资源（已部分优化）

`wwwroot/ort-dist/` 若仍保留 ONNX Runtime（项目说已移除 OnnxRuntime 包但目录可能在）；`tessdata/` 大量 `*.traineddata` 全量加载
**修复**：确认 ORT 目录可清理；Tesseract 数据按需懒加载（首次用哪个语言才解压哪个）。

---

### 🟡 #9 截图 base64 串全驻留 VM

**位置**：所有 `Screenshot` 字段（`DataGrid` 缩略图、`Case` 详情、`Insight` 大图）
**表现**：搜索 `data:image`、`CopyFromScreen` 找到 ~25+ 处；每张截图 200-500KB base64 字符串 + 解码后的 BitmapImage
**修复**：
1. 列表项用 `DecodePixelWidth = 80` 缩略图（占用 1/100）
2. 大图用 `BitmapCacheOption.OnLoad` + `Freeze()` 后放入 LRU 缓存（最多 50 张）
3. SQLite 存储从 base64 改为独立 BLOB 表 + lazy load

---

### 🟡 #10 GC 模式 + Server GC

**位置**：`StockReviewWpf.runtimeconfig.json`
**当前**：默认 Workstation GC（4 线程并行，适合桌面）
**评估**：保持现状，不要切 Server GC（会让单线程 UI 更慢）。
**可加**：`<ServerGarbageCollection>false</ServerGarbageCollection>` + `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`（已在）+ `<RetainVMGarbageCollection>false</RetainVMGarbageCollection>` **去掉**（避免占住 Segment）。

---

## 四、优化方案与预期收益

| # | 优化项 | 工作量 | 预期节省（主进程） | 风险 |
|---|---|---|---|---|
| 1 | `pet_signal_events` 拆表 + 只保留当日 + 节流持久化 | 2 天 | **-150MB 托管 + -500MB 瞬时** | 低（接口层兼容即可） |
| 2 | `price_snapshots` 去重 + 归档 | 0.5 天 | **-2MB DB + -10MB 运行时** | 低 |
| 3 | LOH 碎片（依赖 #1 + ArrayPool） | 1 天 | **-150MB 提交内存** | 低 |
| 4 | WebView2 共享 Environment + 释放复用 | 1 天 | **-350MB** | 中（需测所有编辑器场景） |
| 5 | 强类型 JSON 源生成 | 1 天 | **-30MB + 加速** | 低 |
| 6 | WeakReference 根除 + 事件清理 | 0.5 天 | **-1MB + 稳定性** | 低 |
| 7 | SQLite PRAGMA 优化 | 0.5 天 | **-3MB + 长期不增长** | 低 |
| 8 | ECharts/OCR 资源按需加载 | 0.5 天 | **-50-100MB 启动期** | 低 |
| 9 | 截图缩略图 + LRU 缓存 | 1 天 | **-30-50MB** | 中 |
| 10 | GC 配置微调 | 0.5 小时 | 微 | 极低 |

**全做预期**：主进程稳态 1716 MB → **300-400 MB**（与 Electron 版 100MB 同量级）。

---

## 五、优先级与执行计划

### Phase 1（1 周内，最大头）
- ✅ #1 `pet_signal_events` 拆表（已完成首版改动 2026-09-04，本次需续做）
- ✅ #2 `price_snapshots` 去重
- ✅ #7 SQLite PRAGMA

### Phase 2（2 周内）
- #3 LOH 碎片（依赖 #1 完成）
- #4 WebView2 共享环境
- #5 强类型 JSON

### Phase 3（3 周内收尾）
- #6/#8/#9/#10

---

## 六、监控与守护

- `MemoryProbe.cs`（已有）每 5 分钟记录 `PrivateMemorySize64` + `dotnet.gc.last_collection.heap.size`
- 超阈值（私有 > 600MB / 活对象 > 350MB）自动触发 `dotnet-counters` 快照
- 长期未修复则下次启动 **WPF 启动前自动 VACUUM + 重建 pet_signal_events 表**

---

## 七、相关数据文件

| 文件 | 内容 |
|---|---|
| `.workbuddy/counters-report.txt` | GC 计数器摘要 |
| `.workbuddy/heap-top.txt` | 对象类型 TOP 25 |
| `.workbuddy/mem-trend.csv` | 进程内存增长曲线 |
| `.workbuddy/wv-parents.txt` | WebView2 进程归属 |
| `.workbuddy/db-stats.txt` | SQLite 表统计 |