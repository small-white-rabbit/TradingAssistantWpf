using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace StockReviewWpf.Views.Pet;

/// <summary>
/// 独立宠物面板窗口 — 承载计划列表/提醒/历史/图库/设置/分时图等浮动面板。
///
/// 解决原内嵌方案的问题：
/// - 原面板被局限于 200×240 的宠物窗口内，720px 宽的面板被裁剪（"局限宠物区域"）。
/// - 面板内嵌导致宠物窗口尺寸/捕获状态变化，引发"宠物消失又出现"。
///
/// 设计：
/// - 无边界透明窗口，独立于宠物窗口显示，由 PetWindow 定位到宠物右侧。
/// - 标题栏可拖动（DragMove）。
/// - 关闭时通过 <see cref="CloseRequested"/> 通知宠物窗口重置面板状态。
/// - 位置记忆：按面板类型持久化到 pet-panel-state.json，用户拖动过后面板不再跟随宠物。
/// </summary>
public partial class PetPanelWindow : Window
{
    /// <summary>面板请求关闭时触发（用于通知 PetWindow 重置面板状态/心情）。</summary>
    public event Action? CloseRequested;

    /// <summary>当前面板标识（按面板类型记忆位置）。</summary>
    public string? CurrentKey { get; private set; }

    /// <summary>用户是否手动定位过面板（true 时不再跟随宠物移动）。</summary>
    public bool UserMoved { get; private set; }

    private static readonly string StatePath = Path.Combine(App.DataDir, "pet-panel-state.json");

    public PetPanelWindow()
    {
        InitializeComponent();
        Closed += (_, _) => SaveCurrentPosition();
    }

    /// <summary>设置面板内容与标题并显示窗口；有记忆位置则恢复，否则由 PetWindow 定位。</summary>
    public void ShowPanel(string title, object content, string key, double? width = null, bool autoHeight = false)
    {
        CurrentKey = key;
        UserMoved = false;
        PanelTitleText.Text = title;
        PanelContent.Content = content;

        // 分时图：隐藏窗口级标题栏（面板自带"分时图"标题头），避免双标题栏堆叠
        TitleBar.Visibility = key == "Intraday" ? Visibility.Collapsed : Visibility.Visible;

        var workArea = SystemParameters.WorkArea;
        var maxH = Math.Min(workArea.Height - 40, 880);

        // 按面板类型设置固定宽度（对齐原版 el-dialog width）
        if (width.HasValue && width.Value > 0)
        {
            Width = width.Value;

            // 计划列表固定视口：18 行(720) + 筛选栏 + 表头 + 边距 ≈ 862（超出由内部 ScrollViewer 出滚动条）
            // 提醒列表固定视口：8 行(320) + 工具条 + 表头 + 边距 ≈ 464
            // 其余面板按内容自适应高度（SizeToContent.Height）
            switch (key)
            {
                case "PlanList":
                    SizeToContent = SizeToContent.Manual;
                    Height = Math.Min(870, maxH);
                    break;
                case "Reminder":
                    SizeToContent = SizeToContent.Manual;
                    Height = Math.Min(466, maxH);
                    break;
                case "Intraday":
                    // 分时图：固定 800x700 视口，图表区随窗口拉伸（WpfPlot 不依赖内容撑高）
                    SizeToContent = SizeToContent.Manual;
                    Height = Math.Min(700, maxH);
                    break;
                case "Gallery":
                    // 图库固定视口：SizeToContent.Height + MaxHeight 下窗口高度随内容伸缩，
                    // 滚动位置随内容 invalidate 反复重置，ScrollChanged 懒加载链路断裂；
                    // 固定高度后 ScrollViewer 滚动稳定，接近底部扩批才可靠
                    SizeToContent = SizeToContent.Manual;
                    Height = Math.Min(820, maxH);
                    break;
                default:
                    SizeToContent = SizeToContent.Height;
                    MaxHeight = maxH;
                    break;
            }
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }

        if (TryRestorePosition(key))
            UserMoved = true;
        Show();
        Activate();
    }

    /// <summary>面板内容标题栏请求拖动窗口（无窗口级标题栏的面板，如分时图自带标题头）。</summary>
    public void DragFromContent()
    {
        try { DragMove(); }
        catch { }
        UserMoved = true;
        SaveCurrentPosition();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { }
            // 拖动结束：用户手动定位，记录位置并停止跟随宠物
            UserMoved = true;
            SaveCurrentPosition();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    /// <summary>面板内容请求关闭（设置面板保存/取消按钮路径，等价于点 ✕）。</summary>
    public void RequestClose()
    {
        SaveCurrentPosition();
        // 清理面板内容引用，防止 UserControl 的事件/绑定泄漏累积
        PanelContent.Content = null;
        Hide();
        CloseRequested?.Invoke();
    }

    /// <summary>窗口关闭时清理面板内容引用。</summary>
    protected override void OnClosed(EventArgs e)
    {
        PanelContent.Content = null;
        base.OnClosed(e);
    }

    // === 位置持久化（按面板类型记忆，对应宠物窗口的 pet-window-state.json 方案） ===

    private sealed class PanelPos
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private static Dictionary<string, PanelPos> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<Dictionary<string, PanelPos>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch { }
        return new();
    }

    private bool TryRestorePosition(string key) => TryRestore(this, key);

    private void SaveCurrentPosition()
    {
        if (string.IsNullOrEmpty(CurrentKey) || !IsLoaded) return;
        Save(this, CurrentKey);
    }

    /// <summary>恢复窗口到 key 的记忆位置（宠物系所有弹窗统一位置记忆，含 AddPlanDialog）。成功返回 true。</summary>
    internal static bool TryRestore(Window window, string key)
    {
        var pos = LoadState().GetValueOrDefault(key);
        if (pos == null) return false;
        var wa = SystemParameters.WorkArea;
        // 位置超出可见工作区则忽略（分辨率变化/换显示器后不复活到屏幕外）
        if (pos.X < wa.Left - 10 || pos.X > wa.Right - 80 || pos.Y < wa.Top - 10 || pos.Y > wa.Bottom - 60)
            return false;
        window.Left = pos.X;
        window.Top = pos.Y;
        return true;
    }

    /// <summary>把窗口当前位置保存到 key（宠物系所有弹窗统一位置记忆）。</summary>
    internal static void Save(Window window, string key)
    {
        try
        {
            var state = LoadState();
            state[key] = new PanelPos { X = window.Left, Y = window.Top };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
