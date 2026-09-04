# 外部资源引入与借鉴分析报告

**日期**：2026-09-03
**分析对象**：富途 Skillhub ｜ FinanceMCP ｜ 今日投资数据 AI 原生 API（investoday）｜ cn-financial-mcp
**方法**：四个外部资源逐一调研（官网 + GitHub 仓库）＋ 本项目源码级能力核对（富途适配器、行情聚合链、数据库 schema、指标实现、AI 相关代码全量搜索）
**用途**：评估哪些东西值得引入（直接用）、哪些值得借鉴（抄设计），并给出分阶段落地路线

***

## 0. 结论速览

四个资源共同指向同一个方向：**金融工具的 AI 原生化（MCP 化）**。而本项目恰好是一个"数据闭环完整、但完全封闭"的桌面工具——复盘数据域、信号引擎、行情管道都很扎实，唯独没有 AI 层和外部数据广度。

**一句话定位**：最值得做的是「**输出侧 MCP 化**（把项目自己的数据暴露给 AI）＋「**输入侧数据扩域**（补资金流/新闻/基本面）」；最值得抄的是**富途的安全架构**和 **FinanceMCP 的工程细节**。

### Top 6 建议（按性价比排序）

| # | 建议                                     | 类型 | 对应缺口                   | 成本       |
| - | -------------------------------------- | -- | ---------------------- | -------- |
| 1 | 激活 FutuAdapter 中沉睡的 SDK 回调（新闻/资金流/财务）  | 引入 | 3 个空白数据域               | 极低（零新依赖） |
| 2 | 补 MACD / KDJ / BOLL 指标                 | 借鉴 | 指标体系缺 MACD（全库 grep 确认） | 低        |
| 3 | 建立项目自己的 MCP Server（官方 C# SDK）          | 引入 | 无任何 AI/MCP 集成          | 中        |
| 4 | 补资金流/龙虎榜/北向数据域（借鉴 cn-financial-mcp 端点） | 借鉴 | CapitalFlowCache 空壳    | 中        |
| 5 | LLM 复盘助手（基于 MCP Server）                | 引入 | 数据全结构化但无 AI 消费方        | 中        |
| 6 | 若未来做交易执行，照抄富途三重安全架构                    | 借鉴 | Trd 模块未接（当前只读）         | 高        |

***

## 1. 四个资源是什么

| 资源               | 形态                    | 定位                      | 许可证        | 与本项目相关度     |
| ---------------- | --------------------- | ----------------------- | ---------- | ----------- |
| 富途 Skillhub      | 官方 AI 技能商店            | 行情 + 交易 + 内容分析的"AI 完全体" | 闭源服务       | ★★★（已同管道）   |
| FinanceMCP       | TypeScript MCP Server | 多源金融数据路由层（19 工具）        | MIT        | ★★★（同构设计）   |
| investoday       | 商业数据 API              | AI 原生金融数据（200+ MCP 工具）  | 商业付费       | ★★（数据广度）    |
| cn-financial-mcp | Python MCP Server     | AKShare 免费聚合（42 工具）     | Apache-2.0 | ★★★（免费补域参照） |

### 1.1 富途 Skillhub（futunn.com/skillhub）

- 官方把 OpenD 网关能力封装成 **AI Skills**（Markdown 指令形式，配给 Claude Code / Cursor / Codex 等通用 AI 工具）。

- 能力面：港/美/A/新/日 5 大市场实时行情、**13 种订单类型**（限价/市价/止损止盈/追踪止损/TWAP/VWAP/触价/竞价/冰山等）、7 类推送回调；内容侧有**资讯搜索、个股解读、社区情绪温度计、技术面异动、资金面异动、衍生品异动**。

- **三重安全架构**：① OpenD 本地加密网关；② 交易密码本地输入、AI 上下文不可见；③ 默认模拟盘、切实盘需二次确认，另有审计日志。

- 与本项目的关系：本项目已用 futu-api NuGet 直连 OpenD（只读行情）——Skillhub 展示的正是**同一条管道加上 AI 层之后的完全体**。

### 1.2 FinanceMCP（github.com/guangxiangdebizi/FinanceMCP）

- Node.js/TypeScript 实现的 MCP Server，**19 个稳定工具**（行情/财报/基金/可转债/资金流/龙虎榜/宏观/期货等），stdio + Streamable HTTP 双传输。

- 核心设计（本项目最该抄的部分）：

  - **多数据源路由 + 自动降级**：Tushare → Twingly → Qveris → Binance，按优先级失败续传；

  - **来源标注**：每次返回都标注实际命中的数据源，发生降级时返回完整路由链（"数据源路由： Qveris（未覆盖）→ Tushare（成功）"）；

  - **按凭证动态裁剪工具列表**：`tools/list` 只展示当前凭证实际可用的工具；

  - **指标引擎的窗口扩展**：计算 MACD/RSI/KDJ/BOLL 前自动预取额外历史数据，算完再裁剪回用户请求区间；

  - 安全工程：Key 只走 Header/环境变量、日志统一 `[REDACTED]` 脱敏、AsyncLocalStorage 请求级隔离、Host 白名单校验。

- 生态位：它作为数据层，与上层 AI 文档系统 MarkiNote（FinNote）组合成"**数据基础设施 + Agent 应用**"的两层架构。

### 1.3 今日投资数据 · AI 原生金融数据 API（investoday）

- 商业服务（¥12.9 起步、按量付费），200+ MCP 工具，覆盖 A 股/港股/基金/指数/宏观/**研报/公告/产业链/大模型语料**。

- 核心理念"**AI 原生数据**"：不是把给人看的数据换个接口，而是——统一结构化 JSON、毫秒级行情、分钟级资讯入库、**语义化可解释的衍生指标**（风险因子、估值分位、个股评价），让 AI 回答"有据可依"。

- 与本项目的关系：数据域广度上是四个资源中最全的；付费是唯一门槛。其"语义化衍生指标"的设计理念对第 5 条建议（LLM 复盘助手）的**输出范式**有直接参考价值。

### 1.4 cn-financial-mcp（github.com/ccq1/cn-financial-mcp）

- Python 实现的 MCP Server，基于 **AKShare**，**42 个工具、无需 API Key、免费开箱即用**。

- 8 大模块：公司信息(4)/行情(4)/财报(8)/估值(4)/行业板块(5)/市场总览(5)/新闻公告(4)/宏观衍生(8)——含**北向资金、龙虎榜、涨跌停池、融资融券、高管增减持、分析师评级、财报日历**。

- **多源 fallback：东方财富 → 新浪/腾讯/同花顺**——与本项目的 HTTP 源降级链几乎一致（本项目：东财→腾讯→新浪）。

- 工程细节：TTL 缓存、DataFrame→JSON 格式化器、专门的"**slim tool output for LLM consumption**"输出瘦身重构（工具输出为 LLM 消费优化，而不是给人看的全量 DataFrame）。

- 与本项目的关系：**免费补齐缺失数据域的最直接参照**——东财端点、字段映射、fallback 策略都可对照抄写。

***

## 2. 本项目现状与能力边界

基于源码级调研（2026-09-03）：

**已有**（且质量不低）：

- 四源行情降级链：富途推送（链首）→ 东财 → 腾讯 → 新浪，带自定义 SourceHealth 健康追踪（成功率 <0.3 且样本 ≥10 则冷却 60s）与 LastIntradaySource 来源标注（`MarketDataAggregator.cs`）；

- 推送驱动的买卖点检测引擎：13 个指标/工具方法（ATR/MA/RSI/WR/MFI/VWAP 斜率/线性回归斜率/平台识别等），30+ 信号类型，多因子评分（`SellPointDetectorService.*` / `BuyPointDetectorService.cs`）；

- 信号事件回放与归因统计（`SignalEventService.*`，事件存 appConfig KV）；

- SQLite 复盘数据域：11 张业务表（trades 30 列含问题标签/反思/截图、patternCases、insights、dailyPicks、dailySummaries 等，`DatabaseService.cs:110-268`）+ 盘中 price\_snapshots；

- Vue 统计前端（WebView2 + DbHostObject 桥，约 30 个白名单防护的查询方法）；

- 桌面宠物提醒、OCR（百度云优先 + Tesseract 兜底）、Velopack 更新、WebDAV 同步。

**缺失**（外部资源能填的）：

- **交易执行**：富途 Trd 模块未接，纯只读行情；

- **外部数据域**：新闻、基本面、财务、资金流、龙虎榜、北向全部空白——且 `MarketDataCache` 里的 CapitalFlowCache 是**空壳**（实际拉取返回 null）；FutuAdapter 里**约 160 个 SDK 强制回调为空实现**（含 GetSearchNews、GetCapitalFlow、GetFinancialsStatements——SDK 本身支持、项目全未接）；

- **AI 层**：除 OCR 外无任何 LLM/MCP/Agent 代码（全库精确搜索 0 匹配）；

- **MACD**：全库无 MACD 计算（grep 确认），KDJ/BOLL 亦无；

- **AI 编码助手规则文件**：.trae/rules、CLAUDE.md 等均不存在；

- 小问题：Polly 在 csproj 引用了但代码未使用。

**缺口 → 资源映射表**（★ = 最佳参照）：

| 项目缺口       | 富途 Skillhub       | FinanceMCP                  | investoday      | cn-financial-mcp  |
| ---------- | ----------------- | --------------------------- | --------------- | ----------------- |
| 新闻/资讯      | ✔（SDK 回调 + skill） | ✔                           | ✔✔（研报/公告/资讯域）   | ✔                 |
| 资金流/龙虎榜/北向 | ✔（SDK 回调 + skill） | ✔（money\_flow 等工具）          | ✔               | ★（免费端点参照）         |
| 财务/基本面     | ✔（SDK 回调）         | ★（company\_performance 三件套） | ✔               | ✔                 |
| 市场情绪       | ★（情绪温度计）          | ✖                           | 部分              | ✖                 |
| 技术指标补齐     | 参考                | ★（窗口扩展模式）                   | 衍生指标理念          | ✖                 |
| AI/LLM 层   | skill 形态参考        | ★（FinNote 两层架构范式）           | ★（AI 原生理念/输出范式） | ✔（slim output 理念） |
| 交易执行安全     | ★（三重安全范本）         | ✖                           | ✖               | ✖                 |
| 降级/路由工程    | ✖                 | ★（路由+标注+动态工具）               | ✖               | ✔（东财→新浪/腾讯/同花顺）   |

***

## 3. 可引入/借鉴点逐项分析

### P0 · 低成本高收益（建议优先做）

#### A1. 激活富途 SDK 沉睡回调 → 三个新数据域零成本落地

- **缺口**：新闻、资金流、财务数据域全空白；CapitalFlowCache 是占位空壳。

- **做法**：在 `FutuAdapter.cs` 实现 GetSearchNews / GetCapitalFlow / GetFinancialsStatements 等回调。futu-api NuGet 已在引用中、OpenD TCP 管道已通（127.0.0.1:11111）、市场代码映射已就绪——**只差填实现**。

- **参照**：富途 Skillhub 的"资讯搜索/资金面异动"skill 本质就是这些 OpenD 接口的 AI 封装。

- **注意**：A 股 LV1 行情与新闻接口存在配额/权限等级限制，需实测；资金流数据可能需要更高行情权限，拿不到再走 A4 的东财 HTTP 兜底。

#### A2. 补 MACD / KDJ / BOLL 指标

- **缺口**：全库无 MACD（grep 确认）；现有 13 个指标方法缺 KDJ/BOLL。

- **做法**：借鉴 FinanceMCP 的"**自动扩展历史窗口 → 计算 → 裁剪回请求区间**"模式。MACD(12,26,9) 日线需约 40+ 根预热；项目 `CalculateDailyMA` 已有"取 N 日均价"的取数先例（`SellPointDetectorService.Indicators.cs:50`），同模式扩展即可。

- **收益**：买卖点引擎新增信号维度（MACD 背离/金叉死叉、布林突破/KDJ 超买超卖）；与富途"技术面异动"扫描（K 线形态 + MACD/KDJ/RSI 捕捉超买超卖/背离/突破）能力对齐。

- **配套**：新指标按项目惯例补进 `StockReview.Tests`（含跨语言基线对齐思路），保持 142 个用例的回归水位。

#### A3. 建立 AI 编码助手规则文件（.trae/rules）

- **缺口**：项目无任何 AI 助手规则（调研确认），但项目恰恰有强约束资产：跨语言基线测试、JsMath.JsRound 语义对齐、Core/Wpf 分层约定、partial class 拆分惯例（SellPointDetectorService 拆 4 文件、PlanSchedulerService 拆 6 文件）。

- **做法**：写明构建/测试命令（`dotnet build`、`dotnet test`——README 只写了 bat 脚本）、分层约定、测试基线要求、JS→C# 取整语义规则。

- **收益**：属于"AI 原生化"的第零步——先让 AI 能正确理解并安全修改这个项目，后面所有 MCP/AI 工作都受益。

### P1 · 战略级（中等工作量）

#### B1. 为项目构建 MCP Server（输出侧 AI 原生化）★ 本报告最核心建议

- **定位**：把项目的复盘数据域 + 信号事件暴露为 MCP 工具，让 Trae / Claude Code / Cursor 等任何 AI 直接查询："我这个月胜率怎么样？""昨天 600519 触发了什么信号？""帮我复盘这笔交易"——项目从"封闭桌面工具"升级为"**AI 可编程的个人交易数据基础设施**"。

- **技术选型**：官方 C# SDK **`ModelContextProtocol`** **2.2.0**（Microsoft 维护，modelcontextprotocol/csharp-sdk，.NET 8+，支持 stdio/HTTP 传输与 AOT；另有 `Microsoft.McpServer.ProjectTemplates` 项目模板，preview）。本项目 .NET 10，完全满足。

- **建议工具集**（读优先，10–15 个起步）：

  - `query_trades`（支持时间/标的/入场类型/问题标签过滤）

  - `get_statistics_summary`（胜率/盈亏比/分布）

  - `query_signal_events` / `get_signal_stats`（信号触发与事后归因）

  - `get_trade_plans`（当前监控计划与状态）

  - `search_pattern_cases`（形态案例检索）

  - `get_daily_summary` / `get_monthly_summary`

  - `query_strong_stocks` / `get_insights`

- **实现要点**：

  1. 新建 `StockReview.Mcp` 控制台项目，**直接引用 StockReview\.Core 的 DatabaseService**（不要绕 Wpf 层的 DbHostObject——那是 WebView2 桥，Core 层才是正路）；
  2. 借鉴 FinanceMCP 的"**按运行条件动态裁剪工具列表**"：OpenD 未运行时不暴露富途依赖工具；
  3. 借鉴 cn-financial-mcp 的"**slim output**"：工具输出为 LLM 消费裁剪（分页、字段精选），不吐全表；
  4. **只读起步**；写操作（如 AI 代记心得）后置并加表白名单（DbHostObject 已有现成白名单范式可抄）。

- **价值论证**：这正是 FinanceMCP→FinNote 验证过的两层架构（数据层 + Agent 层）——FinanceMCP 作者的贡献准则"避免一接口一 Tool 的表面扩张"同样适用于本项目：按复盘场景裁剪，不做大而全。

#### B2. 数据域扩容：资金流/龙虎榜/北向/涨跌停池（借鉴 cn-financial-mcp）

- **缺口**：这些域空白，且 CapitalFlowCache 已有占位结构。

- **做法**：cn-financial-mcp 的东财端点清单可直接对照（东财 push2 系列与本项目 `Sources.cs` 已用的接口同族）：个股资金流、板块资金流、北向净流入、龙虎榜、涨跌停池、融资融券。

- **与 A1 的关系**：A1 走富途管道（质量高、有权限门槛），B2 走东财 HTTP（免费、无门槛），**两者互为降级源**——正好嵌进现有 SourceHealth 降级框架，等于把"单指标降级链"升级为"分域多源降级链"。

- **收益**：`GetMarketContext` / `CheckVolumeAmplified` 等现有指标吃到真实资金流数据；可新增"板块共振""龙虎榜联动""北向异动"类信号——直接对齐富途"资金面异动"skill 的能力面。

#### B3. LLM 复盘助手（AI 层）

- **缺口**：无任何 LLM 集成，但数据全结构化（trades 30 列含问题标签/反思/截图路径、insights 心得、dailySummaries、信号事件回放）——**只差消费方**。

- **做法**：在 B1 的 MCP Server 之上，先不做 UI——直接在 Trae/Claude Code 里让 AI 读数据生成复盘报告（"本周亏损交易的共性问题标签是什么？"）。验证价值后再考虑应用内 AI 面板（WebView2 已有基础设施）。

- **输出范式参照**：

  - investoday 的"语义化可解释"理念——AI 复盘输出应对齐"风险因子/估值分位"式的**有据可依**格式；

  - 富途"个股解读"的输出结构（**方向研判 + 关键信号 + 原文链接**）可直接套用为"交易解读"（结论 + 触发了哪些信号 + 对应 trades/patternCases 记录）。

### P2 · 长期 / 条件触发

#### C1. 交易执行能力（若做，必抄富途三重安全架构）

- 现状：PlanScheduler 已有 ATR 止损/止盈/移动止损等**信号**，但只提醒不执行（定位是纪律工具）。

- 若要进化为执行：futu-api C# SDK 的 Trd 模块技术可行，但富途的三重安全**必须照抄**——默认模拟盘、交易密码本地输入（程序/AI 上下文不可见）、切实盘二次确认、审计日志。

- **建议**：除非强烈需求，维持"信号 + 提醒"定位。执行层引入的真金白银风险与项目"复盘纪律"的核心价值主张不匹配，风险收益比不佳。

#### C2. 付费数据接入（investoday 或 Tushare）

- 场景：需要**研报、公告、产业链、大模型语料**等深数据域时（免费方案覆盖不到的）。

- investoday ¥12.9 起步按量付费（个人可承受）；Tushare 有免费积分体系（学生认证 2000 分）。

- 优先级低于免费方案（A1/B2），建议等数据需求明确、且免费源实测不够用后再付费。

#### C3. 市场情绪维度（借鉴富途"情绪温度计"）

- 桌面宠物 + 情绪指示：把市场情绪（涨跌家数/涨停池温度/成交额分位）映射为宠物状态或面板指标——产品差异化点，且 B2 的涨跌停池数据正好是原料。

***

## 4. 不建议引入的部分

| 项                      | 来源                              | 理由                                                                                     |
| ---------------------- | ------------------------------- | -------------------------------------------------------------------------------------- |
| 公网 HTTP MCP Endpoint   | FinanceMCP 的 Streamable HTTP 模式 | 单用户桌面应用，stdio 足够；公网暴露引入 Host 校验/会话管理/认证等安全面，收益为零                                       |
| 直接依赖 AKShare Python 库  | cn-financial-mcp 底层             | .NET 进程引入 Python 运行时过重。要么抄它的东财 HTTP 端点直连（B2），要么把它当独立外部 MCP 进程用（仅开发期分析）                 |
| 富途 Skillhub 的 Skill 本体 | 富途                              | Skill 面向"通用 AI 工具 + OpenD"组合；本项目已有自己的 OpenD 管道与引擎，应**借鉴其能力清单**（新闻/情绪/资金面异动的产品定义）而非直接安装 |
| 一次性铺开 42/200+ 工具       | cn-financial-mcp / investoday   | FinanceMCP 贡献准则原话："避免一接口一 Tool 的表面扩张"。本项目 MCP Server 按复盘场景裁剪 10–15 个工具即可               |
| 交易执行（现阶段）              | 富途                              | 见 C1，风险收益比不佳                                                                           |

***

## 5. 分阶段路线图

```
Phase 1  自我补强（A1 → A2 → A3）
  激活富途沉睡回调（新闻/资金流/财务）
  → 补 MACD/KDJ/BOLL（窗口扩展模式 + 测试基线）
  → 写 .trae/rules（构建/测试命令 + 架构约定）
  产出：3 个新数据域 + 指标增强，零新架构

Phase 2  MCP 化（B1）
  StockReview.Mcp 控制台项目（官方 C# SDK，stdio）
  → 10–15 个只读工具直连 DatabaseService
  → 注册到 Trae/Claude Code/Cursor
  产出：项目成为 AI 可编程数据基础设施

Phase 3  数据与 AI（B2 + B3）
  东财 HTTP 补资金流/龙虎榜/北向（与富途源互为降级）
  → 新增资金面信号维度
  → 基于 MCP 的 AI 复盘工作流（先命令行验证，后考虑面板）
  产出：信号引擎升级 + AI 复盘闭环

Phase 4  条件触发（C1–C3）
  交易执行（须富途三重安全）/ 付费深数据 / 情绪宠物
  产出：视需求决定
```

依赖关系：B3 依赖 B1；B2 与 A1 互为补充（同域双源）；Phase 1/2 之间无依赖可并行。

***

## 6. 许可证与安全注意事项

- **FinanceMCP（MIT）**：设计模式可自由借鉴；代码是 TypeScript，需转译不可直接复用。

- **cn-financial-mcp（Apache-2.0）**：端点知识、工具分类学可自由借鉴（Python，同样借鉴而非复用）。

- **AKShare（MIT）**：若作为外部进程使用无障碍。

- **investoday**：商业服务，接入前注意服务协议与数据再分发条款。

- **富途**：OpenD 使用协议 + 行情权限等级；A 股数据有配额，新闻/资金流接口需实测权限。

- **通用安全**（FinanceMCP 的做法值得抄）：任何 API Key 走环境变量或 DPAPI（本项目已有 `CredentialProtector`），不入库、不入仓、日志脱敏 `[REDACTED]`。MCP Server 只读起步 + 表白名单（`DbHostObject.cs:25-29` 的白名单范式可直接平移）。

***

## 附：本次调研的内部事实来源

- 富途集成只读性、约 160 个空回调、市场代码映射：`StockReview.Core/Futu/FutuAdapter.cs`

- 四源降级链与来源标注：`StockReview.Core/MarketData/MarketDataAggregator.cs`、`Sources/Sources.cs`

- 7 类缓存与 CapitalFlow 占位：`StockReview.Core/MarketData/MarketDataCache.cs`

- 11 表 schema：`StockReview.Core/Data/DatabaseService.cs:110-268`

- 指标清单（13 个，无 MACD）：`StockReview.Core/Engines/SellPointDetectorService.Indicators.cs`

- 信号事件 KV 存储：`StockReview.Core/Services/SignalEventService.cs`

- WebView2 桥与白名单：`StockReviewWpf/WebBridge/DbHostObject.cs`

- 测试规模（142 用例）：`deliverables/optimization-report-2026-09-02.md`

