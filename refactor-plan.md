# 重构方案 · TradingAssistantWpf 巨文件拆分
**架构通（SoftwareArchitect）** · 2026-08-27 · 基于火眼眼审查报告 💭#7

---

## 一、勘察结论

| 文件 | 总行数 | 内部结构 | 自然缝 |
|------|--------|----------|--------|
| `Services/PlanSchedulerService.cs` | 4622 | 1-518 行为 **19 个模型/配置类**；519+ 为 `PlanSchedulerService : IHostedService` 单体类（约 3900 行） | 职责边界清晰：调度/检测/告警节流/快照/提醒/自进化/富途 |
| `Engines/SellPointDetectorService.cs` | 3882 | 1-403 + 3846-3882 为 **13 个模型/常量/扩展类**；中间为检测器单体类 | 指标计算/评分系统/形态检测三大块天然分离 |
| `Services/SignalEventService.cs` | 2033 | 1-1679 主类；1680-2033 为 **24 个模型类** | 写入/评估回放/统计聚合 |

## 二、ADR-001：拆分策略

**Context**：三个文件是 JS→C# 逐行翻译产物，回测已对齐、48 个测试全绿。任何语义扰动都会破坏对照基准的可信度，且测试覆盖低（1256 行）不足以兜底行为重构。

**Options**：

| 方案 | 做法 | 得到 | 放弃 |
|------|------|------|------|
| A. 纯 partial 物理拆分 | 同一类拆多个 `partial` 文件，**整块移动、零字符改动**（仅加 `partial` 关键字 + 文件头） | 零行为风险；立刻可读/可导航；git 可回退 | 耦合不变——仍是同一个类共享全部状态 |
| B. 直接提取协作服务 | 把快照存储/限流器/富途管理/自进化引擎抽成独立类 | 真解耦、可独立测试 | 需改构造/DI/调用点；行为漂移风险高；当前测试覆盖兜不住 |
| C. 分阶段（**推荐**） | Phase 1 = 方案 A（本次执行）；Phase 2 = 测试补齐后按缝真提取 | 先拿 80% 收益、零风险 | 解耦收益推迟到 Phase 2 |

**Decision**：**C 的 Phase 1**。理由：回测对齐的资产价值 > 一次性架构洁癖；reversibility 优先（partial 拆分可逐文件回退）。

**Consequences**：Phase 1 后单文件 ≤ ~2200 行（`Analyze` 主流程刻意不细分，见下）；真正解耦推迟到软件工坊补测试之后。

## 三、目标文件映射（Phase 1 执行清单）

### PlanSchedulerService（4622 → 7 个文件）
| 新文件 | 内容 | 预估行数 |
|--------|------|----------|
| `PlanSchedulerModels.cs` | 19 个模型/配置类（MonitorConfig…PetSettings）整块移出 | ~520 |
| `PlanSchedulerService.cs` | 骨架：DI/配置/状态/时区/构造/生命周期/时段调度（Handle*） | ~880 |
| `PlanSchedulerService.Checking.cs` | 信号检测路由：CheckPlanSignals/CheckTodayPlan/目标价/止损/隔夜/入场跌幅/DetectAndRoute*/Emit*Alert | ~1030 |
| `PlanSchedulerService.Snapshots.cs` | 节流冷却/波门/快照采集（Record/Save/Flush/Cleanup/EnsureSnapshotTable） | ~560 |
| `PlanSchedulerService.Reminders.cs` | 自定义提醒/闲时洞察/盘中摘要/周末总结/事件回填评估 | ~590 |
| `PlanSchedulerService.Evolution.cs` | 自进化引擎（AutoOptimizeParamsAsync ~530 行）/参数加载保存/SelfEvolution 报告 | ~850 |
| `PlanSchedulerService.Futu.cs` | 富途订阅/推送驱动检测/盘后通知状态 | ~380 |

### SellPointDetectorService（3882 → 5 个文件）
| 新文件 | 内容 | 预估行数 |
|--------|------|----------|
| `SellPointModels.cs` | 13 个模型/常量/扩展类（含尾部 SellSignal 等） | ~440 |
| `SellPointDetectorService.cs` | 骨架：构造/配置/信号乘数/计划状态/快照归一化 | ~290 |
| `SellPointDetectorService.Analyze.cs` | **Analyze 主流程 + CreateBreakSignal（~2180 行）**。对照基准 `sellPointDetector.js analyze()`，刻意保持单文件不细分，避免切坏 JS 对照关系 | ~2180 |
| `SellPointDetectorService.Indicators.cs` | ATR/RSI/WR/MFI/超买共振/市场上下文/形态几何（FindPeaks/Slope/VWAPSlope）/PrepareAnalyzeCtx | ~520 |
| `SellPointDetectorService.Scoring.cs` | 权重/去重/时间密度/EvaluateSignals/动量确认/腿部量能校验 | ~460 |

### SignalEventService（2033 → 4 个文件）
| 新文件 | 内容 | 预估行数 |
|--------|------|----------|
| `SignalEventModels.cs` | 24 个模型类（1680-2033） | ~360 |
| `SignalEventService.cs` | 骨架 + 事件写入/查询 | ~560 |
| `SignalEventService.Evaluation.cs` | 评估窗口计算/回放（未来价格窗口/时间效率/确认） | ~600 |
| `SignalEventService.Stats.cs` | 统计聚合/因子奖励/归因账本/复盘建议 | ~520 |

## 四、执行协议（零行为变更的保障）

1. **纯移动**：方法/类整块搬到新 `partial` 文件；除 `partial` 关键字与文件头（using/namespace）外**零字符改动**。
2. **逐文件验证**：每拆一个文件 → `dotnet build`（0 警告 0 错误）→ `dotnet test`（48/48）。任何一步红 → 立即回退该文件。
3. **分批提交**：每个 partial 一个 commit（`refactor: split PlanSchedulerService.Evolution.cs` 等），可单文件回退。
4. **完成验收**：`wc -l` 对照表 + 全量测试 + 与拆分前 git diff 仅含「代码块移动 + partial 关键字 + 文件头」。

## 五、Phase 2 候选（本次不做，留测试补齐后）

真提取协作服务清单（状态边界从清到浊排序）：
1. `IndicatorCalculator`（纯函数集：ATR/RSI/WR/MFI/几何 → 静态类，最安全）
2. `FutuSubscriptionManager`（独立状态机：订阅集合/推送计数/心跳）
3. `SnapshotStore`（SQLite 表自管：写/冲刷/清理）
4. `RateLimiter` / `WaveGate`（纯字典状态）
5. `SelfEvolutionEngine`（参数优化 ~850 行独立域，需先补回归测试）

## 六、ADR-002（提案，暂缓）：本地 OCR 接入
`Microsoft.ML.OnnxRuntime` 已引用未接入；OCR 走百度云（`StockOcrService`）。
**建议**：Phase 2 做「本地 PaddleOCR ONNX 模型 + 云端兜底」双通道；**代价**：需引入模型文件（~10MB）、推理代码与维护成本；**收益**：离线可用、无 API 依赖。待你确认优先级。

## 七、ADR-003（待拍板）：DateTime 时区语义
现状：存储 UTC（`"o"` 格式）+ 逻辑本地时间（`DateTime.Now` ×13 处）混用。
需你确认两个产品问题后才能统一：
1. **交易日切分**以哪个时钟为准？（建议：上海市场时钟，已有 `MarketTimeService` 雏形）
2. 历史数据（Electron 版遗留，本地时间存储）如何兼容？（建议：读侧宽容解析、写侧统一 UTC，迁移脚本可选）
