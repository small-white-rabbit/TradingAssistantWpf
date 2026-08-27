using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.Futu;
using StockReview.Core.MarketData;
using StockReview.Core.Services;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Main;
using StockReviewWpf.ViewModels.Pet;
using System.Runtime.InteropServices;

namespace StockReviewWpf;

/// <summary>
/// 交易助手 WPF 版 - 应用入口
/// 对应 Electron main.cjs 的 app 初始化逻辑
/// 技术选型参照《Electron → WPF 迁移技术选型方案》
/// </summary>
public partial class App : Application
{
    public static IHost? Host { get; private set; }

    /// <summary>
    /// 启动时预热的共享 WebView2 环境（拉起浏览器进程）。
    /// 内嵌 Electron 图表页的 WebChartView 复用它，避免首次导航时才冷启动浏览器进程（1-3 秒）。
    /// </summary>
    public static Microsoft.Web.WebView2.Core.CoreWebView2Environment? SharedWebView2Environment { get; private set; }
    public static string AppBaseDir { get; private set; } = "";
    public static string DataDir { get; private set; } = "";
    /// <summary>真正退出标志（对应原版 main.cjs 的 isQuitting）：置位后关闭拦截放行</summary>
    public static bool IsQuitting { get; private set; }
    /// <summary>以仅宠物模式启动（对应原版 --pet-only 自启动语义）</summary>
    public static bool PetOnlyMode { get; private set; }

    /// <summary>单实例互斥锁（对应原版 requestSingleInstanceLock）</summary>
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(2);

    public static string AppVersion => "2.0.0";
    public static string BuildDate => "2026-08-25";
    public static string AppTitle => $"交易助手 v{AppVersion} ({BuildDate})";

    /// <summary>
    /// 从 DI 容器解析必需服务（供视图层构造 VM 用）。
    /// 主机未就绪/服务未注册时抛明确异常，替代视图层 `db!` null 抹除导致的静默 NRE。
    /// </summary>
    public static T RequireService<T>() where T : notnull =>
        (Host ?? throw new InvalidOperationException("DI 主机尚未初始化")).Services.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);

        // 注册 GBK/GB2312 等代码页（新浪等行情接口返回 charset=GBK，HttpClient.GetStringAsync 解码依赖此 Provider）
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // GC 运行时调优：在关键操作后触发 Gen2 回收，降低后台调度+截图解码的长期内存驻留。
        // 对应 Electron V8 的 --max-old-space-size 效果：WPF 无等价 CLI 参数，用 GC 配置替代。
        // ServerGC + 并发回收已在 csproj 中启用。

        // 单实例锁（对应原版 main.cjs 的 requestSingleInstanceLock）：
        // 二次启动直接退出，避免多托盘/SQLite WAL 多写竞争/宠物窗口重叠
        _instanceMutex = new Mutex(true, @"Global\StockReviewWpf.SingleInstance", out var isNew);
        if (!isNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // 解析 --pet-only（对应原版开机自启仅启动宠物的语义）
        PetOnlyMode = e.Args != null && e.Args.Any(a =>
            string.Equals(a, "--pet-only", StringComparison.OrdinalIgnoreCase));

        // 初始化 Serilog（文档推荐：结构化日志，文件滚动）
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockReviewWpf", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        Log.Information("[WPF] 应用启动 v{Version}", AppVersion);

        // ScottPlot 默认字体无中文字形（图表中文全渲染成方块）。注意必须用字体族本地化中文名：
        // SkiaSharp 的 FontFamilies 列表里微软雅黑注册名是"微软雅黑"而非"Microsoft YaHei"，
        // ScottPlot 的 SystemFontResolver 按该列表精确匹配，英文名会匹配失败回退 Segoe UI
        ScottPlot.Fonts.Default = "微软雅黑";

        // 全局异常处理（对应 main.cjs 的 uncaughtException / unhandledRejection）
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // 初始化数据目录（对应 main.cjs 的 getDataDir 逻辑）
        // 文档要求：直接沿用现有 data/data.db，schema 零改动
        InitializeDataDirectory();

        // 配置 Generic Host + DI（文档推荐：统一生命周期管理）
        Host = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services);
            })
            .Build();

        // 同步数据目录到数据层并初始化建表（必须早于 Host.Start()，否则后台服务初始化会连回退空库报缺表）
        var dataService = Host.Services.GetRequiredService<StockReview.Core.Data.DatabaseService>();
        dataService.SetDataDir(DataDir);
        dataService.Initialize();

        // 同步数据目录到 ImageService（截图按日期目录解析依赖此设置）
        Host.Services.GetRequiredService<StockReview.Core.Data.ImageService>().SetDataDir(DataDir);

        // 同步启动 Host（避免 async void 把后续代码切到线程池线程导致窗口跨线程创建失败）
        Host.Start();

        // 创建主窗口（此时仍在 UI 线程，WPF 窗口创建合法）
        var mainViewModel = Host.Services.GetRequiredService<MainViewModel>();
        var mainWindow = new Views.Main.MainWindow { DataContext = mainViewModel };
        Application.Current.MainWindow = mainWindow;
        // --pet-only 模式且宠物启用时主窗保持隐藏；其余情况（含宠物已关）正常显示
        if (!PetOnlyMode || !PetSettingsStore.Load().Enabled)
        {
            mainWindow.Show();
            mainWindow.Activate();
        }

        // 关闭主窗时若宠物处于启用状态 → 取消关闭、隐藏主窗到托盘（而非退出）；否则正常退出
        // IsQuitting 置位（托盘退出/系统关机）时放行，避免永远退不出（对应原版 isQuitting）
        mainWindow.Closing += (s, ce) =>
        {
            if (!IsQuitting && PetSettingsStore.Load().Enabled)
            {
                ce.Cancel = true;
                mainWindow.Hide();
                Log.Information("[主窗] 宠物启用，关闭时隐藏到托盘");
            }
        };

        // 初始化系统托盘（必须 UI 线程，NotifyIcon 依赖消息循环）
        Host.Services.GetRequiredService<TrayService>().Initialize();

        // 后台预热 WebView2 环境（拉起浏览器进程；不阻塞启动）
        _ = PrewarmWebView2Async();

        // 若宠物处于启用状态，随主程序一并显示在桌面
        if (PetSettingsStore.Load().Enabled)
        {
            Host.Services.GetRequiredService<PetWindowManager>().ShowPet();
            Log.Information("[宠物] 已随主程序启用显示");
        }

        // 启动自定义提醒调度器（对应 customReminderScheduler.js 的 start）
        // 触发事件由 SchedulerPetStore 的 AddReminder 管线统一处理
        var customReminderScheduler = Host.Services.GetRequiredService<CustomReminderSchedulerService>();
        var petStore = Host.Services.GetRequiredService<StockReview.Core.Services.IPetStore>();
        customReminderScheduler.OnReminderTriggered += (_, reminder) =>
        {
            petStore.AddReminder(new ReminderRequest
            {
                Id = $"custom_{reminder.Id}_{DateTime.Now:yyyy-MM-dd}",
                Type = "custom_reminder",
                Level = ReminderLevel.Hint,
                Title = reminder.Title,
                Content = reminder.Content,
                StockCode = reminder.StockCode,
                StockName = reminder.StockName,
                Importance = 3,
                // 气泡按钮带原始提醒 ID（对齐 Electron 触发时注入 action.reminderId）
                Actions = (reminder.Actions != null && reminder.Actions.Count > 0
                        ? reminder.Actions
                        : CustomRemindersService.DefaultActions)
                    .Select(a => new ReminderAction
                    {
                        Type = a.Type,
                        Label = a.Label,
                        PlanIds = a.PlanIds,
                        ReminderId = reminder.Id
                    }).ToList()
            });
        };
        customReminderScheduler.Start();
        Log.Information("[自定义提醒] 调度器已启动");

        // 窗口已显示、UI 线程空闲后再创建默认视图（年月回顾），
        // 避免 MainViewModel 构造期同步创建 YearMonthView 引发 UI 线程死锁。
        // 使用 Background 优先级确保窗口渲染先于视图创建，消除首屏白闪。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            mainViewModel.InitializeDefaultView();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>预热共享 WebView2 环境：浏览器进程提前启动，首次打开内嵌图表页时免去 1-3 秒冷启动</summary>
    private static async System.Threading.Tasks.Task PrewarmWebView2Async()
    {
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
            SharedWebView2Environment = env;
            Log.Information("[WPF] WebView2 环境已预热（浏览器进程就绪）");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WPF] WebView2 环境预热失败（将在首次导航时按需创建）");
        }
    }

    /// <summary>
    /// 注册所有服务
    /// 对应 Electron 的模块加载 + Vue 的 Pinia store 注册
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // === Core 层（阶段 1：C# 类库层）===

        // 数据层 - Dapper + Microsoft.Data.Sqlite（文档推荐：data.db 零改动）
        services.AddSingleton<StockReview.Core.Data.DatabaseService>();
        services.AddSingleton<StockReview.Core.Data.ImageService>();

        // 行情数据 - HttpClient + Polly 多源降级链
        services.AddHttpClient();
        services.AddSingleton(sp =>
        {
            var agg = new MarketDataAggregator(sp.GetRequiredService<System.Net.Http.HttpClient>());
            // 分时降级链：富途轮询 → 东财 → 腾讯 → 新浪（富途订阅推送由面板层单独消费）
            agg.AddIntradaySource(new StockReview.Core.MarketData.Sources.FutuIntradaySource(
                sp.GetRequiredService<StockReview.Core.Futu.FutuAdapter>()));
            return agg;
        });

        // 富途 - 官方 C# SDK（文档推荐：去 Python 依赖）
        services.AddSingleton<FutuAdapter>();

        // 业务引擎（对应 Pinia stores）
        services.AddSingleton<PlanSchedulerService>();
        // 后台调度器（Stage 3：与上述 PlanSchedulerService 共享同一实例，随 Host 启动）
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PlanSchedulerService>());
        services.AddSingleton<SellPointDetectorService>();
        services.AddSingleton<BuyPointDetectorService>();
        services.AddSingleton<PatternSimilarityService>();
        services.AddSingleton<MultiFactorEngineService>();

        // === WPF 层（阶段 2/3：UI）===

        // 宠物服务
        services.AddSingleton<PetService>();
        // 宠物外观包管理（安装/卸载/激活，GitHub awesome-codex-pet 目录）
        services.AddSingleton(sp => new PetManagementService(
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            DataDir,
            sp.GetRequiredService<StockReview.Core.Data.DatabaseService>()));
        services.AddSingleton<PetWindowManager>();

        // 交易计划调度器依赖（真实实现，逐步接入）
        services.AddSingleton<StockReview.Core.Services.IMarketTimeService, StockReview.Core.Services.MarketTimeService>();
        services.AddSingleton<StockReview.Core.Services.IPetStore, SchedulerPetStore>();
        services.AddSingleton<StockReview.Core.Services.IPetSettingsStore, SchedulerPetSettingsStore>();

        // 数据源存储（Stage 4：DB 支撑，桥接既有核心服务）
        services.AddSingleton<TradePlanService>();
        services.AddSingleton<CustomRemindersService>();
        services.AddSingleton<StockReview.Core.Services.ITradePlanStore, StockReview.Core.Services.SchedulerTradePlanStore>();
        services.AddSingleton<StockReview.Core.Services.ICustomRemindersStore, StockReview.Core.Services.SchedulerCustomRemindersStore>();

        // 检测/引擎适配器（Stage 5：接真实引擎）
        services.AddSingleton<StockReview.Core.Services.ISellPointDetector, StockReview.Core.Services.SchedulerSellPointDetector>();
        services.AddSingleton<StockReview.Core.Services.IBuyPointDetector, StockReview.Core.Services.SchedulerBuyPointDetector>();
        services.AddSingleton<StockReview.Core.Services.IMultiFactorEngine, StockReview.Core.Services.SchedulerMultiFactorEngine>();

        // 信号事件存储 + 气泡调度器 + 提醒历史（Stage 6：信号统计落地、气泡显示去重接真实服务）
        services.AddSingleton<SignalEventService>();
        services.AddSingleton<BubbleSchedulerService>();
        services.AddSingleton<ReminderHistoryService>();
        services.AddSingleton<StockReview.Core.Services.ISignalEventStore, StockReview.Core.Services.SchedulerSignalEventStore>();

        // 自定义提醒调度器（对应 customReminderScheduler.js）
        services.AddSingleton<CustomReminderSchedulerService>();

        // 其他服务
        services.AddSingleton<ScreenshotService>();
        // WebDAV 云同步（对齐原版 rejectUnauthorized:false，允许自签名证书的私有服务器）
        services.AddHttpClient<WebDavSyncService>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        services.AddSingleton<BackupService>(sp => new BackupService(
            sp.GetRequiredService<StockReview.Core.Data.DatabaseService>(),
            sp.GetRequiredService<StockReview.Core.Data.ImageService>(),
            DataDir));
        // CloudSyncService 构造含 string dataDir，必须用工厂注入（同 BackupService），
        // 否则容器解析 System.String 抛 InvalidOperationException
        services.AddSingleton<CloudSyncService>(sp => new CloudSyncService(
            sp.GetRequiredService<WebDavSyncService>(),
            sp.GetRequiredService<BackupService>(),
            DataDir));
        services.AddSingleton<OpenDService>();
        services.AddSingleton<TrayService>();
        // 心得定时提醒（随 Host 启动，按设置间隔推送 insights 记录到宠物气泡）
        services.AddHostedService<InsightReminderService>();

        // 双通道 OCR：百度云端（typed client）+ 本地 Tesseract
        services.AddHttpClient<OcrService>();
        services.AddSingleton<StockOcrService>(sp => new StockOcrService(
            sp.GetRequiredService<StockReview.Core.Data.DatabaseService>(),
            sp.GetRequiredService<OcrService>()));

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<PetViewModel>();
    }

    /// <summary>
    /// 初始化数据目录
    /// 存储结构（与 Electron 版完全一致）:
    ///   /data
    ///   ├── data.db          (SQLite 数据库, WAL 模式, 直接沿用)
    ///   ├── images/          (按日期组织的截图)
    ///   │   ├── 2026-05-28/
    ///   │   └── 2026-05-29/
    ///   ├── backups/         (数据库备份)
    ///   └── pets/            (宠物精灵资源)
    /// </summary>
    private void InitializeDataDirectory()
    {
        AppBaseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 读取数据目录配置文件（对应 data-dir.json）
        var configPath = Path.Combine(AppBaseDir, "data-dir.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<DataDirConfig>(json);
                if (!string.IsNullOrEmpty(config?.DataDir) && Directory.Exists(config.DataDir))
                {
                    DataDir = config.DataDir;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[配置] 读取数据目录配置失败");
            }
        }

        if (string.IsNullOrEmpty(DataDir))
        {
            DataDir = Path.Combine(AppBaseDir, "data");
        }

        // 确保目录存在
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "images"));
        Directory.CreateDirectory(Path.Combine(DataDir, "backups"));
        Directory.CreateDirectory(Path.Combine(DataDir, "pets"));

        // 播种默认宠物精灵（对应原版随包携带的 data/pets/firefly--lingxiaotian），
        // 否则全新安装首次启动精灵区空白
        SeedDefaultPet();

        Log.Information("[数据] 数据目录: {DataDir}", DataDir);
    }

    /// <summary>输出目录携带的默认精灵 → DataDir\pets；仅在缺失时写入，不覆盖用户数据</summary>
    private static void SeedDefaultPet()
    {
        const string petId = "firefly--lingxiaotian";
        try
        {
            var srcDir = Path.Combine(AppBaseDir, "Resources", "Pets", petId);
            if (!Directory.Exists(srcDir)) return;

            var dstDir = Path.Combine(DataDir, "pets", petId);
            if (File.Exists(Path.Combine(dstDir, "spritesheet.png"))) return;

            Directory.CreateDirectory(dstDir);
            foreach (var file in Directory.GetFiles(srcDir))
                File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)), overwrite: true);
            Log.Information("[宠物] 已播种默认精灵 {PetId}", petId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 播种默认精灵失败");
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[WPF] 未捕获的 Dispatcher 异常");

        // 启动期（主窗口尚未创建）异常不能吞：OnStartup 中断后没有任何窗口/托盘，
        // e.Handled=true 会留下无头僵尸进程占住单实例锁 → 表现为"程序启动不了"。
        // 此路径必须让进程退出，用户可重新启动。
        if (Application.Current?.MainWindow == null && !PetOnlyMode)
        {
            Log.Fatal("[WPF] 启动期异常，进程退出（详见上方堆栈）");
            e.Handled = true;
            RequestQuit();
            return;
        }

        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("[WPF] 域未处理异常: {Exception}", e.ExceptionObject);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[WPF] 未观察的 Task 异常");
        e.SetObserved();
    }

    /// <summary>真正的退出请求（托盘菜单等调用）：置位 IsQuitting 放行 Closing 拦截</summary>
    public static void RequestQuit()
    {
        IsQuitting = true;
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsQuitting = true;
        Log.Information("[WPF] 应用退出");

        // 1) 先强制清理宠物窗口 & 气泡（WPF Popup 是独立 HWND，主窗销毁后 Popup 仍会"漂浮"
        //    直到定时器触发才关，必须在 Host.Stop / Dispose 之前先同步清理）
        try
        {
            Host?.Services.GetService<PetWindowManager>()?.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WPF] 宠物气泡退出清理失败");
        }
        // 双保险：枚举所有当前 Windows 的 Popup（含气泡、托盘菜单），强制关
        try
        {
            foreach (System.Windows.Window? win in Current.Windows)
            {
                if (win == null) continue;
                try
                {
                    var t = win.GetType();
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.FieldType == typeof(System.Windows.Controls.Primitives.Popup))
                        {
                            if (f.GetValue(win) is System.Windows.Controls.Primitives.Popup p && p.IsOpen)
                                p.IsOpen = false;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        RunCloudAutoSync();

        if (Host != null)
        {
            Host.Services.GetService<TrayService>()?.Dispose();
            // 在线程池上等待 Host 停止（含 PlanScheduler FlushSnapshotsAsync 落盘）：
            // UI 线程同步等待 + await 回投 UI 线程会构成死锁，故用 Task.Run 隔离上下文
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                Task.Run(() => Host.StopAsync(cts.Token)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[WPF] Host 停止超时/失败");
            }
            Host.Dispose();
        }

        // 释放单实例锁
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException) { /* 未持有，忽略 */ }
        _instanceMutex?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// 退出前自动云备份（对应原版 before-quit 的 cloudSync:autoSync）。
    /// 同步等待但限时，避免网络异常卡死退出流程。
    /// </summary>
    private void RunCloudAutoSync()
    {
        try
        {
            if (Host == null) return;
            var db = Host.Services.GetRequiredService<StockReview.Core.Data.DatabaseService>();
            var cfg = db.GetById("appConfig", "webdavConfig");
            if (cfg == null || !cfg.TryGetValue("value", out var v) || v == null) return;
            using var doc = System.Text.Json.JsonDocument.Parse(v.ToString()!);
            var r = doc.RootElement;
            if (!r.TryGetProperty("autoSync", out var a) || !a.GetBoolean()) return;
            if (!r.TryGetProperty("serverUrl", out var su) || string.IsNullOrEmpty(su.GetString())) return;
            if (!r.TryGetProperty("username", out var un) || string.IsNullOrEmpty(un.GetString())) return;
            if (!r.TryGetProperty("password", out var pw) || string.IsNullOrEmpty(pw.GetString())) return;
            var remotePath = r.TryGetProperty("remotePath", out var rp) && !string.IsNullOrEmpty(rp.GetString())
                ? rp.GetString()! : "/StockReviewSync/";

            var cloud = Host.Services.GetRequiredService<CloudSyncService>();
            // 在线程池上启动（内部 await 不回投 UI 线程），再限时等待，避免 UI 线程死锁
            var task = Task.Run(() => cloud.AutoSyncAsync(su.GetString()!, un.GetString()!, pw.GetString()!, remotePath));
            if (task.Wait(TimeSpan.FromSeconds(60)))
                Log.Information("[云端同步] 退出自动备份完成: {File}", task.Result.fileName);
            else
                Log.Warning("[云端同步] 退出自动备份超时，已跳过");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[云端同步] 退出自动备份失败");
        }
    }

    private class DataDirConfig
    {
        [System.Text.Json.Serialization.JsonPropertyName("dataDir")]
        public string? DataDir { get; set; }
    }
}
