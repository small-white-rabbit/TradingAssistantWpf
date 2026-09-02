# 交易助手 WPF 版

> 桌面股票复盘助手（WPF 独立项目）

## 技术栈

| 层次     | 选型                             | 说明                      |
| ------ | ------------------------------ | ----------------------- |
| 运行时    | .NET 10 (LTS)                  | 支持至 2028 年底             |
| UI 框架  | WPF + XAML                     | 主程序 + 宠物一体化             |
| MVVM   | CommunityToolkit.Mvvm          | 微软官方，源代码生成器             |
| 应用骨架   | Generic Host                   | DI、配置、日志统一生命周期          |
| 数据库    | Microsoft.Data.Sqlite + Dapper | 沿用现有 data.db，schema 零改动 |
| 图表     | ScottPlot 5                    | K线 / OHLC 高性能绘图         |
| 行情网络   | HttpClient + Polly             | 多源降级链                   |
| 富途     | futu-api (NuGet)               | 官方 C# SDK 直连 FutuOpenD  |
| 日志     | Serilog                        | 结构化日志，文件滚动              |
| AI/OCR | Tesseract                      | 本地 OCR 兜底（云端百度 OCR 优先）  |
| 打包     | Velopack                       | 单文件 exe + 增量更新          |

## 项目结构

```
stock-review-wpf/
├── StockReviewWpf.sln              # 解决方案
├── StockReview.Core/                # 核心类库（阶段 1）
│   ├── Data/                        # 数据访问层 (Dapper + SQLite)
│   │   ├── DatabaseService.cs       # 数据库服务
│   │   └── ImageService.cs          # 图片服务
│   ├── MarketData/                  # 行情数据聚合
│   │   ├── MarketDataAggregator.cs  # 多源降级链
│   │   └── Sources/                 # 东财/腾讯/新浪
│   ├── Engines/                     # 业务引擎
│   │   ├── BacktestEngineService.cs # 回测引擎
│   │   ├── SellPointDetectorService.cs  # 卖点检测
│   │   ├── BuyPointDetectorService.cs   # 买点检测
│   │   ├── PatternSimilarityService.cs  # 形态相似度
│   │   └── MultiFactorEngineService.cs  # 多因子引擎
│   ├── Schedulers/                  # 调度器
│   │   └── PlanSchedulerService.cs  # 计划调度
│   └── Futu/                        # 富途适配器
│       └── FutuAdapter.cs
├── StockReviewWpf/                  # WPF 应用（阶段 2/3）
│   ├── App.xaml / App.xaml.cs       # 应用入口 + DI 配置
│   ├── Views/
│   │   ├── Main/                    # 主窗口 + 8 个视图
│   │   │   ├── MainWindow.xaml
│   │   │   ├── DailyPickView.xaml   # 每日选股
│   │   │   ├── InsightsView.xaml    # 洞察分析
│   │   │   ├── StatisticsView.xaml  # 统计分析
│   │   │   ├── PatternOptimizeView.xaml
│   │   │   ├── StrongStocksView.xaml
│   │   │   ├── YearMonthView.xaml
│   │   │   ├── CasesView.xaml
│   │   │   └── SettingsView.xaml
│   │   └── Pet/                     # 宠物窗口
│   │       ├── PetWindow.xaml       # 透明置顶窗口
│   │       └── Panels/              # 宠物面板
│   ├── ViewModels/
│   │   ├── Main/
│   │   │   └── MainViewModel.cs
│   │   └── Pet/
│   │       └── PetViewModel.cs
│   ├── Services/                    # WPF 层服务
│   │   ├── PetService.cs
│   │   ├── PetWindowManager.cs
│   │   ├── ScreenshotService.cs
│   │   ├── WebDavSyncService.cs
│   │   ├── OpenDService.cs
│   │   └── TrayService.cs
│   ├── Resources/
│   │   └── Styles/                  # Element Plus 风格主题
│   │       ├── Colors.xaml
│   │       └── CommonStyles.xaml
│   └── GlobalUsings.cs
├── 构建WPF.bat                       # 构建脚本
└── 启动WPF.bat                       # 启动脚本
```

## 快速开始

### 环境要求

- .NET 10 SDK (10.0.400+)

- Windows 10/11

### 构建

```batch
构建WPF.bat
```

### 运行

```batch
启动WPF.bat
```

或手动：

```bash
# 设置 PATH（如果 dotnet 不在系统 PATH 中）
set PATH=%LOCALAPPDATA%\dotnet;%PATH%

# 编译运行
dotnet run --project stock-review-wpf/StockReviewWpf/StockReviewWpf.csproj
```

## 数据兼容性

直接沿用原有 `data/data.db` 数据文件，schema 零改动。
WAL 模式保持不变（避免多进程同时写）。

## 迁移进度

- [x] 项目结构搭建

- [x] Core 类库层骨架

- [x] WPF 应用层骨架

- [x] 主窗口 + 导航

- [x] 宠物透明窗口 + Win32 点击穿透

- [x] 系统托盘

- [x] 行情聚合器（6 数据源降级链）

- [x] 编译通过

- [x] SQLite schema 完整翻译（`Data/DatabaseService.cs`，1368 行，WAL + 自建表/索引/迁移）

- [x] planScheduler 业务逻辑翻译（`Services/PlanSchedulerService.cs`，4620 行）

- [x] sellPointDetector 业务逻辑翻译（`Engines/SellPointDetectorService.cs`，3882 行）

- [x] buyPointDetector 业务逻辑翻译（`Engines/BuyPointDetectorService.cs`，851 行）

- [x] 宠物精灵动画系统（`PetSpriteControl` 精灵动画 + `PetWindow` 动画/点击穿透）

- [x] 8 个视图完整 UI（实际 9 个视图，`.xaml` 合计约 7180 行：`DailyPick`/`Insights`/`Statistics`/`PatternOptimize`/`StrongStocks`/`YearMonth`/`Cases`/`Settings`/`TradeForm`）

- \[\~] ECharts → ScottPlot 图表迁移（ScottPlot 5 已用于 K 线/分时/图表：`ChartTheme.cs`、`ChartAnimations.cs`、`IntradayChartPanel`；但 `StatisticsView` 仍走 `WebView2 + ECharts` 渲染）

- [x] 截图/WebDAV/OCR 功能迁移（`ScreenshotService` / `WebDavSyncService` / `StockOcrService` 已落地；OCR 走**百度云优先 + Tesseract 本地兜底**双通道，`Microsoft.ML.OnnxRuntime` 包已移除未引用）

> 对照说明：迁移行为基准已内嵌于 `StockReview.Tests/CrossLanguageBaseline` 测试（自包含，不依赖外部仓库）。
> `Schedulers/PlanSchedulerService.cs` 仅为 2 行重定向注释，真实实现在 `Services/PlanSchedulerService.cs`。
> 截至 2026-08-27 实际核查：Core 层 `dotnet build` 0 警告 0 错误；上述进度按代码现状填写，算法翻译正确性仍需回测校验（见 `sellPointDetector` 等引擎）。

