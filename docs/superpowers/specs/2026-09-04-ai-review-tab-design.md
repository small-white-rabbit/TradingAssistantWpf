# 设计文档：设置页「AI 复盘助手」Tab（skill 快速安装入口）

> 日期：2026-09-04
> 状态：设计已获用户批准（2026-09-04 对话确认），待 spec 评审
> 背景：B3 交付的 LLM 复盘 skill（`deliverables/llm-review-skill-2026-09-04.md`）目前安装方式为手动两步（注册 MCP JSON + 粘贴提示词），主程序无任何快速安装入口。用户确认在设置页新增「AI 复盘助手」标签，并随安装包分发。

***

## 1. 目标与范围

**目标**：在设置页新增「AI 复盘助手」Tab，把 skill 安装从"看文档手动配置"变成"三步卡片复制粘贴"；同时让 `StockReview.Mcp.exe` 与提示词资源随安装包分发，普通用户无需 dotnet SDK。

**范围内**：
- 设置页新增一个 TabItem（不动 MainWindow 导航，不新建页面/ViewModel 文件）
- csproj / pack.ps1 构建层改动（携带 Mcp.exe + 嵌入提示词资源）
- 提示词资源文件 `Resources\llm-review-prompt.md`

**范围外（明确不做）**：
- 不做应用内 AI 对话面板（B3 决策：先在 AI 客户端验证价值）
- 不自动写入 AI 客户端配置文件（各家格式不同且需重启客户端，保持复制粘贴半自动）
- 不修改 StockReview.Mcp 项目本身（不加自包含配置到其 csproj，由 pack.ps1 发布参数控制）

***

## 2. 已验证的关键事实（实施依据）

| # | 事实 | 来源 |
| --- | --- | --- |
| 1 | `App.AppBaseDir` / `App.DataDir` 全局静态属性存在 | App.xaml.cs:35-36 |
| 2 | `IDatabaseService.Count(string table)` 存在 | StockReview.Core\Data\IDatabaseService.cs:24 |
| 3 | 弹窗先例 `_dialogs.Info()`（ShowPetHelp）、导出先例 SaveFileDialog（ExportData，SettingsViewModel.cs:1107-1133） | SettingsViewModel.cs |
| 4 | Content 资源运行时读取先例：`Path.Combine(App.AppBaseDir, "Resources", "stockList.json")`（StockOcrService.cs:61），安装版同样工作 | StockOcrService.cs |
| 5 | Velopack 安装目录结构：`%LOCALAPPDATA%\StockReviewWpf\current\`，正在运行的进程路径即 `current\StockReviewWpf.exe`；current 目录下 Content 文件（update-source.json、Resources、tessdata 等）齐全 | 本机实测 2026-09-04 |
| 6 | Velopack 更新机制维持 `current` 指向最新版本 → `App.AppBaseDir\StockReview.Mcp.exe` 跨版本更新路径不变，MCP 注册一次长期有效 | 本机实测 + Velopack 机制 |
| 7 | StockReviewWpf 为自包含单文件（Release）→ 目标机可无 .NET 10 运行时；StockReview.Mcp 当前是框架依赖（FDD）→ 必须在打包时改为自包含，否则普通用户机无法启动 Mcp.exe | 两个 csproj 对比 |
| 8 | skill 文档第 3、4、5 节 = 行 91-206（第 3 节起于行 91，第 5 节末条止于行 206；提示词源，原文提取） | deliverables\llm-review-skill-2026-09-04.md |
| 9 | MCP JSON 契约：`mcpServers.stockreview.command` + `env.STOCKREVIEW_DATA_DIR`（skill 文档 1.1 节，cfg 任务端到端验证过） | skill 文档 + cfg 验证记录 |

***

## 3. 架构与改动清单（四层）

| 层 | 文件 | 改动 |
| --- | --- | --- |
| 构建层 | `StockReviewWpf\StockReviewWpf.csproj` | ① 加 `<ProjectReference Include="..\StockReview.Mcp\StockReview.Mcp.csproj" ReferenceOutputAssembly="false"/>`（仅建立构建顺序，不引入编译引用）；② 加 Debug 专用 Copy Target（AfterTargets="Build"）：把 `..\StockReview.Mcp\bin\$(Configuration)\net10.0-windows\**` 复制到 WPF 输出目录（FDD exe 及其依赖 dll 全量带过去，保证 WPF bin 里 Mcp.exe 可被 MCP 客户端直接拉起）；③ 加 `<Content Include="Resources\llm-review-prompt.md">` + CopyToOutputDirectory=PreserveNewest + ExcludeFromSingleFile=true |
| 构建层 | `pack.ps1` | 在 `dotnet publish WPF` 之后、`vpk pack` 之前新增一步：`dotnet publish StockReview.Mcp -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`，把产出的**自包含单文件** `StockReview.Mcp.exe` 落入 WPF publish 目录（普通用户机无 .NET 运行时也可启动） |
| 资源层 | `StockReviewWpf\Resources\llm-review-prompt.md`（新建） | 内容 = skill 文档第 3-5 节原文（行 91-206）+ 头部来源注释（源自 deliverables\llm-review-skill-2026-09-04.md，源更新须同步本文件） |
| UI 层 | `StockReviewWpf\Views\Main\SettingsView.xaml` | 新增 TabItem「AI 复盘助手」，插在「桌面宠物」（行 435）与「显示设置」（行 539）之间；三步卡片布局（见第 4 节） |
| 逻辑层 | `StockReviewWpf\ViewModels\Main\SettingsViewModel.cs` | 增量修改（不新建文件）：新增属性 + 5 个 RelayCommand（见第 5 节） |

**分发包体积影响**：自包含单文件 Mcp.exe 约 +30-60MB（含 .NET 运行时）。主程序 exe 已 114MB，可接受；Velopack 增量更新仅传输差异。

***

## 4. UI 设计（三步卡片）

Tab 页面自上而下：

```
┌─ 蓝色提示条（PrimaryLighterBrush 背景）──────────────────────────┐
│ 把 AI 复盘助手接入 Trae / Claude Code 等 AI 客户端，三步完成。      │
│ 数据全程只读，AI 只能查询，不能修改你的交易数据。                    │
└──────────────────────────────────────────────────────────────┘

┌─ 步骤① 注册 MCP Server ─────────────────────────────────────┐
│ 说明行：复制下方 JSON，粘贴到 AI 客户端的 MCP 配置中              │
│ ┌────────────────────────────────────────────────────────┐  │
│ │ { "mcpServers": { "stockreview": {                      │  │
│ │   "command": "<程序目录>\\StockReview.Mcp.exe",          │  │
│ │   "env": { "STOCKREVIEW_DATA_DIR": "<数据目录>" } } } }  │  │
│ └──────── 只读多行文本框（等宽字体，高度约 8 行）────────────┘  │
│ 注：<程序目录> <数据目录> 为运行时动态填充的真实值（见第 5 节）  │
│ [复制配置]（PrimaryButton）          复制成功反馈行             │
└────────────────────────────────────────────────────────────┘

┌─ 步骤② 激活复盘工作流 ──────────────────────────────────────┐
│ 说明行：把复盘指令粘贴为 AI 客户端的系统提示/自定义指令            │
│   （Trae 项目规则或 SOLO 配置；Claude Code CLAUDE.md）          │
│ [复制提示词]（PrimaryButton） [导出 Markdown]（DefaultButton）   │
│ 复制/导出成功反馈行                                             │
└────────────────────────────────────────────────────────────┘

┌─ 步骤③ 环境自检 ────────────────────────────────────────────┐
│ 状态行 1：Mcp.exe —— <存在 ✓/缺失 ✗>（<路径>）                  │
│ 状态行 2：当前数据库 <N> 笔交易（AI 将读取同一份数据）            │
│ [重新自检]（DefaultButton）                                     │
└────────────────────────────────────────────────────────────┘

[使用帮助]（TextButton）—— _dialogs.Info() 弹窗（参照 ShowPetHelp）
```

样式全部复用现有资源：CardPanel、SubtitleText、SmallLabel、PrimaryButton、DefaultButton、TextButton、反馈行 StringNotEqualsToVis 显隐模式（参照现有各 Tab 写法）。

状态行 1 缺失时用 DangerBrush 红字（参照现有警示样式），并附一句修复指引（如"开发版请先构建 StockReview.Mcp；安装版请重新安装"）。

***

## 5. 关键数据流与 ViewModel 设计

**新增属性**（CommunityToolkit.Mvvm [ObservableProperty]）：

- `McpJsonText`：构造时生成，只读展示。动态填充：

  ```csharp
  var json = new
  {
      mcpServers = new Dictionary<string, object>
      {
          ["stockreview"] = new
          {
              command = Path.Combine(App.AppBaseDir, "StockReview.Mcp.exe"),
              env = new Dictionary<string, string>
              {
                  ["STOCKREVIEW_DATA_DIR"] = App.DataDir
              }
          }
      }
  };
  McpJsonText = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
  ```

  序列化用 System.Text.Json（BCL 内置，不新增包引用）。两个路径取自 `App.AppBaseDir` / `App.DataDir` 运行时实际值——**与正在运行的程序完全同一份 exe 位置与同一份数据目录**，杜绝文档写死路径过时问题（skill 文档 1.1 节示例为静态快照，本 Tab 是动态真值）。

- `McpExePath`、`McpExeExists`、`McpStatusText`：自检结果（存在性 + 路径展示）
- `TradesCountText`：`_db.Count("trades")` 的展示（"126 笔交易"），在现有 `LoadDataAsync()` 中一并加载
- 反馈行属性（复制配置成功 / 复制提示词成功 / 导出成功），参照现有 Tab 的反馈行模式

**新增 RelayCommand（5 个）**：

| 命令 | 行为 |
| --- | --- |
| `CopyMcpJsonCommand` | `Clipboard.SetText(McpJsonText)`，try/catch，成功置反馈行 + 短延时清除 |
| `CopyPromptCommand` | `File.ReadAllText(Path.Combine(App.AppBaseDir, "Resources", "llm-review-prompt.md"))` → 剪贴板；文件缺失时走错误分支（见第 6 节） |
| `ExportPromptCommand` | SaveFileDialog（Filter="Markdown 文件|*.md"，FileName=`llm-review-prompt.md`，参照 ExportData 行 1110 写法）→ `File.WriteAllText` |
| `ShowAiHelpCommand` | `_dialogs.Info()` 多行帮助弹窗（三步图文说明 + 各客户端粘贴位置指引 + 常见问题，参照 ShowPetHelp 行 611-619） |
| `RunAiCheckCommand` | 重跑存在性检查 + `_db.Count("trades")`，刷新状态行 |

**构建产物与运行形态对照**：

| 场景 | Mcp.exe 形态 | 位置 | 能否启动 |
| --- | --- | --- | --- |
| Debug（VS F5） | FDD（依赖 SDK 机器） | `bin\Debug\net10.0-windows\`（Copy Target 全量携带 dll） | 开发机可 |
| 安装版（pack.ps1） | 自包含单文件 | `%LOCALAPPDATA%\StockReviewWpf\current\` | 任意用户机可 |
| 未走 pack.ps1 的裸 Release 运行 | 不携带（Copy Target 仅 Debug） | 无 | 自检红字提示 |

***

## 6. 错误处理

| 场景 | 现象 | 处理 |
| --- | --- | --- |
| Mcp.exe 缺失 | 裸 Release 运行 / 安装损坏 / 未构建 | 自检状态行红字 + 修复指引；「复制配置」仍可用（JSON 本身无害，附警示说明） |
| 提示词资源缺失 | Content 未复制 / 被杀软误删 | 复制/导出按钮置 IsEnabled=false + 缺失说明；帮助弹窗指引重装 |
| 剪贴板被占用 | Clipboard.SetText 抛异常 | try/catch → `_dialogs.Error`（现有全局模式） |
| 导出写失败 | 目标目录只读/磁盘满 | try/catch → `_dialogs.Error("导出失败: " + ex.Message)`（与 ExportData 一致） |
| 数据库异常 | `_db.Count` 抛错 | TradesCountText 显示"读取失败"，不影响其他卡片 |

***

## 7. 风险与对策

| # | 风险 | 对策 |
| --- | --- | --- |
| 1 | 程序更新后已注册的 MCP JSON 路径失效 | 已实测（事实 5/6）：Velopack `current` 目录跨版本稳定，`App.AppBaseDir` 永远解析到 current，注册一次长期有效 |
| 2 | FDD Mcp.exe 在无 .NET 10 运行时的用户机启动失败 | pack.ps1 以 `--self-contained true -p:PublishSingleFile=true` 发布，根治；开发机有 SDK 不受影响 |
| 3 | llm-review-prompt.md 被单文件发布误 bundle 为 NativeBinary | ExcludeFromSingleFile=true 兜底（wwwroot\icon.ico 先例）；update-source.json（同为文本 Content）未加排除也正常工作，双保险 |
| 4 | skill 文档（deliverables 源）更新后资源文件漂移 | 资源头部注明来源文件与同步规则；skill 文档 1.2 节激活说明改为"从设置页导出/复制"的交叉引用 |
| 5 | Debug Copy Target 覆盖 WPF bin 中同名共享 dll（Core/Serilog/Extensions） | Copy 用 SkipUnchangedFiles=true；两项目经同一 Core 引用链解析，版本一致，即使覆盖内容相同无害 |
| 6 | 安装包体积增大 | 自包含 Mcp.exe 约 +30-60MB；Velopack 增量更新只传差异，可接受（已与用户确认"装进安装包"） |

***

## 8. 验证方案

1. **Debug 构建**：构建 Wpf → `bin\Debug\net10.0-windows\` 出现 `StockReview.Mcp.exe` + `ModelContextProtocol.dll` 等依赖 + `Resources\llm-review-prompt.md`
2. **UI 走查**：F5 运行 → 设置页 9 个 Tab（8 旧 + 1 新无回归）；新 Tab 三步卡片渲染正常、JSON 路径为本机真实值
3. **自检**：状态行显示 Mcp.exe 存在 + "126 笔交易"（与 list_data_tables 返回一致）
4. **复制 JSON → Trae 实测**：粘贴到 Trae MCP 配置 → server 启动、`list_data_tables` 返回真实数据（复用 cfg 任务已验证的流程）
5. **导出 Markdown**：导出文件与 `Resources\llm-review-prompt.md` 内容一致（diff）
6. **打包**：pack.ps1 → publish 目录出现自包含单文件 Mcp.exe → vpk 打包 → 本机安装升级 → `current\StockReview.Mcp.exe` 存在 → 新 Tab JSON 指向 current 路径 → Trae 复测启动
7. **错误路径**：临时改名 Mcp.exe 验证自检红字与指引文案

***

## 9. 实施顺序建议（供 writing-plans 参考）

1. 资源文件（新建 llm-review-prompt.md）
2. csproj（ProjectReference + Copy Target + Content）
3. ViewModel（属性 + 5 命令）
4. XAML（TabItem 三步卡片）
5. Debug 构建 + 手动走查 + 冒烟（验证方案 1-5）
6. pack.ps1 + 安装包验证（验证方案 6-7）
