# AI 复盘助手设置 Tab Implementation Plan

> **For agentic workers（执行本计划的零上下文工程师/AI 代理）**：本计划按任务顺序执行，禁止跳步、禁止合并相邻任务的提交。每个任务先读「Files」定位精确锚点，再逐步骤复选框执行；所有代码块均为最终内容（原样粘贴，无占位符）；每个命令给出预期输出，不符即停并排查。执行完一个任务立即提交一次。规格依据：`docs/superpowers/specs/2026-09-04-ai-review-tab-design.md`（已获用户批准）。

## Goal

在设置页新增「AI 复盘助手」Tab，把 skill 安装从"看文档手动配置"变成"三步卡片复制粘贴"；同时让 `StockReview.Mcp.exe` 与提示词资源随安装包分发，普通用户无需 dotnet SDK。

## Architecture（四层改动，对应 spec 第 3 节）

| 层 | 文件 | 改动 |
| --- | --- | --- |
| 资源层 | `StockReviewWpf\Resources\llm-review-prompt.md`（新建） | skill 文档第 3/4/5 节原文（行 91-206）+ 头部来源注释 |
| 构建层 | `StockReviewWpf\StockReviewWpf.csproj` | ProjectReference（仅构建顺序）+ Debug Copy Target + Content + ExcludeFromSingleFile |
| 构建层 | `pack.ps1` | publish WPF 之后、vpk pack 之前，新增 Mcp 自包含单文件发布到同一 publish 目录 |
| 逻辑层 | `StockReviewWpf\ViewModels\Main\SettingsViewModel.cs` | 8 个 [ObservableProperty] + 5 个 [RelayCommand]（含 TradesCount 异步加载） |
| UI 层 | `StockReviewWpf\Views\Main\SettingsView.xaml` | 新 TabItem「AI 复盘助手」插在行 536 `</TabItem>` 后；三步卡片（蓝提示条 + 注册 MCP Server + 激活提示词 + 环境自检）+ 使用帮助按钮 |

## Tech Stack

- .NET 10 WPF / `net10.0-windows`
- CommunityToolkit.Mvvm 8.3.2（`[ObservableProperty]` + `[RelayCommand]` 源生成器）
- System.Text.Json（SettingsViewModel.cs 行 7 已有 `using`，无需新增）
- Velopack 0.0.942（打包脚本 pack.ps1）
- 测试仅覆盖 Core 层（xunit）——本功能触及 UI/构建层，验证走 **Debug 构建 + F5 手动走查**，不做 TDD

---

## Task 1：新建提示词资源文件（资源层）

**Files: Create: `StockReviewWpf\Resources\llm-review-prompt.md`**

1. [ ] 前置：确认 `StockReviewWpf\Resources\` 目录存在（已有 stockList.json 等）。
2. [ ] 新建 `llm-review-prompt.md`，头部来源注释 + skill 文档第 3/4/5 节全文（见下面"最终内容"）。
3. [ ] 保存 UTF-8（含 BOM 无关紧要，中文需正确编码）。

**最终内容**（`#` 起为来源头，其后为 skill 文档行 91-206 原文）：

```
<!-- 来源：deliverables/llm-review-skill-2026-09-04.md 第 3/4/5 节（行 91-206），用于设置页"激活提示词"一键复制。
     源文档更新时必须同步本文件，否则复制内容过期。 -->

## 3. 复盘工作流（给 AI 的指令）

> 以下为注入给 AI 的工作流定义。原则：**先取数、后分析、结论必挂数据**。

**通用前置**：首次对话或数据存疑时，先调 `list_data_tables` 确认数据规模；任何场景开始前先明确复盘时间窗（本周/本月/指定区间），所有后续调用都收敛到该窗口。

### 场景 A：周复盘
- 提示词："帮我做本周复盘"
- 调用序列：`query_trades`（限定时间窗）→ `totalReturn` 分桶（盈利/亏损/打平）→ `problemTags` 词频 TOP3 → `get_daily_summaries` → `get_signal_stats` → 命中时 `search_pattern_cases`
- 输出：按第 4 节格式给出周复盘结论 + 关键信号表 + 记录引用 + 行动项（≤3 条）

### 场景 B：月复盘
- 提示词："复盘一下这个月"或"本月交易情况"
- 调用序列：`query_trades`（YYYY-MM 时间窗）→ `totalReturn` 分桶 → `problemTags` 词频 → `get_monthly_summaries` → 与上月环比（`get_signal_stats` 分周）
- 输出：月度趋势 + 本月问题标签 TOP3 + 与上月差异；证实偏差归因，不泛化

### 场景 C：单笔交易复盘
- 提示词："复盘一下 00700 最近那笔交易"（股票名/代码均可）
- 调用序列：`query_trades`（按 symbol 过滤）→ `get_trade_detail`（取出时间、price、量、方向）→ `search_pattern_cases`（同形态历史）
- 输出：三段式——当时决策逻辑 / 实际结果与偏差 / 下次同类场景的处置规则

### 场景 D：信号体系复盘
- 提示词："最近信号质量怎么样，该不该调参？"
- 调用序列：`get_signal_stats`（按 evaluation 分桶：成功/失败/待验证）→ 信号源/形态/日期的命中率交叉 → 对比近期 vs 历史基线
- 输出：信号命中率走势 + 哪些信号源/形态近期失真 + 调参建议（不输出具体买卖指令）

### 场景 E：形态模式复盘
- 提示词："看看最近有没有出现哪些值得注意的形态"或"这段时间我的交易形态分布"
- 调用序列：`query_pattern_cases`（按时间窗 + patternType 分组）→ 各形态出现频次与 outcome 分布 → 命中常用形态时对比历史胜率
- 输出：形态出现与胜率变化的对照 + 哪些形态在当前阶段盈亏比更好

### 通用兜底
- 任一场景中数据为空（total=0 或 data 空）时，如实说"该时间窗无数据"，不得编造记录或结论；样本不足 5 笔标"低"置信度。

## 4. 复盘输出模板（AI 返回的报告结构）

> 以下为 AI 面向用户的报告输出范式，字段与口径必须与工具返回一致。

**结论（1-3 条）**：每条约 1 句话，直接回答复盘问题；标置信度（高/中/低，样本 <5 笔一律"低"）。

**关键信号表**：

| # | 信号 | 数据（证据） | 数据来源（工具:字段） |
| --- | --- | --- | --- |
| 1 | 本周亏损集中在 problemTags=X | 6 笔亏损中 4 笔命中 | query_trades: problemTags |

**记录引用**：列出支撑结论的具体记录 id（trades / patternCases / insights / dailySummaries），格式 `id=T12`，便于用户在系统内核对。

**行动项（≤3 条）**：限于纪律/流程修正（止损设置、仓位规则、模式取舍），不输出具体买卖指令；每条给出可验证的验收标准。

## 5. 纪律与边界（给 AI 的硬约束）

1. **只读边界**：15 个工具全部只读。AI 不得声称"已写入/已修正"任何数据；报告产出物在对话或文件中，落库动作（dailySummaries weekly 写入、insights 新增）由人工完成。
2. **数据信任链**：结论只能引用工具返回的字段值。工具返回为空（total=0 或 data 空）时如实说"该时间窗无数据"，不得编造。
3. **样本量守则**：样本 < 5 笔时结论置信度一律标"低"，并提示"样本不足，仅供参考"。
4. **不越界建议**：本系统定位是"信号 + 提醒"的复盘纪律工具（不做交易执行）。报告中的行动项限于纪律/流程修正（止损设置、仓位规则、模式取舍），不输出具体买卖指令。
5. **口径对齐**：`totalReturn` 为盈亏判定口径；信号成功/失败以 evaluation 字段为准；月份过滤用 YYYY-MM，日期用 yyyy-MM-dd，与工具参数格式严格一致。
```

4. [ ] **验证**：`Get-Content StockReviewWpf\Resources\llm-review-prompt.md` — 应能读回全部 4 个 `## ` 小节标题与 5 条纪律。不符即停。
5. [ ] 提交：`git add StockReviewWpf/Resources/llm-review-prompt.md && git commit -m "resources(ml): 新增 AI 复盘提示词资源文件（来自 skill 文档第 3/4/5 节）"`

---

## Task 2：csproj 三处改动（构建层）

**Files: Modify: `StockReviewWpf\StockReviewWpf.csproj`**

1. [ ] **改动① 行 41 后插 ProjectReference**：

在下列现有块（行 39-42）中、`</ProjectReference>` 之后追加：

```xml
  <ItemGroup>
    <!-- 项目引用 -->
    <ProjectReference Include="..\StockReview.Core\StockReview.Core.csproj" />
    <!-- AI 复盘助手 Mcp 服务端：仅建立构建顺序，ReferenceOutputAssembly=false 不向本程序集暴露其类型 -->
    <ProjectReference Include="..\StockReview.Mcp\StockReview.Mcp.csproj" ReferenceOutputAssembly="false" />
  </ItemGroup>
```

2. [ ] **改动② 行 83-122 Content ItemGroup 内追加**（跟在行 103 tray.ico Content 之后即可）：

```xml
    <!-- AI 复盘助手提示词：设置页"激活提示词"读取源；ExcludeFromSingleFile 防止 bundle 误判为 NativeBinary 嵌入 exe -->
    <Content Include="Resources\llm-review-prompt.md">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
    </Content>
```

3. [ ] **改动③ 行 137 `</ItemGroup>` 之后、行 139 `</Project>` 之前新增 Debug Copy Target**：

```xml
  <!-- 仅 Debug：把 StockReview.Mcp 的构建产物（exe + 依赖 dll）复制进 WPF 输出目录，
       使 F5 运行时 AppBaseDir 下即存在 StockReview.Mcp.exe 供自检；ReferenceOutputAssembly=false 已保证构建顺序 -->
  <Target Name="CopyMcpToWpfOutput" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug'">
    <ItemGroup>
      <McpOutput Include="..\StockReview.Mcp\bin\$(Configuration)\net10.0-windows\**\*.*" />
    </ItemGroup>
    <Copy SourceFiles="@(McpOutput)" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

4. [ ] **验证**：
   - `dotnet build StockReviewWpf\StockReviewWpf.csproj -c Debug` — 预期 **成功**；输出目录出现 `StockReview.Mcp.exe` 及依赖 dll、`Resources\llm-review-prompt.md`。
   - 检查 `bin\Debug\net10.0-windows\` 下 `Test-Path StockReview.Mcp.exe` 应为 True。不符即停。
5. [ ] 提交：`git add StockReviewWpf/StockReviewWpf.csproj && git commit -m "build(ml): WPF 工程引用 Mcp 项目并在 Debug 时复制 Mcp 产物与提示词资源"`

---

## Task 3：SettingsViewModel 逻辑层

**Files: Modify: `StockReviewWpf\ViewModels\Main\SettingsViewModel.cs`**

1. [ ] **新增 8 个 [ObservableProperty]**（置于类内任意属性区，先例为行 28 起的 `[ObservableProperty] private string _activeTab = "";` 模式，反馈对参照 `_cloudMessage`/`_cloudMessageIsError`）：

```csharp
    // ===== AI 复盘助手 =====
    [ObservableProperty]
    private string _mcpJsonText = "";

    [ObservableProperty]
    private string _mcpExePath = "";

    [ObservableProperty]
    private bool _mcpExeExists;

    [ObservableProperty]
    private string _mcpStatusText = "";

    [ObservableProperty]
    private string _tradesCountText = "";

    [ObservableProperty]
    private string _mcpJsonFeedback = "";

    [ObservableProperty]
    private bool _mcpJsonFeedbackIsError;

    [ObservableProperty]
    private string _mcpPromptFeedback = "";

    [ObservableProperty]
    private bool _mcpPromptFeedbackIsError;

    [ObservableProperty]
    private string _mcpExportFeedback = "";

    [ObservableProperty]
    private bool _mcpExportFeedbackIsError;
```

> 属性清单说明（与 spec 的"8 个 [ObservableProperty]"对应）：最终需新增 **8 个状态属性 + 3 个错误标记 bool**。其中：
> - 5 个状态：`McpJsonText` / `McpExePath` / `McpExeExists` / `McpStatusText` / `TradesCountText`
> - 3 个反馈文本：`McpJsonFeedback` / `McpPromptFeedback` / `McpExportFeedback`
> - 3 个错误标记（bool，XAML 用 DataTrigger 控制红/绿）：`McpJsonFeedbackIsError` / `McpPromptFeedbackIsError` / `McpExportFeedbackIsError`
>
> 上面代码块按实际字段逐个列出为 11 个 `[ObservableProperty]`，即为最终实现，无占位符。每组反馈对的语义对齐现有 `_cloudMessage`/`_cloudMessageIsError`（SettingsViewModel.cs 行 60-115）。三命令行（复制 MCP / 复制提示词 / 导出）共用一行错误的 3 组反馈行各司其职，互不覆盖。

2. [ ] **构造函数内初始化 McpJsonText**（在行 187-193 `SettingsViewModel(IDatabaseService? db, IDialogService? dialogs = null)` 的 `_ = LoadDataAsync();` 之前插入）：

```csharp
            // ===== AI 复盘助手：启动时生成 MCP Server 配置 JSON（spec 第 5 节）=====
            McpExePath = System.IO.Path.Combine(App.AppBaseDir, "StockReview.Mcp.exe");
            McpExeExists = File.Exists(McpExePath);
            McpJsonText = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["stockreview"] = new
                    {
                        command = McpExePath,
                        env = new Dictionary<string, string>
                        {
                            ["STOCKREVIEW_DATA_DIR"] = App.DataDir
                        }
                    }
                }
            }, new JsonSerializerOptions { WriteIndented = true });
```

3. [ ] **LoadDataAsync 加载 TradesCount**：在行 330 `PetRunning = ...` 之后追加（后台线程查询 + UI 线程回写）：

```csharp
        // AI 复盘助手：交易笔数（环境自检用）——后台查询避免阻塞 UI
        _ = Task.Run(() =>
        {
            var n = _db.Count("trades");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                TradesCountText = $"当前库共 {n} 笔交易记录，可用于复盘样本量判断");
        });
```

4. [ ] **新增 4 个 [RelayCommand]**（参照 ExportData 行 1107-1133 / ShowPetHelp 行 611-619）：

```csharp
    [RelayCommand]
    private void CopyMcpJson()
    {
        try
        {
            System.Windows.Clipboard.SetText(McpJsonText);
            McpJsonFeedback = "已复制，可直接粘贴到 MCP 配置文件";
            McpJsonFeedbackIsError = false;
        }
        catch (Exception ex)
        {
            McpJsonFeedback = "复制失败: " + ex.Message;
            McpJsonFeedbackIsError = true;
        }
    }

    [RelayCommand]
    private async Task CopyPrompt()
    {
        var path = System.IO.Path.Combine(App.AppBaseDir, "Resources", "llm-review-prompt.md");
        try
        {
            var text = await File.ReadAllTextAsync(path);
            System.Windows.Clipboard.SetText(text);
            McpPromptFeedback = "已复制，可粘贴为 AI 客户端的系统提示 / 自定义指令 / Agent 规则";
            McpPromptFeedbackIsError = false;
        }
        catch (Exception ex)
        {
            McpPromptFeedback = "提示词读取/复制失败: " + ex.Message;
            McpPromptFeedbackIsError = true;
        }
    }

    [RelayCommand]
    private async Task ExportPrompt()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown 文件|*.md",
            Title = "导出一键安装提示词",
            FileName = "llm-review-prompt.md"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = System.IO.Path.Combine(App.AppBaseDir, "Resources", "llm-review-prompt.md");
            await File.CopyAsync(path, dlg.FileName, overwrite: true);
            McpExportFeedback = "已导出到 " + dlg.FileName;
            McpExportFeedbackIsError = false;
        }
        catch (Exception ex)
        {
            McpExportFeedback = "导出失败: " + ex.Message;
            McpExportFeedbackIsError = true;
        }
    }

    [RelayCommand]
    private async Task RunAiCheck()
    {
        McpExeExists = File.Exists(McpExePath);
        long n = await Task.Run(() => _db.Count("trades"));
        TradesCountText = $"当前库共 {n} 笔交易记录，可用于复盘样本量判断";
        if (McpExeExists)
            McpStatusText = "环境就绪：Mcp 服务端已随安装分发，trade 库 " + n + " 笔";
        else
            McpStatusText = "环境异常：未找到 StockReview.Mcp.exe（请使用官方安装包安装，勿用裸 Release 目录）";
    }

    [RelayCommand]
    private void ShowAiHelp()
    {
        _dialogs.Info(
            "使用步骤：\n" +
            "1. 复制步骤 1 的 MCP 配置，粘贴到 AI 客户端的 MCP Server 配置（Trae / Claude Code 等）\n" +
            "2. 复制步骤 2 的提示词，粘贴到 AI 客户端的系统提示 / 自定义指令 / Agent 规则\n" +
            "3. 在 AI 对话中直接提问：\"帮我做本周复盘\" / \"复盘一下 00700 最近那笔交易\" 等\n" +
            "完整说明见 deliverables\\llm-review-skill-2026-09-04.md",
            "AI 复盘助手使用帮助");
    }
```

5. [ ] `dotnet build StockReviewWpf\StockReviewWpf.csproj -c Debug` — 预期成功、无源生成器错误。不符即停排查（重点：`_db.Count("trades")` 返回 `long`，勿赋给 int）。
6. [ ] 提交：`git add StockReviewWpf/ViewModels/Main/SettingsViewModel.cs && git commit -m "feat(ml): 设置页 AI 复盘助手 ViewModel（MCP JSON 生成 + 提示词复制/导出 + 环境自检）"`

---

## Task 4：SettingsView XAML 新 Tab（UI 层）

**Files: Modify: `StockReviewWpf\Views\Main\SettingsView.xaml`**

1. [ ] 定位插入锚点：行 536 `</TabItem>`（桌面宠物 Tab 结束）之后、行 538 `<!-- ====== 显示设置 ====== -->` 之前。
2. [ ] 在锚点处插入以下完整 TabItem（样式键已由侦察确认可用：CardPanel/SubtitleText/SmallLabel/Label/PrimaryButton/DefaultButton + PrimaryLighterBrush/PrimaryLightBrush/InfoBrush/DangerBrush/SuccessBrush/TextPrimaryBrush；唯一改只读 TextBox 用属性组合而非样式键）：

```xml
                <!-- ====== AI 复盘助手 ====== -->
                <TabItem Header="AI 复盘助手">
                    <Border Style="{StaticResource CardPanel}" Margin="0,12,0,0">
                        <StackPanel>
                            <TextBlock Style="{StaticResource SubtitleText}" Text="AI 复盘助手（Skill 快捷安装）"/>
                            <TextBlock Style="{StaticResource SmallLabel}" Text="把 skill 安装从看文档改为一键复制粘贴：注册 MCP Server + 激活提示词，即可在 AI 里直接问「本周复盘」"/>
                            <Border Background="{StaticResource PrimaryLighterBrush}" BorderBrush="{StaticResource PrimaryLightBrush}"
                                    BorderThickness="1" CornerRadius="4" Padding="10" Margin="0,0,0,20">
                                <TextBlock Text="StockReview.Mcp.exe 与提示词已随安装包分发，无需 dotnet SDK；MCP 数据目录自动指向本地 data 库" FontSize="12" Foreground="{StaticResource InfoBrush}" TextWrapping="Wrap"/>
                            </Border>

                            <TextBlock Style="{StaticResource SubtitleText}" Text="步骤 1 · 注册 MCP Server" Margin="0,0,0,8"/>
                            <TextBlock Style="{StaticResource SmallLabel}" Text="复制下方 JSON，粘贴到 AI 客户端的 MCP Server 配置中（Trae / Claude Code 等）"/>
                            <TextBox Text="{Binding McpJsonText}" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" FontFamily="Consolas" Height="150" Margin="0,8,0,8"/>
                            <StackPanel Orientation="Horizontal">
                                <Button Content="复制 MCP 配置" Style="{StaticResource PrimaryButton}" Margin="0,0,8,0" Command="{Binding CopyMcpJsonCommand}"/>
                                <Button Content="使用帮助" Style="{StaticResource DefaultButton}" Command="{Binding ShowAiHelpCommand}"/>
                            </StackPanel>
                            <TextBlock Text="{Binding McpJsonFeedback}" FontSize="12" Margin="0,10,0,0" TextWrapping="Wrap">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="Foreground" Value="{StaticResource SuccessBrush}"/>
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding McpJsonFeedbackIsError}" Value="True">
                                                <Setter Property="Foreground" Value="{StaticResource DangerBrush}"/>
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>

                            <TextBlock Style="{StaticResource SubtitleText}" Text="步骤 2 · 激活提示词" Margin="0,24,0,8"/>
                            <TextBlock Style="{StaticResource SmallLabel}" Text="复制提示词，粘贴为 AI 客户端的系统提示 / 自定义指令 / Agent 规则"/>
                            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                                <Button Content="复制提示词" Style="{StaticResource DefaultButton}" Margin="0,0,8,0" Command="{Binding CopyPromptCommand}"/>
                                <Button Content="导出为 Markdown" Style="{StaticResource DefaultButton}" Command="{Binding ExportPromptCommand}"/>
                            </StackPanel>
                            <TextBlock Text="{Binding McpPromptFeedback}" FontSize="12" Margin="0,10,0,0" TextWrapping="Wrap">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="Foreground" Value="{StaticResource SuccessBrush}"/>
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding McpPromptFeedbackIsError}" Value="True">
                                                <Setter Property="Foreground" Value="{StaticResource DangerBrush}"/>
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>

                            <TextBlock Style="{StaticResource SubtitleText}" Text="步骤 3 · 环境自检" Margin="0,24,0,8"/>
                            <TextBlock Style="{StaticResource SmallLabel}" Text="确认 Mcp 服务端就绪、可读取交易库样本量"/>
                            <Grid Margin="0,8,0,8">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Text="{Binding McpStatusText}" Grid.Column="0" FontSize="13" Foreground="{StaticResource TextPrimaryBrush}" TextWrapping="Wrap" VerticalAlignment="Center"/>
                                <Button Content="重新自检" Style="{StaticResource DefaultButton}" Grid.Column="1" Margin="8,0,0,0" Command="{Binding RunAiCheckCommand}"/>
                            </Grid>
                            <TextBlock Text="{Binding TradesCountText}" Style="{StaticResource SmallLabel}" Margin="0,0,0,8"/>
                            <TextBlock Text="{Binding McpExportFeedback}" FontSize="12" Foreground="{StaticResource SuccessBrush}" Margin="0,0,0,0" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>
                </TabItem>
```

3. [ ] 验证：`dotnet build StockReviewWpf\StockReviewWpf.csproj -c Debug` — 预期成功。若绑定名与 ViewModel 字段拼写不符（如 `McpStatusText`）会报 x:Bind 类错误，逐个核对。
4. [ ] 提交：`git add StockReviewWpf/Views/Main/SettingsView.xaml && git commit -m "feat(ml): 设置页新增 AI 复盘助手 Tab（三步卡片 + 复本/导出/自检）"`

---

## Task 5：Debug 构建走查

**Files: 无（验证任务）**

1. [ ] `dotnet build StockReviewWpf\StockReviewWpf.csproj -c Debug`
   - 预期：Build succeeded；`bin\Debug\net10.0-windows\` 下出现 `StockReview.Mcp.exe`、`Resources\llm-review-prompt.md`。
2. [ ] F5 运行，进入「设置」页，确认 **9 个 Tab 均正常、无新 Tab 回归**；切到「AI 复盘助手」。
3. [ ] 对照 spec 第 8 节 1-5 步逐条核验：
   - 步骤 1 卡片展示配置 JSON 缩进正确，含 `STOCKREVIEW_DATA_DIR`；
   - 点击「复制 MCP 配置」剪贴板有内容、反馈变绿；
   - 点击「复制提示词」反馈变绿；
   - 「重新自检」后状态行随 Mcp 是否存在正确翻转、`trades` 数与本机库一致；
   - 空/异常环境（删除 `bin\Debug` 下 Mcp 再自检）出现红色异常提示。
4. [ ] 回归其他 Tab（数据管理/云存储/桌面宠物）核心功能无碍。
5. [ ] 本任务不提交（验证性）。

---

## Task 6：pack.ps1 打包含 Mcp（构建层）

**Files: Modify: `pack.ps1`**

1. [ ] 定位：行 33 `if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }` 之后、行 35 `Write-Host "==> vpk pack ..."` 之前插入：

```powershell
Write-Host "==> dotnet publish StockReview.Mcp（自包含单文件，随安装包分发）" -ForegroundColor Cyan
$McpProject = Join-Path $RepoRoot "StockReview.Mcp\StockReview.Mcp.csproj"
dotnet publish $McpProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $RepoRoot $PublishDir)
if ($LASTEXITCODE -ne 0) { throw "dotnet publish StockReview.Mcp 失败" }
```

2. [ ] 确认 `$McpProject` 指向存在的 `StockReview.Mcp\StockReview.Mcp.csproj`；确认该 csproj 的 TFM 为 `net10.0-windows`、OutputType=Exe（已侦察确认）。
3. [ ] **验证（可选但推荐）**：`dotnet publish .\StockReview.Mcp\StockReview.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $env:TEMP\mcp-test` — 预期生成 `StockReview.Mcp.exe` 单文件；异常必停。
4. [ ] 提交：`git add pack.ps1 && git commit -m "build(ml): 打包脚本在 vpk pack 前发布自包含单文件 StockReview.Mcp 进安装包"`

---

## Task 7：交叉引用文档修改（spec 风险 #4）

**Files: Modify: `deliverables\llm-review-skill-2026-09-04.md`**

1. [ ] 定位行 41-51 的 **1.2 激活 Skill 指令** 节。
2. [ ] 把行 42 原文：
   > 把本文 **第 3、4、5 节** 粘贴为 AI 客户端的系统提示 / 自定义指令 / Agent 规则（Trae：项目规则或 SOLO 配置；Claude Code：CLAUDE.md 或 skills 目录）。

   改为（增加指向设置页的交叉引用，内容不变）：
   > 安装方式二选一：① 打开主程序「设置 → AI 复盘助手」，点击「复制提示词」一键获取（推荐，配套 MCP 配置一键复制、环境自检、已随安装包分发）；② 手动把本文 **第 3、4、5 节** 粘贴为 AI 客户端的系统提示 / 自定义指令 / Agent 规则（Trae：项目规则或 SOLO 配置；Claude Code：CLAUDE.md 或 skills 目录）。
3. [ ] 提交：`git add deliverables/llm-review-skill-2026-09-04.md && git commit -m "docs(ml): skill 文档指向设置页一键安装入口（交叉引用）"`

---

## Self-review（写作自审清单）

- [ ] **spec 覆盖**：9 节全部落为 Task 1-7？（资源/构建/逻辑/UI 四层 + 打包 + 交叉引用 + 走查）
- [ ] **无占位符**：每个代码块均为最终可粘贴内容；无 `TODO`、`...`、占位路径。
- [ ] **类型一致**：`_db.Count(...)` 返回 `long`——赋值/拼接均为 long/可格式化；无 int 误用。
- [ ] **锚点行号**：csproj 行 41/83-122/137、SettingsView 行 536、pack.ps1 行 33、skill 文档行 42——与代码现状一致。
- [ ] **样式键**：仅用已确认存在的键（CardPanel/SubtitleText/SmallLabel/PrimaryButton/DefaultButton/各 Brush）。
- [ ] **提交粒度**：每任务独立一次 commit，Conventional Commits 中文描述。

## Execution Handoff（执行交接，二选一）

推荐 **Subagent-Driven**：由带本文件路径的 subagent 顺序执行 Task 1→7，每任务结束自查输出后提交。若人工 Inline 执行，按上面顺序依次进行、勿合并相邻任务提交。开启执行前无需再侦察（锚点已逐行核验）。