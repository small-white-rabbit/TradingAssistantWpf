// 轻量对话框服务：解耦 ViewModel 与 WPF MessageBox（2026-09-02 优化报告 A3）。
// 用法：VM 构造函数加可选参数 `IDialogService? dialogs = null`，内部 `dialogs ?? DialogService.Instance`。
// 默认实现行为与原 MessageBox.Show 完全一致；测试时可注入 fake 自动确认/记录调用。
namespace StockReviewWpf.Services;

/// <summary>对话框服务接口（同步语义，贴合 MessageBox 模态行为）。</summary>
public interface IDialogService
{
    /// <summary>信息提示（Information 图标，确定按钮）。</summary>
    void Info(string message, string title = "提示");

    /// <summary>警告提示（Warning 图标，确定按钮）。</summary>
    void Warn(string message, string title = "提示");

    /// <summary>错误提示（Error 图标，确定按钮）。</summary>
    void Error(string message, string title = "错误");

    /// <summary>确认对话框（OKCancel + Warning 图标），用户点"确定"返回 true。</summary>
    bool Confirm(string message, string title = "确认");

    /// <summary>是/否对话框（YesNo + Warning 图标），用户点"是"返回 true。</summary>
    bool ConfirmYesNo(string message, string title = "确认");

    /// <summary>高危操作确认（OKCancel + Stop 图标），用户点"确定"返回 true。</summary>
    bool ConfirmDanger(string message, string title = "确认");
}

/// <summary>
/// 基于 WPF MessageBox 的默认实现。无状态，可直接用 <see cref="Instance"/> 单例。
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>默认共享实例（无状态，线程安全——MessageBox.Show 自身可跨线程调用）。</summary>
    public static readonly DialogService Instance = new();

    public void Info(string message, string title = "提示")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    public void Warn(string message, string title = "提示")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    public void Error(string message, string title = "错误")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

    public bool Confirm(string message, string title = "确认")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            == System.Windows.MessageBoxResult.OK;

    public bool ConfirmYesNo(string message, string title = "确认")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            == System.Windows.MessageBoxResult.Yes;

    public bool ConfirmDanger(string message, string title = "确认")
        => System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Stop)
            == System.Windows.MessageBoxResult.OK;
}
