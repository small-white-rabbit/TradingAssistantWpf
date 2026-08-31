using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Services;

/// <summary>
/// 宠物窗口管理器 - 管理宠物窗口的创建、显示、隐藏
/// </summary>
public class PetWindowManager
{
    private Views.Pet.PetWindow? _petWindow;
    private readonly PetViewModel _petViewModel;
    private readonly PetService _petService;
    private readonly PetManagementService? _petMgmtService;
    private readonly StockReview.Core.Services.PlanSchedulerService _planScheduler;
    private readonly StockReview.Core.Services.CustomRemindersService _customReminders;
    private readonly StockReview.Core.Services.BubbleSchedulerService _bubbleScheduler;
    private readonly StockReview.Core.Services.ReminderHistoryService _reminderHistory;

    public PetWindowManager(
        PetViewModel petViewModel,
        PetService petService,
        StockReview.Core.Services.PlanSchedulerService planScheduler,
        StockReview.Core.Services.CustomRemindersService customReminders,
        StockReview.Core.Services.BubbleSchedulerService bubbleScheduler,
        StockReview.Core.Services.ReminderHistoryService reminderHistory,
        PetManagementService? petMgmtService = null)
    {
        _petViewModel = petViewModel;
        _petService = petService;
        _planScheduler = planScheduler;
        _customReminders = customReminders;
        _bubbleScheduler = bubbleScheduler;
        _reminderHistory = reminderHistory;
        _petMgmtService = petMgmtService;
    }

    public bool IsPetVisible => _petWindow?.IsVisible ?? false;

    public void ShowPet()
    {
        if (_petWindow != null && !_petWindow.IsVisible)
        {
            // 重用已建的窗口实例（保留位置/状态），需在 UI 线程调用
            _petWindow.Show();
            Log.Information("[宠物] 窗口已恢复显示");
            return;
        }

        if (_petWindow == null)
        {
            // 先应用持久化的激活外观再显示窗口：
            // 否则窗口先按默认流萤渲染，随后再切换到实际宠物（启动闪现两个宠物的根因）
            _petWindow = new Views.Pet.PetWindow { DataContext = _petViewModel };
            _petWindow.SetPetService(_petService);  // 显式注入，避免 ServiceLocator 反模式
            _petWindow.BubbleActionPerformed += HandleBubbleAction; // 气泡动作按钮处理（finally AckSlot('executed')）
            _petWindow.BubbleDismissed += (slot, reason) => _bubbleScheduler.AckSlot(slot, reason); // × 关闭即时释放槽位
            _petWindow.ShowMainWindowAction = ShowMainWindow;
            _petWindow.ClosePetAction = HidePet;
            ApplySettings();
            ApplyActivePet();
            _petWindow.Show();
            Log.Information("[宠物] 窗口已显示");

            // 启动后检测 OpenD 运行状态
            CheckOpenDStatus();
        }
    }

    /// <summary>
    /// 启动检测 OpenD（对齐 Electron main.cjs checkOpendAtStartup）：
    /// 选中富途行情时探测网关端口；未运行则弹带「立即启动」按钮的气泡（无路径时提示配置）；
    /// 已运行也弹简短确认气泡。
    /// </summary>
    private async void CheckOpenDStatus()
    {
        try
        {
            var s = PetSettingsStore.Load();
            if (!s.OpendAlertEnabled || s.PrimarySource != "futu") return;
            if (!IsLocalGateway(s.FutuHost)) return; // 仅本机网关支持拉起 OpenD 进程

            if (await TcpProbeAsync(s.FutuHost, s.FutuPort))
            {
                ShowOpenDBubble("OpenD 已在运行，订阅制行情恢复。", "encourage", 8000, "富途 OpenD");
                Log.Information("[宠物] OpenD 已在运行");
                return;
            }

            var path = ResolveOpenDPath(s.FutuOpenDPath);
            if (string.IsNullOrEmpty(path))
            {
                ShowOpenDBubble("未找到 OpenD 可执行文件，请在宠物设置-数据源中配置 OpenD 路径。",
                    "warning", 15000, "富途 OpenD 未运行");
                Log.Warning("[宠物] OpenD 未运行且未找到可执行文件");
                return;
            }

            ShowOpenDBubble(
                "OpenD 未运行，订阅制行情不可用，已降级到东财/腾讯轮询。点击「立即启动」拉起 OpenD 并登录。",
                "warning", 30000, "富途 OpenD 未运行",
                new List<StockReview.Core.Services.BubbleAction>
                {
                    new() { Type = "opend_start", Label = "🚀 立即启动" },
                    new() { Type = "opend_dismiss", Label = "稍后处理" }
                });
            Log.Warning("[宠物] OpenD 未运行，已发送快捷启动气泡");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] OpenD 状态检测异常");
        }
    }

    private bool _opendStarting;

    /// <summary>
    /// 拉起本机 OpenD 并后台轮询端口，就绪后弹气泡通知
    /// （对齐 Electron startFutuOpenD detached 启动 + watchOpendReadyInBackground 5s×5min 轮询）。
    /// </summary>
    private async Task StartOpenDAsync()
    {
        if (_opendStarting) return;
        try
        {
            var s = PetSettingsStore.Load();
            var path = ResolveOpenDPath(s.FutuOpenDPath);
            if (string.IsNullOrEmpty(path))
            {
                ShowOpenDBubble("未找到 OpenD 可执行文件，请在宠物设置-数据源中配置 OpenD 路径。",
                    "warning", 15000, "富途 OpenD");
                return;
            }

            // 竞态防护：点击时可能已被拉起/手动启动
            if (await TcpProbeAsync(s.FutuHost, s.FutuPort))
            {
                ShowOpenDBubble("OpenD 已在运行，订阅制行情恢复。", "encourage", 8000, "富途 OpenD");
                return;
            }

            _opendStarting = true;
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-mode normal",
                UseShellExecute = true, // 独立进程，不随宿主退出（对齐 Electron detached spawn）
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            });
            Log.Information("[宠物] 已拉起 OpenD: {Path}", path);

            ShowOpenDBubble("正在拉起本机 OpenD 并等待登录，就绪后自动通知。", "hint", 12000, "富途 OpenD");

            // 后台轮询：5 秒间隔，最长 5 分钟，端口可达即视为登录就绪
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (await TcpProbeAsync(s.FutuHost, s.FutuPort))
                {
                    ShowOpenDBubble("富途连接已正常，订阅制行情已恢复。", "encourage", 10000, "富途 OpenD 已就绪");
                    Log.Information("[宠物] OpenD 已就绪");
                    return;
                }
            }
            Log.Warning("[宠物] 等待 OpenD 就绪超时（5 分钟）");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 拉起 OpenD 失败");
            ShowOpenDBubble("OpenD 启动失败，请手动启动 OpenD 并完成登录。", "warning", 12000, "富途 OpenD");
        }
        finally
        {
            _opendStarting = false;
        }
    }

    /// <summary>解析 OpenD 可执行文件：优先用户配置路径，其次常见安装位置（对齐 Electron detectFutuOpenDPath）。</summary>
    private static string? ResolveOpenDPath(string configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(configuredPath);

        candidates.AddRange(new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Futu", "FutuOpenD", "FutuOpenD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Futu", "FutuOpenD", "FutuOpenD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Futu", "FutuOpenD", "FutuOpenD.exe"),
            @"C:\Program Files\Futu\FutuOpenD\FutuOpenD.exe",
            @"C:\Program Files (x86)\Futu\FutuOpenD\FutuOpenD.exe"
        });

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Environment.ExpandEnvironmentVariables)
            .FirstOrDefault(File.Exists);
    }

    private static bool IsLocalGateway(string host)
        => host == "127.0.0.1" || host == "localhost" || host == "::1";

    /// <summary>OpenD 气泡统一出口：经气泡调度器入队展示（异常仅记日志，不阻塞调用方）。</summary>
    private void ShowOpenDBubble(string text, string type, int durationMs, string? title = null,
        List<StockReview.Core.Services.BubbleAction>? actions = null)
    {
        try
        {
            // 不直连 ShowBubble：直连气泡绕过调度器，其 Current 过期时发出的 hide 会把
            // 本气泡误关（未操作几秒后消失的根因）。入队后：
            // - 与队列气泡串行展示互不顶掉，Importance=5 插队到最前
            // - 带动作按钮的项调度器不自动过期（等用户操作，仅 30 分钟安全阀兜底）
            // - 60 秒内同标题+类型去重（重复触发不重复骚扰）
            _bubbleScheduler.Enqueue(new StockReview.Core.Services.BubbleQueueItem
            {
                Id = $"opend_{DateTime.Now:yyyyMMddHHmmssfff}",
                Title = title ?? "富途 OpenD",
                Content = text,
                Level = type, // warning/critical/alert 命中专属配色；encourage 回退按 Importance 映射
                Importance = 5,
                DurationMs = durationMs,
                Actions = actions
            });
            Log.Information("[宠物] OpenD 气泡已入队: {Title} - {Text}", title ?? "", text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] OpenD 气泡显示失败");
        }
    }

    private static async Task<bool> TcpProbeAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动时把 DB 持久化的激活宠物应用到精灵（对应原版 initialize 里 loadActivePet）。
    /// 运行期切换由画廊面板直接调 PetWindow.ApplyPetAppearance 实时生效。
    /// </summary>
    private void ApplyActivePet()
    {
        if (_petMgmtService == null)
        {
            Log.Warning("[宠物] 加载激活外观跳过：PetManagementService 未注入，保持默认流萤");
            return;
        }
        try
        {
            var active = _petMgmtService.GetActivePet();
            if (string.IsNullOrEmpty(active))
            {
                // 问题③排查关键点：为空可能是 DB 读取异常被吞或备份覆盖为空
                Log.Information("[宠物] 加载激活外观：activePetId 为空（未设置或读取失败），保持默认流萤");
                return;
            }
            var installed = _petMgmtService.ListInstalledPets();
            var inst = installed.FirstOrDefault(p => p.Id == active || p.FolderName == active);
            if (inst == null)
            {
                // 已卸载或备份把 activePetId 覆盖成本机没有的 id：保持默认流萤
                Log.Warning("[宠物] 激活外观 {Active} 未在本机安装（已安装: {Installed}），保持默认流萤",
                    active, string.Join(",", installed.Select(p => p.Id)));
                return;
            }
            _petViewModel.PetId = inst.Id;
            _petViewModel.SpriteVersion = inst.SpriteVersionNumber;
            Log.Information("[宠物] 启动应用激活外观: {PetId} V{Version}", inst.Id, inst.SpriteVersionNumber);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 启动加载激活外观失败，保持默认");
        }
    }

    /// <summary>按持久化设置对精灵尺寸/透明度与窗口尺寸实时生效（设置页滑块调用）。</summary>
    public void ApplySettings()
    {
        var s = PetSettingsStore.Load();
        _petViewModel.PetSize = 140 * s.PetSize;
        _petViewModel.PetOpacity = s.PetOpacity;
        _petViewModel.AnimationSpeed = s.AnimationSpeed;
        _petWindow?.ApplyPetSettings(s);
    }

    public void HidePet()
    {
        if (_petWindow != null && _petWindow.IsVisible)
        {
            _petWindow.Hide(); // 隐藏到托盘而非关闭，便于从托盘一键恢复
            Log.Information("[宠物] 窗口已隐藏到托盘");
        }
    }

    public void TogglePet()
    {
        if (IsPetVisible) HidePet();
        else ShowPet();
    }

    /// <summary>
    /// 应用程序退出时的清理入口：立即关闭气泡 + 销毁宠物窗口，确保 WPF Popup（独立 HWND）
    /// 不会在主程序/主窗口关闭后仍"漂浮"到气泡消失动画完毕才消失。
    /// </summary>
    public void Shutdown()
    {
        try
        {
            if (_petWindow != null)
            {
                if (_petService != null)
                {
                    try { _petService.HideBubble(force: true); } catch { }
                }
                _petWindow.Dispatcher.Invoke(() => _petWindow.ShutdownNow(),
                    System.Windows.Threading.DispatcherPriority.Send);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 退出清理失败");
        }
    }

    /// <summary>
    /// 气泡动作按钮处理（对应 DesktopPet.vue handleBubbleAction）：
    /// 收盘提醒 → 调度器批量处理计划；自定义提醒 → 回写提醒状态；
    /// 点击后统一记录响应 + ack 对应槽位气泡 + 关闭该槽位。
    /// </summary>
    private void HandleBubbleAction(StockReview.Core.Services.BubbleAction action, string slot)
    {
        try
        {
            // 1) 回写用户响应到提醒历史（气泡 Id 沿用入队时的提醒 ID）
            var bubbleId = _bubbleScheduler.GetSlotItem(slot)?.Id;
            if (!string.IsNullOrEmpty(bubbleId))
                _reminderHistory.UpdateRecordResponse(bubbleId, action.Type);

            // 2) 分发动作
            switch (action.Type)
            {
                case "after_market_record":
                    // 打开主程序补录交易记录（WPF 宠物无内嵌交易表单）
                    ShowMainWindow();
                    break;

                case "after_market_continue":
                case "after_market_complete":
                case "after_market_dismiss":
                    _planScheduler.HandleAfterMarketAction(action.Type, action.PlanIds ?? new List<string>());
                    break;

                case "custom_done":
                    _customReminders.RespondToReminder(action.ReminderId ?? "", "done");
                    break;

                case "custom_snooze":
                    // 默认稍后 10 分钟再次提醒（对齐原版 respondToReminder(id,'snooze',10)）
                    _customReminders.RespondToReminder(action.ReminderId ?? "", "snooze", 10);
                    break;

                case "opend_start":
                    // 快捷启动 OpenD 并后台轮询就绪（就绪/失败均有后续气泡）
                    _ = StartOpenDAsync();
                    break;

                case "opend_dismiss":
                    Log.Information("[宠物] 用户选择稍后处理 OpenD 启动");
                    break;

                default:
                    Log.Debug("[宠物] 未处理的气泡动作: {Type}", action.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 气泡动作处理失败: {Type}", action.Type);
        }
        finally
        {
            // 3) 统一 ack 对应槽位并关闭（对齐原版 ackSlot + hideBubbleSlot；force=true 确保动作气泡关闭）
            _bubbleScheduler.AckSlot(slot, "executed");
            _petService.HideBubble(slot, force: true);
        }
    }

    private static void ShowMainWindow()
    {
        var main = Application.Current.MainWindow;
        main?.Show();
        main?.Activate();
    }
}
