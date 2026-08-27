using System;
using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 开机自启管理 - 对应 main.cjs 的 setAppAutoStart / getAppAutoStart
/// 使用 Windows 注册表（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）
/// </summary>
public class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "StockReviewSystem";
    private readonly string _exePath;

    public AutoStartService()
    {
        _exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
    }

    /// <summary>
    /// 设置开机自启（附加 --pet-only 参数，仅启动宠物）
    /// </summary>
    public (bool success, bool openAtLogin) SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                Log.Error("[开机自启] 无法打开注册表 Run 键");
                return (false, false);
            }

            if (enabled)
            {
                var cmd = $"\"{_exePath}\" --pet-only";
                key.SetValue(AppName, cmd);
                Log.Information("[开机自启] 已设置为开机自启: {Cmd}", cmd);
            }
            else
            {
                key.DeleteValue(AppName, false);
                Log.Information("[开机自启] 已取消开机自启");
            }

            var actual = GetAutoStart();
            return (true, actual.openAtLogin);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[开机自启] 设置失败");
            return (false, false);
        }
    }

    /// <summary>
    /// 查询当前开机自启状态
    /// </summary>
    public (bool success, bool openAtLogin) GetAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return (true, false);
            var value = key.GetValue(AppName) as string;
            return (true, !string.IsNullOrEmpty(value));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[开机自启] 查询失败");
            return (false, false);
        }
    }
}
