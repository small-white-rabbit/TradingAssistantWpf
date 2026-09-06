using System;
using System.Windows;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 系统托盘服务 - 对应原版 Tray + Menu
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

        // 设置托盘图标（宠物精灵图 = Resources\Images\tray.ico，与主程序双K图标 app.ico 分工不同，
        // 对应原版托盘 icon-firefly-16.png。禁止更换此图标或改指 app.ico——
        // v2.2.6 曾把 exe/托盘/安装器三角色共用一个文件，换图导致宠物托盘图标被双K覆盖）
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
        // pet-only 模式主窗可能尚未创建：EnsureMainWindow 懒创建（2026-09-06 P2）
        var main = App.EnsureMainWindow();
        main.Show();
        main.Activate();
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
