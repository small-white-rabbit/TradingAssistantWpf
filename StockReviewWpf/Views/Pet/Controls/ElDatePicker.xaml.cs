using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace StockReviewWpf.Views.Pet.Controls;

/// <summary>
/// 可复用 el-date-picker 日期选择器：点击日期框弹出 ElCalendar，选择日期后自动关闭。
/// 解决原生 DatePicker 在 AllowsTransparency=True 透明窗口内下拉按钮失效问题。
///
/// 用法：
///   &lt;controls:ElDatePicker SelectedDate="{Binding ...}" /&gt;
///   可选属性：DisablePast（禁选今天之前）、Placeholder（占位文字）
///
/// 三个历史坑（改代码前务必读）：
/// 1. SelectedDate 必须 BindsTwoWayByDefault——用普通 PropertyMetadata 注册时默认单向，
///    未显式写 Mode=TwoWay 的绑定（交易记录/心得/形态优化/计划列表）收不到新日期，
///    表现为"切了日期但拉的还是旧日期行情""保存后日期没变"。
/// 2. 关闭弹层的键盘钩子只认真正的 KeyEventArgs。KeyboardFocusChanged / TextComposition
///    等事件的 Device 同样是 KeyboardDevice，一并关会把"点月份箭头"的焦点变更误判成按键，
///    表现为"点切月后日历面板消失"。
/// 3. 严禁设置 PopupAnimation（Fade/Slide/Scroll）：动画期间 PopupRoot 带位移变换，
///    命中测试坐标与视觉位置不一致，点日期/切月会点错格子（时好时坏）。
///    淡入动效由 code-behind 对 PopupCard 做透明度动画替代，不影响命中测试。
/// </summary>
public partial class ElDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(ElDatePicker),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((ElDatePicker)d).OnSelectedDateChanged((DateTime?)e.NewValue)));

    /// <summary>选中的日期（DateTime?，默认双向绑定）</summary>
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
                if (ctrl.Calendar != null) ctrl.Calendar.DisablePast = (bool)e.NewValue;
            }));

    /// <summary>日期被选中时触发（可选的事件钩子，双向绑定已自动处理 SelectedDate）</summary>
    public event EventHandler? DateSelected;

    public ElDatePicker()
    {
        InitializeComponent();
        UpdateDateText();
        // 弹层必须随控件（或任一祖先）不可见而收起：对话框关闭/页面切走时
        // Popup 是独立 HWND，不收就会悬空浮在界面上。
        IsVisibleChanged += (_, _) => { if (!IsVisible) DatePopup.IsOpen = false; };
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
            TryUpdateSource();
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
    /// 选中日期变化：刷新显示并把值同步给内部日历（VM 侧改日期时日历高亮也要跟上）。
    /// </summary>
    private void OnSelectedDateChanged(DateTime? value)
    {
        UpdateDateText();
        var normalized = value?.Date;
        if (Calendar != null && Calendar.SelectedDate != normalized)
            Calendar.SelectedDate = normalized;
    }

    /// <summary>
    /// 强制把目标值同步回绑定源。单向绑定下 SetValue 会解除绑定表达式且 UpdateSource 会抛
    /// InvalidOperationException，必须先判 Mode 再调用（历史 bug：抛异常导致后面的
    /// DateSelected 事件与关闭弹层都不执行）。
    /// </summary>
    private void TryUpdateSource()
    {
        var expr = GetBindingExpression(SelectedDateProperty);
        if (expr == null) return;
        var mode = expr.ParentBinding?.Mode ?? BindingMode.Default;
        if (mode is BindingMode.OneWay or BindingMode.OneTime) return;
        if (mode != BindingMode.Default && mode != BindingMode.TwoWay && mode != BindingMode.OneWayToSource) return;
        expr.UpdateSource();
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
    /// 外部任意点击关闭由 StaysOpen=False 原生处理；键盘关闭见 OnPreProcessInput。
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
        Calendar.SelectedDate = SelectedDate?.Date;
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
    /// 全局输入预处理：弹层打开时按任意真实按键（含 ESC）关闭。
    /// StagingItem.Input 才是实际的 InputEventArgs（PreProcessInputEventArgs 本身不含 Device/RoutedEvent）。
    ///
    /// 只认 KeyEventArgs：KeyboardFocusChangedEventArgs / TextCompositionEventArgs 同样以
    /// KeyboardDevice 为设备，若一并关闭，点击日历内任何可聚焦元素（原来的月份切换 Button）
    /// 触发的焦点变更事件都会把弹层关掉——"点切月日历就没了"的根因。
    /// </summary>
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!DatePopup.IsOpen) return;
        if (e.StagingItem?.Input is not KeyEventArgs args) return;
        // 单纯按修饰键不算"要关闭"，否则 Shift/Ctrl 连点会误关
        if (IsModifierKey(args.Key)) return;

        DatePopup.IsOpen = false;
        // ESC 只收起日历，不透传给外层弹窗（否则一次 ESC 连带把对话框也关了）
        if (args.RoutedEvent == Keyboard.PreviewKeyDownEvent && args.Key == Key.Escape)
            e.Cancel();
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift
        or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System;

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
