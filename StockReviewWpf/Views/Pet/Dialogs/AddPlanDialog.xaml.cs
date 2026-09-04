using System;
using System.Windows;
using System.Windows.Input;
using StockReviewWpf.ViewModels;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Dialogs;

/// <summary>
/// 添加交易计划对话框 - 对应 AddPlanDialog.vue（600px el-dialog 一比一复刻）
/// 位置记忆复用 PetPanelWindow 的 pet-panel-state.json（key: AddPlanDialog）
/// </summary>
public partial class AddPlanDialog : Window
{
    private const string PosKey = "AddPlanDialog";
    private readonly AddPlanDialogViewModel _viewModel;

    public TradePlan? Result { get; private set; }

    public AddPlanDialog()
    {
        InitializeComponent();
        _viewModel = new AddPlanDialogViewModel();
        DataContext = _viewModel;

        // 日历接线（自绘 ElCalendar，对齐原版 el-date-picker + disablePastDate）
        if (_viewModel.PlanDateValue is { } init)
            PlanCalendar.SetViewMonth(init);
        PlanCalendar.SelectedDate = _viewModel.PlanDateValue ?? DateTime.Today;
        PlanCalendar.DateSelected += (s, d) =>
        {
            _viewModel.PlanDateValue = d;
            UpdateDateText();
            DatePopup.IsOpen = false;
        };
        // 替代 PopupAnimation="Fade"：对内容做透明度淡入，不经过 PopupRoot 位移变换
        DatePopup.Opened += (_, _) => PopupCard.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        UpdateDateText();

        // 有记忆位置则恢复到上次打开处（否则保持 CenterOwner）
        SourceInitialized += (_, _) => PetPanelWindow.TryRestore(this, PosKey);
    }

    private void UpdateDateText()
    {
        var d = _viewModel.PlanDateValue;
        DateText.Text = d?.ToString("yyyy-MM-dd") ?? "";
        DateHint.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed;
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

        var target = _viewModel.PlanDateValue ?? DateTime.Today;
        PlanCalendar.SetViewMonth(target);
        // 每次打开都重新对齐选中态：SetViewMonth 会重渲染，SelectedDate 与上次相同时
        // 不会触发属性回调，必须显式赋值保证高亮落在 VM 当前日期上
        PlanCalendar.SelectedDate = _viewModel.PlanDateValue?.Date;
        DatePopup.IsOpen = true;
    }

    // ===== 键盘关闭（外部点击关闭由 StaysOpen=False 原生处理） =====
    private long _lastClosedAt;
    private bool _hooked;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_hooked) return;
        _hooked = true;
        DatePopup.Closed += (_, _) => _lastClosedAt = Environment.TickCount64;
        System.Windows.Input.InputManager.Current.PreProcessInput += OnPreProcessInput;
        Closed += OnDialogClosed;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnPreProcessInput;
    }

    /// <summary>
    /// 全局输入预处理：Popup 打开时，按任意真实按键（含 ESC）都关闭日历。
    /// StagingItem.Input 才是实际的 InputEventArgs（PreProcessInputEventArgs 本身不含 Device/RoutedEvent）。
    ///
    /// 只认 KeyEventArgs（对齐 ElDatePicker 的修复）：KeyboardFocusChanged /
    /// TextComposition 等事件的 Device 同样是 KeyboardDevice，若一并关闭，点击日历内
    /// 可聚焦元素触发的焦点变更事件会把弹层误关——"点切月后日历面板消失"的根因。
    /// </summary>
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!DatePopup.IsOpen) return;
        if (e.StagingItem?.Input is not KeyEventArgs args) return;
        if (IsModifierKey(args.Key)) return;
        DatePopup.IsOpen = false;
        // ESC 只收起日历，不透传给外层（避免一次 ESC 连带触发别的快捷键）
        if (args.RoutedEvent == System.Windows.Input.Keyboard.PreviewKeyDownEvent && args.Key == Key.Escape)
            e.Cancel();
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift
        or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        // 按钮已由 IsValid 禁用，这里兜底（对齐原版 handleSubmit 校验路径）
        if (!_viewModel.IsValid) return;

        Result = _viewModel.BuildPlan();
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        PetPanelWindow.Save(this, PosKey);
        base.OnClosed(e);
    }
}
