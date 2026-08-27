using System;
using System.Collections.Generic;
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
            _petWindow.BubbleActionPerformed += HandleBubbleAction; // 气泡动作按钮处理
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

    /// <summary>检测 OpenD 是否运行，未运行时弹出气泡提醒（对应 OpendAlertEnabled 设置）。</summary>
    private async void CheckOpenDStatus()
    {
        try
        {
            var s = PetSettingsStore.Load();
            if (!s.OpendAlertEnabled || s.PrimarySource != "futu") return;
            var connected = await TcpProbeAsync(s.FutuHost, s.FutuPort);
            if (!connected)
            {
                _petService.ShowBubble("OpenD 未运行，请启动 OpenD 程序以获取实时行情", "warning", 10000);
                Log.Warning("[宠物] OpenD 未运行，已发送提醒气泡");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] OpenD 状态检测异常");
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
        if (_petMgmtService == null) return;
        try
        {
            var active = _petMgmtService.GetActivePet();
            if (string.IsNullOrEmpty(active)) return;
            var inst = _petMgmtService.ListInstalledPets()
                .FirstOrDefault(p => p.Id == active || p.FolderName == active);
            if (inst == null) return; // 已卸载：保持默认流萤
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
                    try { _petService.HideBubble(); } catch { }
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
    /// 点击后统一记录响应 + ack 当前气泡 + 关闭。
    /// </summary>
    private void HandleBubbleAction(StockReview.Core.Services.BubbleAction action)
    {
        try
        {
            // 1) 回写用户响应到提醒历史（气泡 Id 沿用入队时的提醒 ID）
            var bubbleId = _bubbleScheduler.CurrentBubble?.Id;
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
            // 3) 统一 ack 当前气泡并关闭（对齐原版 ackSlot + hideBubbleSlot）
            _bubbleScheduler.AckCurrent("executed");
            _petService.HideBubble();
        }
    }

    private static void ShowMainWindow()
    {
        var main = Application.Current.MainWindow;
        main?.Show();
        main?.Activate();
    }
}
