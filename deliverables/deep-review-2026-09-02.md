# 深度审查报告 — 注释/命名重构后全维度检查

日期：2026-09-02
改动规模：136 文件变更（111 个跟踪文件，+646/-472 行），覆盖 Core 引擎/服务、WPF 视图/VM、测试、wwwroot 产物

---

## 一、运行验证（全部通过 ✅）

| 项目 | 结果 |
|---|---|
| 编译（Debug，全解决方案） | ✅ 0 警告 0 错误 |
| 单元测试 | ✅ 142/142 通过（含新增 DetectPlatformBreakdownTests 2 个） |
| 跨语言基线（11 个 verify_*.mjs） | ✅ 11/11 通过 |

> 环境备注：沙箱终端缺少 `PROGRAMFILES(X86)`/`APPDATA` 等标准环境变量时，dotnet 会报
> `NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`。
> 这与代码无关；日常构建（构建WPF.bat / 正常终端）不受影响。

## 二、重命名隐患专项排查（字符串耦合点）—— 0 隐患 ✅

7 类"编译器抓不到"的耦合点全部核查安全：

1. **配置序列化**：新增 `PlatformLowerPercentile`(=15)、`PlatformConfirmSnaps`(=18) 均有内联默认值，旧用户 JSON 缺键时反序列化正常；配置键名字符串未动。
2. **signal.Set 键**：`signal.Set("platformMin", segLow)` 仅变量改名，键名 `"platformMin"` 未变；新 wwwroot 产物仍在消费 `platformMin/platformBottom/platformTop` 等键，无断链。
3. **window.electronAPI 桥**：`DbHostObject.cs`/`WebChartView.xaml.cs` 仅注释改写，桥方法名与注入名完全未动。
4. **wwwroot hash 产物**：`index.html` 已切到新 hash（main-BPEl6ObF.js 等），所有被引用文件存在；源码无旧 hash 硬编码；csproj 用 `wwwroot\**\*` 通配符，新文件不会漏打包。
5. **DB 列名/表名**：`electronCompatCols→legacyCompatCols` 是局部变量，SQL 文本原样。
6. **update-source.json**：合法 JSON，`UpdateService` 用 `[JsonPropertyName("url")]` 精确匹配。
7. **XAML**：7 个 xaml 均无实改，绑定路径无风险。

## 三、逻辑正确性审查

**重要澄清**：本次唯一的实质逻辑重构 `DetectPlatformBreakdown`（跌破平台识别）位于
`SellPointDetectorService.Analyze.cs`（**卖点**侧），不在 BuyPointDetectorService（后者仅改了注释头）。
其余引擎文件（MultiFactor / PatternSimilarity / Scoring / JsCompat / MarketData）经 diff 过滤确认**全部仅注释文案变更，无行为变更**。

### DetectPlatformBreakdown 重构逐项检查 —— 无 P0

- `Percentile`（Analyze.cs:1418-1427）：rank=ceil(pct/100·n) 配 [1,n] 钳制，n=1/pct=0/pct=100 均正确，无 off-by-one。
- 索引安全：`start≥0`、`preCount=min(10,start)`、`confirmN=min(18,total)`、`segLow<=0` 有守卫，无越界/除零。
- `PlanSchedulerService.Checking.cs` 的 3 处非注释变更（moveWord/DownLabel 自定义跌词、vwap_slope_down 静音守卫、RecordMutedSignalEvent 默认参数）均符合意图，非 bug。
- 新测试 DetectPlatformBreakdownTests.cs 自洽，覆盖"台阶误报排除"与"确立平台破位触发"两场景。

### 逻辑层发现（按严重度）

**P1｜跨语言基线缺失**：`PlatformCandles` 40→180（含新增分位下轨、3 分钟确认）**没有对应的 `verify_sell_platformBreakdown_js.mjs` 基线**。该引擎是逐行翻译对齐 JS 的，此改动偏离 JS 原版窗口口径，且现有 11 条基线无法捕获该分歧。→ 建议：补一条 platform breakdown 的 JS 对齐基线，或明确确认 JS 侧无需同步（即 C# 已有意演进为独立口径）。

**P2｜开盘预热窗口拉长（2026-09-02 用户已澄清口径）**：180 拍是**固定长度回看监控窗**，并非要求 30 分钟震荡平台——平台可由窗口内较短的下沿段构成，门槛是整窗振幅 ≤ `PlatformAmplitude=1.5%` + 15 分位下轨 + 地板前置（仅看窗口前 10 拍）。残余约束：①整窗 1.5% 振幅限制意味着窗口内不能有 >1.5% 的波动（"短平台+此前拉升"组合仍会被拒）；②首个窗口需 ≥185 拍历史，开盘 ~31 分钟内该检测器无法评估（数据量守卫，Analyze.cs:1434），与平台长短无关。

**P2｜告警延迟（2026-09-02 用户澄清：预设逻辑应为"时间或幅度满足其一即可"）**：确认门在 Analyze.cs:1500-1511，当前**仅有时间尺度**（最近 18 拍全低于下轨），破位最多延迟 ~3 分钟告警；`PlatformBreakdownPct(0.25%)` 是与时间确认**串联**的前置（AND），**没有**"跌得够深免确认"的幅度分支（OR）。若按预设逻辑需要补深破即触发的幅度尺度分支（待定阈值）。

**P2｜地板前置检查的假阴性**：`preMax<segLow→continue` 会拒绝"先低位横盘、再抬升形成平台、随后破位"的形态。仅造成漏报，无风险放大。

## 四、性能 / 架构审查

**P1｜PetWindow.xaml.cs:669/685/770 — critical 气泡的遮罩残留风险**：local 路径若为 critical，`_overlaySlot = view.Slot`（="local"）被赋值，但 `HideBubble("local")` 与 `OnSlotBubbleClosed("local")` 均提前 return，未清 `_overlaySlot`/`HideOverlay`，全屏遮罩可能残留。当前 critical 只经 schedulerDriven 到达，属潜在缺陷。→ 建议：本地路径禁触发遮罩，或在两处 return 前补清理。

**P1｜SchedulerPetStore.cs:60-67 — Dispatcher null 分支跨线程风险**：`OnBubbleConsumerAttached` 在 `Application.Current?.Dispatcher` 为 null 时同步执行 `ResyncSlotViews()`，会直接在气泡调度线程操作 WPF 控件。→ 建议：null 分支改走 SynchronizationContext 或直接 return 等待下次 Resync。

**P2｜Checking.cs:1089**：`signals.Count` + `signals.All(...)` 依赖 `FilterAtrZoneTransitionSignals` 返回 List 的现状，建议显式 `ToList()` 防未来改延迟序列后双重枚举。

**P2｜PetService.cs:14-30**：自定义事件 add 访问器在订阅时同步触发 `BubbleConsumerAttached`，依赖"调度器先于宠物窗口订阅"的时序，建议补注释或改为启动主动 Resync。

### 检查过无问题的维度
- LINQ 多次枚举 / 循环内分配：Core 侧无新增问题
- async void / .Result / .Wait() / 锁范围 / ConfigureAwait：diff 无新增违规
- ObservableProperty 改名→XAML 绑定：VM 均只改注释，无属性重命名
- Loaded/Unloaded 订阅配对：无新增未配对订阅
- 架构：PlanSchedulerService partial 拆分清晰，PetWindow 提取 ShowSlotItem/AllBubbleViews/ResyncSlotViews 后职责更单一，无新增巨型方法

## 五、结论与建议行动

本次 136 文件的注释/命名重写**没有引入字符串断链类隐患**，编译、142 单测、11 条跨语言基线全绿，核心逻辑边界处理自洽。

### 后续处置结果（2026-09-02 同日完成）

| 事项 | 处置 | 结果 |
|---|---|---|
| P1 跨语言基线缺失 | ✅ **已补** `verify_sell_platformBreakdown_js.mjs`（12 号基线） | JS 复刻输出与 C# 单测断言完全对齐：S1 台阶不触发(null) / S2 确立破位触发(levelPrice=51.30, breakdownPct≈0.487) / S3 确认门控拦截(null)；README 已补使用说明 |
| P1 遮罩残留（PetWindow:669/685/770） | ✔️ **核实为误报** | 细读 `BubbleSlotView.Hide()`（1253-1258 行）：其内部**总是**回调 `_owner.OnSlotBubbleHidden(Slot)`，`HideBubble("local")`/`OnSlotBubbleClosed("local")` 均经 `.Hide()` 走该链，`_overlaySlot=="local"` 会被正确清理。仅 `Close()`（窗口关闭路径，注释已声明不走遮罩联动）例外，属预期 |
| P1 Dispatcher null 分支（SchedulerPetStore:60-67） | ✅ **已修复** | null 分支由同步 `ResyncSlotViews()` 改为记日志后跳过（此时无 UI 宿主，无槽位可补渲染，同步执行有跨线程操作控件风险） |
| P2 Checking.cs:1089 防御性 ToList | ✔️ **无需修改** | `FilterAtrZoneTransitionSignals` 参数/返回均为具体 `List<SellSignalInfo>`，非延迟枚举，无双重枚举风险 |
| P2 PetService 事件时序注释 | ✅ 已确认属设计约定 | 保持现状（补渲染机制本身即为此设计） |
| P2 三项设计取舍 | 2026-09-02 用户已澄清 | ①180 拍仅为监控回看长度，非要求 30 分钟震荡（残余约束：整窗振幅 ≤1.5%、开盘 185 拍数据量守卫）；②确认门仅时间尺度，"时间或幅度满足其一"的幅度分支待用户定阈值后补；③地板前置检查符合预设逻辑，不改 |

### 处置后验证（全部通过 ✅）
- 编译：0 警告 0 错误
- 单元测试：142/142 通过
- 跨语言基线：**12/12** 通过（含新增 platformBreakdown）
