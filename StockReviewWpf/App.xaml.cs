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
using Velopack;

namespace StockReviewWpf;

/// <summary>
/// 交易助手 WPF 版 - 应用入口
/// 对应原版的app 初始化逻辑
/// 技术选型沿用既定迁移方案
/// </summary>
public partial class App : Application
{
    public static IHost? Host { get; private set; }

    /// <summary>
    /// 启动时预热的共享 WebView2 环境（拉起浏览器进程）。
    /// 内嵌图表页的 WebChartView 复用它，避免首次导航时才冷启动浏览器进程（1-3 秒）。
    /// </summary>
    public static Microsoft.Web.WebView2.Core.CoreWebView2Environment? SharedWebView2Environment { get; private set; }
    public static string AppBaseDir { get; private set; } = "";
    public static string DataDir { get; private set; } = "";

    /// <summary>数据目录指针文件（data-dir.json）的位置：安装版在 %LocalAppData%\StockReviewWpf，开发版在输出目录</summary>
    public static string DataDirConfigPath { get; private set; } = "";

    /// <summary>是否以 Velopack 安装版运行（current\ 下的数据会随升级被替换，需外置到 LocalAppData）</summary>
    public static bool IsVelopackInstalled { get; private set; }
    /// <summary>真正退出标志（对应原版 isQuitting）：置位后关闭拦截放行</summary>
    public static bool IsQuitting { get; private set; }
    /// <summary>以仅宠物模式启动（对应原版 --pet-only 自启动语义）</summary>
    public static bool PetOnlyMode { get; private set; }

    /// <summary>单实例互斥锁（对应原版 requestSingleInstanceLock）</summary>
    private static Mutex? _instanceMutex;
    /// <summary>二次启动信号：第二实例 Set() → 第一实例显示主窗口（用户双击图标的意图）</summary>
    private const string ShowMainEventName = @"Global\StockReviewWpf.ShowMain";
    private static System.Threading.EventWaitHandle? _showMainEvent;
    private static System.Threading.RegisteredWaitHandle? _showMainWaitHandle;

    /// <summary>
    /// 后台 Host 启动任务：把 IHostedService 初始化（DI 解析 ~400ms + 富途连接/订阅 + 首次检测 tick）
    /// 移出 UI 线程，让主窗尽快创建显示。退出时等待其完成再 StopAsync，避免 Start/Stop 生命周期竞态。
    /// </summary>
    private static System.Threading.Tasks.Task? _hostStartTask;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(2);

    // 从程序集版本动态读取（单一来源：csproj <Version>），避免 UI 硬编码与打包版本脱节（v2.2.8 教训：显示恒为 2.2.6）
    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    public static string BuildDate => "2026-09-05";
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

        // Velopack 安装/卸载/更新钩子（--squirrel-* 参数启动时执行回调后直接退出进程）。
        // 必须是 OnStartup 第一行：早于单实例锁（钩子进程不应被 Mutex 拦截）、早于日志与 UI 初始化
        VelopackApp.Build().Run();

        SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);

        // 注册 GBK/GB2312 等代码页（新浪等行情接口返回 charset=GBK，HttpClient.GetStringAsync 解码依赖此 Provider）
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // 内存探针：启动基线 + 30 分钟定时快照（ Services.MemoryProbe 类注释），定位内存去向
        Services.MemoryProbe.LogSnapshot("启动");

        // GC 运行时调优：在关键操作后触发 Gen2 回收，降低后台调度+截图解码的长期内存驻留。
        // 对应原版 Node V8 的 --max-old-space-size 效果：WPF 无等价 CLI 参数，用 GC 配置替代。
        // ServerGC + 并发回收已在 csproj 中启用。

        // 单实例锁（对应原版 requestSingleInstanceLock）：
        // 二次启动通知已有实例显示主窗口后退出，避免多托盘/SQLite WAL 多写竞争/宠物窗口重叠
        _instanceMutex = new Mutex(true, @"Global\StockReviewWpf.SingleInstance", out var isNew);
        if (!isNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            NotifyRunningInstance();
            Shutdown();
            return;
        }

        // 第一实例：监听二次启动信号（线程池回调 → 调度回 UI 线程恢复主窗）
        try
        {
            _showMainEvent = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, ShowMainEventName);
            _showMainWaitHandle = System.Threading.ThreadPool.RegisterWaitForSingleObject(
                _showMainEvent,
                (_, _) => Dispatcher.BeginInvoke(ShowMainWindowFromSecondInstance),
                null, -1, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WPF] 二次启动信号监听注册失败（不影响单实例语义）");
        }

        // 解析 --pet-only（对应原版开机自启仅启动宠物的语义）
        PetOnlyMode = e.Args != null && e.Args.Any(a =>
            string.Equals(a, "--pet-only", StringComparison.OrdinalIgnoreCase));

        // 初始化 Serilog（文档推荐：结构化日志，文件滚动）
        // 日志目录：注意不能放在 %LocalAppData%\StockReviewWpf 下——那是 Velopack 安装根（packId 保留目录），
        // 安装器修复/卸载时会整目录删除，运行中的应用锁住日志文件会导致"Failed to remove existing application directory"
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingAssistantWpf", "logs");
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

        // 全局异常处理（对应原版 uncaughtException / unhandledRejection）
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // 初始化数据目录（对应原版 getDataDir 逻辑）
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

        // 后台启动 Host：IHostedService 初始化（DI 解析 ~400ms + 富途连接/订阅 + 首次检测 tick）
        // 全部移出 UI 线程，UI 线程立即创建并显示主窗，显著缩短启动卡顿窗口。
        // 保持 OnStartup 同步签名：后续主窗创建仍在 UI 线程，避免 async void 续体切线程池致跨线程 HWND 创建失败。
        // 安全性：Host.Services 在 Build 后即可用；落地页依赖的 DatabaseService/ImageService 等已在 Start 前初始化；
        // 仅 PlanSchedulerService/InsightReminderService 两个 IHostedService 受 Host.Start 驱动，二者均后台安全、不触碰 UI 线程亲和对象。
        // CustomReminderSchedulerService 为普通类，由下方手动 Start，不随 Host 启动，无双重启动竞态。
        _hostStartTask = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await Host.StartAsync();
                Log.Information("[Host] 后台启动完成");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[Host] 后台启动失败");
            }
        });

        // 宠物启用状态：启动期读一次（Load 每次读盘+JSON 反序列化，避免重复 IO）；
        // 窗口 Closing 回调中仍实时 Load（运行时设置可能被用户切换）
        var petEnabledAtStartup = PetSettingsStore.Load().Enabled;

        // 创建主窗口（此时仍在 UI 线程，WPF 窗口创建合法）
        var mainViewModel = Host.Services.GetRequiredService<MainViewModel>();
        var mainWindow = new Views.Main.MainWindow { DataContext = mainViewModel };
        Application.Current.MainWindow = mainWindow;
        // --pet-only 模式且宠物启用时主窗保持隐藏；其余情况（含宠物已关）正常显示
        if (!PetOnlyMode || !petEnabledAtStartup)
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

        // 后台自检恢复桌面快捷方式图标（不阻塞启动；详见 RestoreDesktopShortcutIconIfNeeded 注释）
        _ = System.Threading.Tasks.Task.Run(RestoreDesktopShortcutIconIfNeeded);

        // 若宠物处于启用状态，随主程序显示在桌面。
        // 常规模式延迟 5s：宠物窗口是第二个 HwndSource + 精灵动画定时器，与主窗/落地页/
        // 共享 WebView2 环境预热并发拉起会抢占合成/渲染线程，加剧启动 30s 卡顿；
        // --pet-only 模式下主窗隐藏、宠物是唯一可见窗口，必须立即显示。
        if (petEnabledAtStartup)
        {
            var petMgr = Host.Services.GetRequiredService<PetWindowManager>();
            if (PetOnlyMode)
            {
                petMgr.ShowPet();
                Log.Information("[宠物] 已随主程序启用显示（pet-only 立即）");
            }
            else
            {
                var petTimer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Normal,
                    (s, _) =>
                    {
                        ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                        try { petMgr.ShowPet(); Log.Information("[宠物] 已随主程序启用显示（延迟 5s）"); }
                        catch (Exception ex) { Log.Warning(ex, "[宠物] 延迟显示失败"); }
                    }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                petTimer.Start();
            }
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
                // 气泡按钮带原始提醒 ID（对齐原版 触发时注入 action.reminderId）
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

        // 后台检查应用更新（Velopack：延迟 15s 避开启动高峰，静默下载应用，宠物气泡提示）
        Host.Services.GetRequiredService<UpdateService>().StartBackgroundCheck();

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

    /// <summary>桌面快捷方式文件名（与 Velopack packTitle 生成的默认快捷方式一致）</summary>
    private const string DesktopShortcutName = "交易助手.lnk";

    /// <summary>桌面快捷方式的自定义图标（Velopack 升级重建快捷方式后需恢复到此）</summary>
    private const string DesktopShortcutCustomIcon = @"D:\stock-review-system\ico.ico";

    /// <summary>
    /// 宠物快捷方式文件名（--pet-only 启动宠物，用户自建桌面快捷方式；仅存在时校正图标，不主动创建）。
    /// 图标必须是精灵图（tray.ico），与旧版桌面宠物图标对齐——
    /// 曾因图标直指 exe（双K）被用户反馈"宠物图标变成了双K"。
    /// </summary>
    private const string PetShortcutName = "宠物.lnk";

    /// <summary>
    /// 桌面快捷方式图标自检自愈：Velopack 安装/升级时会用程序内置图标重建桌面快捷方式，
    /// 覆盖自定义图标。安装器顺序是「--veloapp-install 钩子 → 重建快捷方式 → 拉起应用」，
    /// 在安装钩子里修复会被随后的重建覆盖，因此只能在应用每次启动后自检恢复。
    /// 仅当快捷方式与 ico 均存在、且当前图标指向不一致时才改写（避免每次启动都触碰 lnk）。
    /// </summary>
    private static void RestoreDesktopShortcutIconIfNeeded()
    {
        // 主程序快捷方式 → 用户指定双K图标
        RestoreShortcutIcon(DesktopShortcutName, DesktopShortcutCustomIcon);
        // 宠物快捷方式 → 安装目录精灵图（current\Resources\Images\tray.ico，current 目录跨升级稳定指向）
        RestoreShortcutIcon(PetShortcutName, Path.Combine(AppBaseDir, "Resources", "Images", "tray.ico"));
    }

    /// <summary>
    /// 校正桌面快捷方式图标：仅当 lnk 与目标 ico 均存在、且当前图标指向不一致时改写
    /// （避免每次启动都触碰 lnk；lnk 不存在时静默跳过，不主动创建）。
    /// </summary>
    private static void RestoreShortcutIcon(string shortcutName, string expectedIcon)
    {
        try
        {
            var lnkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                shortcutName);
            if (!File.Exists(lnkPath) || !File.Exists(expectedIcon))
            {
                return;
            }

            // ComImport 类到 ComImport 接口不能直接显式转换（CS0030），须经 object 中转由运行时 QI
            object shellLinkObj = new ShellLinkCom();
            var link = (IShellLinkW)shellLinkObj;
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLinkObj;
            persistFile.Load(lnkPath, 0x22); // STGM_READWRITE | STGM_SHARE_DENY_WRITE

            var iconBuf = new System.Text.StringBuilder(520);
            link.GetIconLocation(iconBuf, iconBuf.Capacity, out var iconIndex);
            var currentIcon = iconBuf.ToString();
            if (string.Equals(currentIcon, expectedIcon, StringComparison.OrdinalIgnoreCase)
                && iconIndex == 0)
            {
                return; // 已是自定义图标，无需改写
            }

            link.SetIconLocation(expectedIcon, 0);
            persistFile.Save(lnkPath, true);
            Log.Information("[快捷方式] 桌面 {Lnk} 图标已由 {Old} 恢复为 {New}",
                shortcutName,
                string.IsNullOrEmpty(currentIcon) ? "(空)" : currentIcon,
                expectedIcon);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[快捷方式] 恢复桌面快捷方式图标失败（不影响应用使用）");
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCom
    {
    }

    /// <summary>
    /// IShellLinkW（Unicode 版 Shell Link COM 接口）。
    /// 方法必须按 vtable 顺序完整声明到 SetIconLocation 为止；StringBuilder 需显式
    /// UnmanagedType.LPWStr（接口默认 CharSet.Ansi 会按 ANSI 编组导致路径乱码）。
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxArgs);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>
    /// 注册所有服务
    /// 对应原版的模块加载 + Vue 的 Pinia store 注册
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // === Core 层（阶段 1：C# 类库层）===

        // 数据层 - Dapper + Microsoft.Data.Sqlite（文档推荐：data.db 零改动）
        services.AddSingleton<StockReview.Core.Data.DatabaseService>();
        // A1 接口化：消费方统一依赖 IDatabaseService（同一单例实例）
        services.AddSingleton<StockReview.Core.Data.IDatabaseService>(
            sp => sp.GetRequiredService<StockReview.Core.Data.DatabaseService>());
        services.AddSingleton<StockReview.Core.Data.ImageService>();

        // 行情数据 - HttpClient + Polly 多源降级链
        services.AddHttpClient();
        services.AddSingleton(sp =>
        {
            var agg = new MarketDataAggregator(sp.GetRequiredService<System.Net.Http.HttpClient>());
            // 富途作为行情主源：实时快照 + 历史日K线 + 分时，OpenD 不可用时自动降级到东财/腾讯/新浪
            var futu = new StockReview.Core.MarketData.Sources.FutuIntradaySource(
                sp.GetRequiredService<StockReview.Core.Futu.FutuAdapter>());
            agg.InsertPrimarySource(futu);  // 富途置入 _sources[0]：GetQuoteAsync / GetDailyKLinesAsync 优先富途
            agg.AddIntradaySource(futu);    // 富途也参与分时降级链（_intradaySources 先于 _sources）
            return agg;
        });

        // 富途 - 官方 C# SDK（文档推荐：去 Python 依赖）
        services.AddSingleton<FutuAdapter>();
        // A2 接口化：消费方统一依赖 IFutuAdapter（同一单例实例）
        services.AddSingleton<StockReview.Core.Futu.IFutuAdapter>(
            sp => sp.GetRequiredService<FutuAdapter>());

        // 业务引擎（对应 Pinia stores）
        services.AddSingleton<PlanSchedulerService>();
        // 后台调度器（Stage 3：与上述 PlanSchedulerService 共享同一实例，随 Host 启动）
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PlanSchedulerService>());
        services.AddSingleton<SellPointDetectorService>();
        services.AddSingleton<BuyPointDetectorService>();
        services.AddSingleton<PatternSimilarityService>();
        // 形态相似度接线（对齐 IMultiFactorEvaluator 接线模式）：
        // SellPointDetectorService 构造依赖 IPatternSimilarityCalculator（可选参数），
        // 此前仅注册具体类导致接口解析为 null，8+ 处相似度门控从未生效
        services.AddSingleton<StockReview.Core.Engines.IPatternSimilarityCalculator>(
            sp => new StockReview.Core.Engines.PatternSimilarityAdapter(
                sp.GetRequiredService<PatternSimilarityService>()));
        services.AddSingleton<MultiFactorEngineService>();
        // 建议1/4 接线：包装为 IMultiFactorEvaluator 注入 SellPointDetectorService，
        // 使多因子评分（含富途资金流因子）真正生效
        services.AddSingleton<StockReview.Core.Engines.IMultiFactorEvaluator>(
            sp => new StockReview.Core.Engines.MultiFactorEngineAdapter(
                sp.GetRequiredService<MultiFactorEngineService>()));

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

        // Velopack 自动更新（启动延迟 15s 后台检查，静默下载应用，气泡提示下次启动生效）
        services.AddSingleton<UpdateService>();
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
        services.AddSingleton<TrayService>();
        // 自适应预热统计：导航频次 + 近因衰减，驱动 PreWarmViewCache 按使用习惯排序预热集合
        services.AddSingleton<ViewUsageService>();
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
    /// 存储结构（与原版 版完全一致）:
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

        // Velopack 安装版运行在 <root>\current\ 下，升级时整个 current 目录会被替换；
        // 且安装根 %LocalAppData%\<packId> 在 Setup 安装/修复/卸载时会被整目录清理——
        // 数据放安装根任何位置都会丢（2.1.3~2.1.5 把数据放安装根\data，Setup 重装后仍被重置）。
        // 安装版数据根固定到 %LocalAppData%\TradingAssistantWpf（与日志同根，Velopack 不管理该目录）；
        // 开发目录维持原状。
        IsVelopackInstalled = File.Exists(Path.Combine(AppBaseDir, "Update.exe"))
            || string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(AppBaseDir)),
                "current", StringComparison.OrdinalIgnoreCase);
        var dataRoot = IsVelopackInstalled
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingAssistantWpf")
            : AppBaseDir;
        DataDirConfigPath = Path.Combine(dataRoot, "data-dir.json");

        if (IsVelopackInstalled)
        {
            MigrateLegacyInstalledData(dataRoot);
        }

        // 读取数据目录配置文件（对应 data-dir.json）
        if (File.Exists(DataDirConfigPath))
        {
            try
            {
                var json = File.ReadAllText(DataDirConfigPath);
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
            DataDir = Path.Combine(dataRoot, "data");
        }

        // 确保目录存在
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "images"));
        Directory.CreateDirectory(Path.Combine(DataDir, "backups"));
        Directory.CreateDirectory(Path.Combine(DataDir, "pets"));

        // 播种默认宠物精灵（对应原版随包携带的 data/pets/firefly--lingxiaotian），
        // 否则全新安装首次启动精灵区空白
        SeedDefaultPet();

        Log.Information("[数据] 数据目录: {DataDir}（安装版数据根: {DataRoot}）", DataDir, dataRoot);
    }

    /// <summary>
    /// 安装版一次性迁移：历史版本曾把数据放在两个会被 Velopack 清理的位置——
    ///   ① pre-2.1.3：current\（升级即被整体替换）；
    ///   ② 2.1.3~2.1.5：%LocalAppData%\StockReviewWpf\data（Setup 安装/修复/卸载时整目录清理）。
    /// 首次启动把幸存数据搬到永久数据根 %LocalAppData%\TradingAssistantWpf，此后升级不再丢失。
    /// </summary>
    private static void MigrateLegacyInstalledData(string dataRoot)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 指针迁移：自定义数据目录指针曾写在安装根\data-dir.json（2.1.3~2.1.5）与 current\data-dir.json（pre-2.1.3）两处
            var legacyConfigPaths = new[]
            {
                Path.Combine(localAppData, "StockReviewWpf", "data-dir.json"),
                Path.Combine(AppBaseDir, "data-dir.json"),
            };
            foreach (var cfgPath in legacyConfigPaths)
            {
                if (File.Exists(DataDirConfigPath) || !File.Exists(cfgPath)) continue;
                var legacyJson = File.ReadAllText(cfgPath);
                var cfg = System.Text.Json.JsonSerializer.Deserialize<DataDirConfig>(legacyJson);
                if (!string.IsNullOrEmpty(cfg?.DataDir) && Directory.Exists(cfg.DataDir))
                {
                    Directory.CreateDirectory(dataRoot);
                    File.WriteAllText(DataDirConfigPath, legacyJson);
                    Log.Information("[数据] 已迁移数据目录指针 {Src} → {Dst}（指向 {DataDir}）", cfgPath, DataDirConfigPath, cfg.DataDir);
                    break;
                }
            }

            // 数据迁移：仅当沿用默认目录（无自定义指针）且新位置还没有数据库时执行；
            // 先查 2.1.3~2.1.5 的位置（数据最新），再查 pre-2.1.3 的 current\data
            if (File.Exists(DataDirConfigPath)) return;
            var targetData = Path.Combine(dataRoot, "data");
            var legacyDataDirs = new[]
            {
                Path.Combine(localAppData, "StockReviewWpf", "data"),
                Path.Combine(AppBaseDir, "data"),
            };
            foreach (var legacyData in legacyDataDirs)
            {
                if (File.Exists(Path.Combine(targetData, "data.db"))
                    || !File.Exists(Path.Combine(legacyData, "data.db"))) continue;
                CopyDirectory(legacyData, targetData);
                Log.Information("[数据] 已迁移旧位置数据 {Src} → {Dst}", legacyData, targetData);
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[数据] 迁移旧安装目录数据失败（不影响启动）");
        }
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(dstDir, Path.GetRelativePath(srcDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: false);
        }
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

        // 自适应预热：落盘本次会话导航计数到 appConfig['viewUsage']（本地 DB 写入，先于云端同步，不依赖网络）
        try
        {
            Host?.Services.GetService<ViewUsageService>()?.FlushSession();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WPF] 使用统计落盘失败");
        }

        RunCloudAutoSync();

        if (Host != null)
        {
            // 在 Host 停止前捕获 DatabaseService 引用（Dispose 后容器不可再解析），
            // 待 StopAsync 确保各服务内存态落盘后，再生成 .db 快照，保证快照完整性
            var snapshotDb = Host.Services.GetService<StockReview.Core.Data.DatabaseService>();
            Host.Services.GetService<TrayService>()?.Dispose();
            // 等待后台 Host 启动完成（若仍在启动）再调用 StopAsync，避免 Start/Stop 生命周期竞态。
            // Task.Run 内 StartAsync 续体回投线程池，不依赖 UI 线程，故 .Wait() 同步等待不会死锁。
            if (_hostStartTask != null && !_hostStartTask.IsCompleted)
            {
                try { _hostStartTask.Wait(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { Log.Warning(ex, "[WPF] 等待后台 Host 启动完成超时"); }
            }
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
            RunLocalSnapshotBackup(snapshotDb);
            Host.Dispose();
        }

        // 停止二次启动信号监听并释放资源
        _showMainWaitHandle?.Unregister(null);
        _showMainEvent?.Dispose();

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

    /// <summary>第二实例入口：通知运行中的第一实例显示主窗口（Mutex 拦截后调用）</summary>
    private static void NotifyRunningInstance()
    {
        try
        {
            using var evt = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, ShowMainEventName);
            evt.Set();
        }
        catch (Exception)
        {
            // 此时 Serilog 尚未初始化（单实例检查早于日志），静默兜底即可
        }
    }

    /// <summary>收到二次启动信号：恢复并前置主窗口（等价托盘"显示主窗口"，另处理最小化还原）</summary>
    private void ShowMainWindowFromSecondInstance()
    {
        if (IsQuitting) return;
        var main = MainWindow;
        if (main == null) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        main.Activate();
        Log.Information("[主窗] 收到二次启动信号，显示主窗口");
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
            var task = Task.Run(() => cloud.AutoSyncAsync(su.GetString()!, un.GetString()!, CredentialProtector.Unprotect(pw.GetString()) ?? "", remotePath));
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

    /// <summary>
    /// 退出前生成本地 .db 快照（在 Host 停止后调用，内存态已全部落盘）。
    /// 纯本地 IO 速度快，作为云同步失败时的兜底备份；Backup 内部自动清理超出保留数的旧快照。
    /// </summary>
    private static void RunLocalSnapshotBackup(StockReview.Core.Data.IDatabaseService? db)
    {
        if (db == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var path = db.Backup();
            Log.Information("[本地快照] 退出自动备份完成 ({Ms}ms): {Path}", sw.ElapsedMilliseconds, path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[本地快照] 退出自动备份失败");
        }
    }

    private class DataDirConfig
    {
        [System.Text.Json.Serialization.JsonPropertyName("dataDir")]
        public string? DataDir { get; set; }
    }
}
