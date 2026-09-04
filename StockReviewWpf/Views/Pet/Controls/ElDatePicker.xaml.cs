using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StockReviewWpf.Views.Pet.Controls;

/// <summary>
/// 可复用 el-date-picker 日期选择器：点击日期框弹出 ElCalendar，选择日期后自动关闭。
/// 解决原生 DatePicker 在 AllowsTransparency=True 透明窗口内下拉按钮失效问题。
///
/// 用法：
///   &lt;controls:ElDatePicker SelectedDate="{Binding ...}" /&gt;
///   可选属性：DisablePast（禁选今天之前）、Placeholder（占位文字）
/// </summary>
public partial class ElDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(ElDatePicker),
            new PropertyMetadata(null, (d, _) => ((ElDatePicker)d).UpdateDateText()));

    /// <summary>选中的日期（DateTime?，双向绑定）</summary>
    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>占位提示文字（默认"选择日期"）</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(ElDatePicker),
            new PropertyMetadata("选择日期", (d, e) => ((ElDatePicker)d).UpdateDateText()));

    public string Placeholder
    {
        get => (string?)GetValue(PlaceholderProperty) ?? "选择日期";
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>禁用今天之前（默认 false）</summary>
    public bool DisablePast
    {
        get => (bool)GetValue(DisablePastProperty);
        set => SetValue(DisablePastProperty, value);
    }

    public static readonly DependencyProperty DisablePastProperty =
        DependencyProperty.Register(nameof(DisablePast), typeof(bool), typeof(ElDatePicker),
            new PropertyMetadata(false, (d, e) =>
            {
                var ctrl = (ElDatePicker)d;
                ctrl.Calendar.DisablePast = (bool)e.NewValue;
            }));

    /// <summary>日期被选中时触发（可选的事件钩子，双向绑定已自动处理 SelectedDate）</summary>
    public event EventHandler? DateSelected;

    public ElDatePicker()
    {
        InitializeComponent();
        UpdateDateText();
        // 视图在隐藏停靠区与内容区之间移动会触发 Unloaded/Loaded 循环：
        // 卸载时摘钩、加载时重挂，否则一次导航后外部点击关闭就永久失效（日历"关不掉"）
        Loaded += (_, _) => HookInputManager();
        HookInputManager();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Calendar.DisablePast = DisablePast;
        Calendar.DateSelected += (_, d) =>
        {
            SelectedDate = d;
            // TwoWay 绑定目标→源默认异步排队，事件处理器同步执行时读到的还是旧日期
            //（表现为"切日期后拉的仍是旧日期行情"）。此处强制同步回写源后再触发事件。
            GetBindingExpression(SelectedDateProperty)?.UpdateSource();
            DateSelected?.Invoke(this, EventArgs.Empty);
            DatePopup.IsOpen = false;
        };
        if (SelectedDate is { } init)
            Calendar.SetViewMonth(init);
        // 替代 PopupAnimation="Fade"：对内容做透明度淡入，不经过 PopupRoot 位移变换，
        // 命中测试坐标与视觉位置始终一致（动画期间点击日期/切月也不会错位）。
        DatePopup.Opened += (_, _) => PopupCard.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>
    /// 点击日期框（按下阶段）：仅吞掉事件，切换放到抬起阶段。
    /// 在"抬起时开弹层" + StaysOpen=False 的组合下，打开后没有待释放的按键，
    /// 从根上规避了透明窗口中"打开瞬间的鼠标抬起被误判为外部点击"的长按问题。
    /// </summary>
    private void DateBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// 点击日期框（抬起阶段）：切换日历弹层。
    /// 外部任意点击关闭由 StaysOpen=False 原生处理；ESC/任意按键关闭见 OnPreProcessInput。
    /// </summary>
    private void DateBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (DatePopup.IsOpen)
        {
            DatePopup.IsOpen = false;
            return;
        }

        // 刚被本次点击序列关闭（StaysOpen=False 在按下阶段已外部关闭）→ 不重开，避免"点一下又弹回"
        if (Environment.TickCount64 - _lastClosedAt < 250) return;

        var target = SelectedDate ?? DateTime.Today;
        Calendar.SetViewMonth(target);
        Calendar.SelectedDate = SelectedDate;
        DatePopup.IsOpen = true;
    }

    // ===== 键盘关闭（外部点击关闭由 StaysOpen=False 原生处理） =====
    private long _lastClosedAt;
    private bool _hooked;

    /// <summary>首次加载时挂全局键盘输入钩子（UserControl 无 OnLoaded 可重写）</summary>
    private void HookInputManager()
    {
        if (_hooked) return;
        _hooked = true;
        DatePopup.Closed += (_, _) => _lastClosedAt = Environment.TickCount64;
        System.Windows.Input.InputManager.Current.PreProcessInput += OnPreProcessInput;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 视图在隐藏停靠区与内容区之间移动：摘钩并收起弹层，避免日历悬空残留
        DatePopup.IsOpen = false;
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _hooked = false;
        Unloaded -= OnUnloaded;
    }

    /// <summary>
    /// 全局输入预处理：Popup 打开时，任何键盘按键（含 ESC）都关闭日历。
    /// StagingItem.Input 才是实际的 InputEventArgs（PreProcessInputEventArgs 本身不含 Device/RoutedEvent）。
    /// </summary>
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!DatePopup.IsOpen) return;
        if (e.StagingItem?.Input is not InputEventArgs args) return;
        if (args.Device is not KeyboardDevice) return;
        DatePopup.IsOpen = false;
    }

    private void UpdateDateText()
    {
        if (IsLoaded == false && DateText == null)
        {
            Loaded += DeferredUpdate;
            return;
        }

        if (SelectedDate is { } d)
        {
            DateText.Text = d.ToString("yyyy-MM-dd");
            DateHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            DateText.Text = "";
            DateHint.Text = Placeholder;
            DateHint.Visibility = Visibility.Visible;
        }
    }

    private void DeferredUpdate(object sender, RoutedEventArgs e)
    {
        Loaded -= DeferredUpdate;
        UpdateDateText();
    }
}
