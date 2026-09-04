# LLM 复盘助手 Skill（建议 5 / B3 交付）

> **形态**：AI 复盘工作流指令文件（非 UI）。供 Trae / Claude Code / Cursor 等支持 MCP 的 AI 客户端使用。
> **依赖**：StockReview\.Mcp（B1 交付，官方 C# SDK `ModelContextProtocol 2.2.0`，stdio 传输，15 个只读工具）。
> **验证路径**：按 B3 建议"先不做 UI——直接在 Trae/Claude Code 里让 AI 读数据生成复盘报告"，价值验证后再考虑应用内 AI 面板。
> **日期**：2026-09-04

***

## 1. 快速开始

### 1.1 注册 MCP Server（stdio）

在 AI 客户端的 MCP 配置中添加（以 Trae 为例，Claude Code 的 `claude mcp add` 同理）：

```json
{
  "mcpServers": {
    "stockreview": {
      "command": "D:\\stock-review-system\\TradingAssistantWpf\\StockReview.Mcp\\bin\\Debug\\net10.0-windows\\StockReview.Mcp.exe",
      "env": {
        "STOCKREVIEW_DATA_DIR": "C:\\Users\\YH\\AppData\\Local\\TradingAssistantWpf\\data"
      }
    }
  }
}
```

**数据目录解析规则**（DataDirectoryResolver，优先级从高到低）：

1. 环境变量 `STOCKREVIEW_DATA_DIR`（不存在会直接抛错，最可靠）；
2. `{exe 目录}/data-dir.json` 中的 `DataDir` 字段（安装版走 `%LocalAppData%\TradingAssistantWpf`）；
3. 兜底 `{exe 目录}/data`。

上方示例即本机实测可用的注册配置（2026-09-04）：`command` 直接用已构建 exe（避免 `dotnet run` 的 MSBuild 输出污染 stdout 的风险；源码更新后需重新构建），env 指向真实数据目录 `%LOCALAPPDATA%\TradingAssistantWpf\data`——本机已另设用户级环境变量 `STOCKREVIEW_DATA_DIR`（新启动的 Trae 进程自动继承，故 env 段可省略，显式写出更可靠）。**正式发布版**只需把 `command` 换成 Velopack 安装目录的 `StockReview.Mcp.exe`、去掉 env（自动解析）。

**注意（2026-09-04 端到端实测）**：开发目录 WPF 构建输出里的 data.db 是**空种子库**（约 139KB，无交易数据）；真实历史数据（126 笔交易、14 条心得、694 条强势股记录等，约 37MB）在安装版数据目录 `%LOCALAPPDATA%\TradingAssistantWpf\data\data.db`。上方示例 env 已指向该真实目录；若误连空库，`list_data_tables` 会返回 trades=0，stderr 的目录日志也能立刻发现。

Server 启动后 stderr 会输出 `[StockReview.Mcp] 数据目录: ...`，若看到"数据文件不存在，将以空库启动"说明目录配错了。

### 1.2 激活 Skill 指令

安装方式二选一：① 打开主程序「设置 → AI 复盘助手」，点击「复制提示词」一键获取（推荐，配套 MCP 配置一键复制、环境自检、已随安装包分发）；② 手动把本文 **第 3、4、5 节** 粘贴为 AI 客户端的系统提示 / 自定义指令 / Agent 规则（Trae：项目规则或 SOLO 配置；Claude Code：CLAUDE.md 或 skills 目录）。之后直接用自然语言提问即可，例如：

- "帮我做本周复盘"

- "本周亏损交易的共性问题标签是什么？"（B3 原文场景）

- "复盘一下 00700 最近那笔交易"

- "最近信号质量怎么样，该不该调参？"

***

## 2. 数据与工具速查

### 2.1 工具总表（15 个，5 域，全部只读）

| 域  | 工具                           | 关键参数                                                                                                                                            | 用途                                  |
| -- | ---------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------- |
| 交易 | `query_trades`               | yearMonth(YYYY-MM) / stockCode / year 前缀；limit(1-100) offset                                                                                    | 分页拉交易列表，精简字段                        |
| 交易 | `get_trade_detail`           | id                                                                                                                                              | 单笔全字段（备注/反思/跟进/长文本截断 500）           |
| 复盘 | `search_pattern_cases`       | caseType: success/fail/calibration/all；entryType；keyword（代码/名称/备注/反思）；sortBy: date\_desc/date\_asc/change\_desc/change\_asc；page pageSize(1-50) | 形态案例库检索                             |
| 复盘 | `get_daily_summaries`        | startDate endDate(yyyy-MM-dd)；summaryType: daily/weekly/monthly                                                                                 | 每日/周/月复盘日报正文                        |
| 复盘 | `get_monthly_summaries`      | limit(1-60) offset                                                                                                                              | 月度总结（按月份倒序）                         |
| 复盘 | `query_strong_stocks`        | date / yearMonth / stockCode；limit offset                                                                                                       | 强势股记录（高开价/最大涨幅/强势类型）                |
| 复盘 | `get_insights`               | pinnedOnly；limit offset                                                                                                                         | 复盘心得（置顶优先）                          |
| 统计 | `get_statistics_summary`     | yearMonth 或 year                                                                                                                                | 总览：总数/胜率/平均收益 + 入场类型分布 + 问题标签分布     |
| 统计 | `get_monthly_win_rate_stats` | months(1-24)                                                                                                                                    | 月度胜率趋势                              |
| 统计 | `get_type_win_rate_stats`    | 无                                                                                                                                               | 各入场类型胜率 + 收益分布                      |
| 统计 | `list_data_tables`           | 无                                                                                                                                               | 全部表及行数（探索数据规模，冷启动首选）                |
| 计划 | `get_trade_plans`            | planId / status 包含匹配                                                                                                                            | 监控中计划（入场理由/目标价/止损价/监控点）             |
| 信号 | `query_signal_events`        | date(yyyy-MM-dd，空=今日) stockCode；limit(1-200)                                                                                                    | 信号事件回放，含 evaluation（结果/原因/最大涨幅/奖励分） |
| 信号 | `get_signal_stats`           | days(1-60)；kind: recent/quality/factor                                                                                                          | 信号统计三视角：类型层/股票层/因子层                 |
| 信号 | `get_signal_suggestions`     | days(1-60)                                                                                                                                      | 系统自动生成的调参方向与理由                      |

### 2.2 关键字段口径

- **trades**（列表字段）：`id, tradeDate, stockCode, stockName, entryType, parentEntryType, positionStatus, caseType, changePct, maxChangePct, todayPerformance, meetExpectation, exitPrice, exitDate, totalReturn, problemTags, followUp`。盈亏判定用 `totalReturn`；`problemTags` 是共性问题分析的核心字段（多标签）；`meetExpectation` 表达是否符合预期。

- **patternCases**（列表字段）：`id, entryType, caseType, stockCode, stockName, tradeDate, totalReturn, reflection, createdAt`。`caseType` 区分成功/失败/卖出校准三类经验。

- **strongStocks**（字段）：`id, date, stockCode, stockName, highPrice, maxChangePct, strongType, relatedTradeIds`。

- **insights**（字段）：`id, recordDate, title, content, importance, isPinned, stockCode, stockName, tags, relatedCaseId, relatedCaseType`。

- 数据库共 11 张表（`list_data_tables` 可查行数）：trades / entryTypes / strongStocks / problemTags / monthlySummaries / dailySummaries / todoTemplates / dailyPicks / appConfig / patternCases / insights。信号事件由 `query_signal_events` 单独提供（不走表查询）。

***

## 3. 复盘工作流（给 AI 的指令）

> 以下为注入给 AI 的工作流定义。原则：**先取数、后分析、结论必挂数据**。

**通用前置**：首次对话或数据存疑时，先调 `list_data_tables` 确认数据规模；任何场景开始前先明确复盘时间窗（本周/本月/指定区间），所有后续调用都收敛到该窗口。

### 场景 A：周复盘（默认最高频）

触发语："帮我做本周复盘" / "本周亏损交易的共性问题标签是什么？" / "这周做得怎么样"

调用序列：

1. `query_trades(yearMonth=<当月 YYYY-MM>, limit=100)` → 按 `tradeDate` 过滤出本周（周一至今）记录；
2. 以 `totalReturn` 分桶为盈利组 / 亏损组；亏损组逐笔统计 `problemTags` 词频 → 共性问题 TOP3；
3. `get_daily_summaries(startDate=<本周一>, endDate=<今天>, summaryType="daily")` → 取每日复盘正文，对照"当时怎么想 vs 实际结果"；
4. `get_signal_stats(days=5, kind="recent")` → 信号层当周触发/成功/失败计数，判断信号体系是否与交易盈亏同步；
5. 对共性问题 TOP1 的标签，用 `search_pattern_cases(caseType="fail", keyword=<相关股票代码>)` 找历史同类失败案例佐证（判断是"偶发"还是"惯性问题"）；
6. 按第 4 节格式输出周报。

分析要点：共性问题标签必须给出**频次**（如"追高(4/5 笔亏损交易)"）；对照历史案例后明确回答"这个错误是新出现还是重复出现"。

### 场景 B：月复盘

触发语："这个月表现怎么样？" / "和上个月比有什么变化？" / "帮我写月度总结"

调用序列：

1. `get_statistics_summary(yearMonth=<YYYY-MM>)` → 当月胜率/平均收益/类型分布/问题标签分布；
2. `get_monthly_win_rate_stats(months=6)` → 在 6 个月趋势中定位当月水位；
3. `get_type_win_rate_stats()` → 入场类型维度找"拖后腿"与"贡献主力"；
4. `get_monthly_summaries(limit=3)` → 取最近 3 个月总结，对比当月是否兑现了上月行动项；
5. `get_insights(pinnedOnly=true)` → 取置顶心得，核对当月纪律执行偏差；
6. 输出：月度对比结论 + 类型分化 + 纪律执行评分。

### 场景 C：单笔交易复盘（"交易解读"，对齐富途"个股解读"结构）

触发语："复盘一下 00700 那笔交易" / "id=123 的交易问题出在哪" / "这笔为什么亏"

调用序列：

1. `query_trades(stockCode=<代码>)` → 定位目标记录拿 `id`（多笔时让用户确认或取最近一笔）；
2. `get_trade_detail(id)` → 全字段：备注、反思、跟进、完整价格轨迹；
3. `query_signal_events(date=<该笔 tradeDate>, stockCode=<代码>)` → 当日触发了什么信号、信号 evaluation 结果；
4. `search_pattern_cases(keyword=<代码>)` → 该股历史形态案例（成功/失败/校准）；
5. `query_strong_stocks(stockCode=<代码>)` → 当期是否进过强势池（入场环境佐证）；
6. 按三段式输出：**结论**（成败归因一句话）+ **关键信号**（触发信号/changePct/maxChangePct/meetExpectation 的实际轨迹）+ **记录引用**（trades id 与 patternCases id）。

### 场景 D：信号体系复盘

触发语："最近信号质量怎么样？" / "信号该不该调参？" / "哪些因子有效？"

调用序列：

1. `get_signal_stats(days=10, kind="recent")` → 各类型触发/成功/失败计数与均值；
2. `get_signal_stats(days=10, kind="quality")` → 股票层高/中/低质量信号分布；
3. `get_signal_stats(days=10, kind="factor")` → 因子奖励与判别力；
4. `get_signal_suggestions(days=10)` → 系统自动调参建议；
5. 抽查 1-2 个可疑信号：`query_signal_events(date=<该日>)` 回放细节；
6. 输出：信号健康度结论 + **对系统自动建议的逐条复核意见**（同意/反对 + 数据理由——AI 不应照单全收自动建议）。

### 场景 E：形态/模式复盘

触发语："'首板'最近成功率如何？" / "什么模式在亏钱？" / "卖出时机有什么校准经验？"

调用序列：

1. `search_pattern_cases(caseType="all", entryType=<模式名>, sortBy="change_desc")` → 该模式案例池，按收益分档；
2. `get_type_win_rate_stats()` → 统计层佐证（样本量与胜率）；
3. `query_strong_stocks(yearMonth=<YYYY-MM>)` → 当月强势股与该模式的联动关系；
4. `search_pattern_cases(caseType="calibration")` → 卖出校准经验（何时走是对的）；
5. 输出：该模式胜率画像 + 典型成功/失败案例各 1 笔对照（引用 case id）+ 卖出校准要点。

***

## 4. 输出格式规范（三段式，两个参照源的融合）

**参照**：investoday 的"语义化可解释"（每条结论有据可依）+ 富途"个股解读"（方向研判 + 关键信号 + 原文链接 → 套用为"交易解读"）。所有复盘报告统一为：

```markdown
# 复盘报告：<一句话标题>（<时间窗>）

## 一、结论（方向研判）
1-3 条，每条一句话 + 置信度（高/中/低）。
置信度依据：样本量（笔数/天数）与证据一致性。

## 二、关键信号（有据可依）
| # | 信号/数据 | 证据 | 来源 |
| --- | --- | --- | --- |
| 1 | 共性问题标签 TOP1：追高 | 出现于 4/5 笔亏损交易 | query_trades problemTags 统计 |
| 2 | ... | ... | ... |

规则：
- 语义化标签必须带频次（"追高(4)"而非"存在追高问题"）；
- 任何数值（胜率/收益/涨幅）必须来自工具返回，禁止臆测；
- 每条证据标明来源工具与字段。

## 三、记录引用（原文链接）
- trades: id=123（供人工在应用内打开该笔详情）
- patternCases: id=45（同类失败案例）
- insights: id=7（置顶纪律）
- dailySummaries: recordDate=2026-09-02（当日复盘正文）

## 四、行动项（可选，≤3 条）
下周期可执行的纪律修正，一条一个动作，可检验。
注意：本 Skill 只读，行动项落库（周报/月报/心得）由人工在 WPF 应用内完成。
```

***

## 5. 纪律与边界（给 AI 的硬约束）

1. **只读边界**：15 个工具全部只读。AI 不得声称"已写入/已修正"任何数据；报告产出物在对话或文件中，落库动作（dailySummaries weekly 写入、insights 新增）由人工完成。
2. **数据信任链**：结论只能引用工具返回的字段值。工具返回为空（total=0 或 data 空）时如实说"该时间窗无数据"，不得编造。
3. **样本量守则**：样本 < 5 笔时结论置信度一律标"低"，并提示"样本不足，仅供参考"。
4. **不越界建议**：本系统定位是"信号 + 提醒"的复盘纪律工具（不做交易执行）。报告中的行动项限于纪律/流程修正（止损设置、仓位规则、模式取舍），不输出具体买卖指令。
5. **口径对齐**：`totalReturn` 为盈亏判定口径；信号成功/失败以 evaluation 字段为准；月份过滤用 YYYY-MM，日期用 yyyy-MM-dd，与工具参数格式严格一致。

***

## 6. 验证清单

- [x] MCP 工具清单与签名：与 StockReview\.Mcp/Tools 五个文件逐一核对（15 个工具、参数名、可选值、分页上限）；

- [x] 字段口径：与 TradeTools/ReviewTools 内 ListFields 常量核对；

- [x] 数据目录解析：与 DataDirectoryResolver.cs 三级优先级核对；

- [x] 输出范式：对齐 external-resources-analysis-2026-09-03.md B3 节（investoday 语义化可解释 + 富途三段式）；

- [x] 端到端冒烟（2026-09-04）：stdio 管道发送 initialize + tools/list，Server 正确返回 15 个工具（响应 10310 字节），数据目录解析正确；

- [x] 端到端流程验证（2026-09-04）：按场景 A 周复盘调用序列，对真实库只读副本（源自 %LOCALAPPDATA%\TradingAssistantWpf\data，126 笔交易）串行执行 11 个工具调用（list\_data\_tables / query\_trades 双月 / get\_statistics\_summary / get\_daily\_summaries / get\_signal\_stats / query\_strong\_stocks / get\_insights / query\_signal\_events），数据全部真实返回，并产出符合第 4 节格式的周复盘报告（含"本周 0 笔交易"的空窗如实处理、样本量守则执行）；

- [x] STOCKREVIEW\_DATA\_DIR 配置与验证（2026-09-04）：本机已设用户级环境变量持久化指向真实数据目录；MCP server（已构建 exe）在该变量下启动，stderr 输出正确数据目录，`list_data_tables` 返回真实数据（trades=126，与端到端验证一致）；

- [ ] 真实对话验证：在 Trae 注册 stockreview server 后实际提问"本周亏损交易的共性问题标签是什么？"（首次使用时执行）。

