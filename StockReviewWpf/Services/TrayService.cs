using System;
using System.Windows;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 系统托盘服务 - 对应 main.cjs 的 Tray + Menu
/// </summary>
public class TrayService
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    public bool IsInitialized { get; private set; }

    public void Initialize()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "交易助手",
            Visible = true
        };

        // 设置托盘图标
        try
        {
            var iconPath = System.IO.Path.Combine(App.AppBaseDir, "Resources", "Images", "tray.ico");
            if (System.IO.File.Exists(iconPath))
                _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
            else if (System.Environment.ProcessPath != null)
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Environment.ProcessPath);
            else
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[托盘] 图标加载失败");
        }

        // 右键菜单
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
        menu.Items.Add("显示宠物", null, (s, e) => ShowPet());
        menu.Items.Add("隐藏宠物", null, (s, e) => HidePet());
        menu.Items.Add("-");
        // 必须走 RequestQuit 置位 IsQuitting，否则主窗 Closing 拦截（宠物启用时）会取消退出
        menu.Items.Add("退出", null, (s, e) => App.RequestQuit());
        _notifyIcon.ContextMenuStrip = menu;

        // 双击显示主窗口
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

        IsInitialized = true;
        Log.Information("[托盘] 初始化完成");
    }

    private void ShowMainWindow()
    {
        Application.Current.MainWindow?.Show();
        Application.Current.MainWindow?.Activate();
    }

    private void ShowPet()
    {
        var petManager = App.Host?.Services.GetService(typeof(PetWindowManager)) as PetWindowManager;
        petManager?.ShowPet();
    }

    private void HidePet()
    {
        var petManager = App.Host?.Services.GetService(typeof(PetWindowManager)) as PetWindowManager;
        petManager?.HidePet();
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
    }
}
