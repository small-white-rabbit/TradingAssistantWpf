using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Pet;
using StockReviewWpf.Views.Pet.Panels;

using PetSettings = StockReviewWpf.ViewModels.PetSettings;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace StockReviewWpf.Views.Pet;

/// <summary>
/// 桌面宠物窗口 — WPF 版
/// 对应 Electron 版的宠物 BrowserWindow + DesktopPet.vue
///
/// 能力：
/// - 透明无边界窗口：WindowStyle="None" + AllowsTransparency="True"
/// - 点击穿透：Win32 WS_EX_TRANSPARENT | WS_EX_LAYERED
/// - 置顶：Topmost="True"
/// - 拖拽移动：DragMove + 方向检测
/// - 右键菜单：计划列表/提醒/图库/设置/打开主程序/关闭
/// - 面板切换：点击菜单项在面板容器中切换
/// - 位置持久化：JSON 存储窗口位置
/// </summary>
public partial class PetWindow : Window
{
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartLeft;
    private double _dragStartTop;
    private string? _dragDirection;
    private DateTime _dragStartTime;

    // 面板状态
    private PetPanelType _currentPanel = PetPanelType.None;

    // 独立面板窗口（对应原内嵌 PanelContainer，抽离为独立窗口避免被宠物窗口裁剪）
    private PetPanelWindow? _panelWindow;

    // 面板引用
    private PlanListPanel? _planListPanel;
    private ReminderPanel? _reminderPanel;
    private ReminderHistoryPanel? _historyPanel;
    private GalleryPanel? _galleryPanel;
    private PetSettingsPanel? _petSettingsPanel;

    private Panels.PetSettingsPanel GetSettingsPanel()
    {
        _petSettingsPanel ??= new PetSettingsPanel();
        EnsureSettingsWired(_petSettingsPanel);
        return _petSettingsPanel;
    }
    private IntradayChartPanel? _intradayPanel;

    // ViewModel（用于双向面板导航）
    private PetViewModel? _vm;

    // 回调
    public Action? ShowMainWindowAction { get; set; }
    public Action? ClosePetAction { get; set; }

    /// <summary>拖拽移动开关（来自宠物设置）</summary>
    public bool DragMoveEnabled { get; set; } = true;

    private bool _settingsWired;
    private PetSettings _currentSettings = PetSettingsStore.Load();

    // 全屏遮罩窗口（critical 气泡时覆盖全屏提示）
    private OverlayWindow? _overlayWindow;

    public PetWindow()
    {
        InitializeComponent();
        LoadSavedPosition();
        _savePosTimer.Tick += (_, _) => { _savePosTimer.Stop(); SavePosition(); };

        // 订阅 PetViewModel 的面板导航（对应 Electron 点击宠物/VM 命令打开面板）
        // 注意：PetWindowManager 用对象初始化器在构造完成后才赋 DataContext，
        // 构造器里 DataContext 恒为 null，必须挂 DataContextChanged 才能拿到 VM
        DataContextChanged += (s, _) =>
        {
            if (s is PetWindow w && w.DataContext is PetViewModel vm)
                w.AttachViewModel(vm);
        };
        if (DataContext is PetViewModel initialVm)
            AttachViewModel(initialVm);

        // 诊断：记录窗口可见性变化——下次"消失又出现"时可直接区分
        // 「窗口被隐藏/关闭」（有日志）与「纯渲染闪烁」（无日志）
        IsVisibleChanged += (s, e) =>
            Log.Information("[宠物] 窗口可见性: {Visible}，位置 ({X},{Y})", IsVisible, (int)Left, (int)Top);
    }

    /// <summary>
    /// 立即切换精灵外观（对应原版跨窗口 onActivePetUpdated 广播）。
    /// 画廊面板"使用/切回默认"后调用：写 VM 属性经绑定传导到 SpriteControl。
    /// </summary>
    public void ApplyPetAppearance(string petId, int spriteVersion)
    {
        Log.Information("[宠物] 切换外观: {PetId} V{Version}", petId, spriteVersion);
        if (DataContext is PetViewModel vm)
        {
            vm.PetId = petId;
            vm.SpriteVersion = spriteVersion;
        }
        else
        {
            // VM 未就绪时直接落到控件（本地值优先于绑定）
            SpriteControl.PetId = petId;
            SpriteControl.SpriteVersion = spriteVersion;
        }
    }

    /// <summary>由 PetWindowManager 显式注入 PetService，避免 ServiceLocator 反模式</summary>
    public void SetPetService(PetService petService)
    {
        petService.BubbleRequested += (text, type, duration, title, actions) =>
            Dispatcher.BeginInvoke(() => ShowBubble(text, type, duration, title, actions));
        petService.MoodRequested += mood =>
            Dispatcher.BeginInvoke(() => SetMoodFromScheduler(mood));
        petService.BubbleHiddenRequested += () =>
            Dispatcher.BeginInvoke(HideBubble);
    }

    /// <summary>把调度器 MoodType 映射到精灵动画帧并加载（3 秒后恢复 idle）</summary>
    private void SetMoodFromScheduler(StockReview.Core.Services.MoodType mood)
    {
        Log.Information("[宠物] 调度器心情切换: {Mood}", mood);
        var frame = mood switch
        {
            StockReview.Core.Services.MoodType.Happy => "happy",
            StockReview.Core.Services.MoodType.Sad => "crying",
            StockReview.Core.Services.MoodType.Thinking => "focused",
            StockReview.Core.Services.MoodType.Celebrating => "celebrating",
            StockReview.Core.Services.MoodType.Excited => "excited",
            StockReview.Core.Services.MoodType.Sleeping or StockReview.Core.Services.MoodType.Resting => "idle",
            _ => "idle"
        };
        SpriteControl.Mood = frame;
        if (mood != StockReview.Core.Services.MoodType.Neutral)
            ScheduleMoodReset(TimeSpan.FromSeconds(3));
    }

    /// <summary>绑定 ViewModel 并订阅面板导航事件（可重复调用，自动解除旧订阅防止泄漏）</summary>
    public void AttachViewModel(PetViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PetViewModel.CurrentPanel)) return;
        var panel = _vm?.CurrentPanel switch
        {
            "PlanList" => PetPanelType.PlanList,
            "Reminder" => PetPanelType.Reminder,
            "Gallery" => PetPanelType.Gallery,
            "Settings" => PetPanelType.Settings,
            "Intraday" => PetPanelType.Intraday,
            _ => PetPanelType.None
        };

        if (panel == PetPanelType.None)
            HidePanel();
        else
            ShowPanel(panel);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Log.Information("[宠物] 窗口已加载，位置: ({X}, {Y})", Left, Top);
        // 加载已持久化的宠物设置并生效（点击穿透/拖拽开关）
        ApplyPetSettings(PetSettingsStore.Load());
    }

    // === 拖拽逻辑（对应 DesktopPet.vue 的拖拽） ===

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 系统双击判定（对齐原版 dblclick）：直接打开交易计划列表，不进入拖拽/隐藏面板
        if (e.ClickCount >= 2)
        {
            OnPetDoubleClick();
            return;
        }

        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragStartTime = DateTime.Now;
        _dragDirection = null;

        // 隐藏面板
        HidePanel();

        // 使用 DragMove 让窗口跟随鼠标（仅当拖拽开关开启）
        if (DragMoveEnabled)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        // 检测拖拽方向（用于精灵动画切换 running-left/right）
        var currentPos = e.GetPosition(this);
        var deltaX = currentPos.X - _dragStartPoint.X;
        if (Math.Abs(deltaX) > 5)
        {
            _dragDirection = deltaX > 0 ? "right" : "left";
            // 更新精灵控件
            SpriteControl.IsDragging = true;
            SpriteControl.DragDirection = _dragDirection;
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        SpriteControl.IsDragging = false;
        SpriteControl.DragDirection = null;

        // 短按 = 点击（可触发互动）
        var elapsed = (DateTime.Now - _dragStartTime).TotalMilliseconds;
        var dist = Point.Subtract(e.GetPosition(this), _dragStartPoint).Length;
        if (elapsed < 250 && dist < 5)
        {
            OnPetClick();
        }
    }

    /// <summary>宠物被单击（短按，双击已由 MouseDown 的 ClickCount 分流）：随机开心互动并鼓励。</summary>
    private static readonly Random _random = new();

    private void OnPetClick() => OnPetSingleClick();

    private static readonly string[] ClickMoods = { "excited", "celebrating", "happy" };
    private static readonly string[] SingleClickTexts = { "嘿嘿，点我干嘛呀～", "手感不错哦！", "继续加油鸭！" };

    private void OnPetSingleClick()
    {
        // 点击动画帧：随机切到兴奋/庆祝帧，3 秒后恢复 idle
        SpriteControl.Mood = ClickMoods[_random.Next(ClickMoods.Length)];
        ScheduleMoodReset(TimeSpan.FromSeconds(3));
        // 待操作的动作气泡显示中：互动文案不顶掉
        if (!IsActionBubbleActive)
            ShowBubble(SingleClickTexts[_random.Next(SingleClickTexts.Length)], "encourage", 3200);
    }

    private void OnPetDoubleClick()
    {
        // 双击宠物 → 打开交易计划列表（对齐原版 DesktopPet.vue 双击行为）
        ShowPanel(PetPanelType.PlanList);
    }

    private void ScheduleMoodReset(TimeSpan delay)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            SpriteControl.Mood = "idle";
        };
        timer.Start();
    }

    // === 右键菜单 ===

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 自定义 Popup 菜单（对齐 PetMenu.vue 暖色渐变风格）
        var pos = e.GetPosition(this);
        MenuPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        MenuPopup.PlacementTarget = this;
        MenuPopup.HorizontalOffset = pos.X;
        MenuPopup.VerticalOffset = pos.Y;
        MenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void Menu_Click(object sender, MouseButtonEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (sender is not FrameworkElement fe) return;
        var action = fe.Tag as string ?? "";
        switch (action)
        {
            case "addPlan": MenuAddPlan_Click(sender, e); break;
            case "viewPlans": ShowPanel(PetPanelType.PlanList); break;
            case "viewCustomReminders": ShowPanel(PetPanelType.Reminder); break;
            case "viewHistory": ShowPanel(PetPanelType.History); break;
            case "petGallery": ShowPanel(PetPanelType.Gallery); break;
            case "toggleTop":
                Topmost = !Topmost;
                if (_panelWindow != null) _panelWindow.Topmost = Topmost;
                if (BubblePopup.IsOpen) SetPopupTopmost(BubblePopup, Topmost);
                if (MenuPopup.IsOpen) SetPopupTopmost(MenuPopup, Topmost);
                MenuPinText.Text = Topmost ? "取消置顶" : "置顶";
                MenuPinIcon.Text = Topmost ? "📌" : "📍";
                break;
            case "openSettings": ShowPanel(PetPanelType.Settings); break;
            case "exit":
                HidePanel();
                ClosePetAction?.Invoke();
                break;
        }
    }

    private void BubbleClose_Click(object sender, RoutedEventArgs e)
    {
        HideBubble();
    }

    private void MenuAddPlan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.AddPlanDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            var r = dialog.Result;
            var plan = new StockReview.Core.Services.TradePlan
            {
                StockCode = r.StockCode,
                StockName = r.StockName,
                PlanType = r.PlanType,
                PlanDate = r.PlanDate,
                EntryReason = r.EntryReason,
                EntryPrice = r.EntryPrice,
                TargetPrice = r.TargetPrice,
                StopLoss = r.StopLoss,
                MaxHoldDays = r.MaxHoldDays,
                Note = r.Note,
            };
            var planService = App.Host?.Services.GetService(typeof(StockReview.Core.Services.TradePlanService))
                as StockReview.Core.Services.TradePlanService;
            var (ok, _, error) = planService?.AddPlan(plan) ?? (false, null, "服务未初始化");
            if (!IsActionBubbleActive)
                ShowBubble(ok ? $"已添加计划：{r.StockName} {r.PlanDate}" : $"添加失败：{error}", ok ? "hint" : "tease", 5000);
            if (ok) _planListPanel?.RefreshData();
        }
    }

    // === 面板切换 ===

    private void ShowPanel(PetPanelType type)
    {
        if (_currentPanel == type)
        {
            HidePanel();
            return;
        }

        _currentPanel = type;

        // 懒加载面板
        UserControl? panel = type switch
        {
            PetPanelType.PlanList => _planListPanel ??= new PlanListPanel(),
            PetPanelType.Reminder => _reminderPanel ??= new ReminderPanel(),
            PetPanelType.History => _historyPanel ??= new ReminderHistoryPanel(),
            PetPanelType.Gallery => _galleryPanel ??= new GalleryPanel(),
            PetPanelType.Settings => GetSettingsPanel(),
            PetPanelType.Intraday => _intradayPanel ??= new IntradayChartPanel(),
            _ => null
        };

        // 每次打开刷新数据（新增的计划/提醒/历史立即可见，对应原版每次打开重新拉 store）
        if (panel is PlanListPanel pl) pl.RefreshData();
        else if (panel is ReminderPanel rm) rm.RefreshData();
        else if (panel is ReminderHistoryPanel rh) rh.RefreshData();

        if (panel == null) return;

        var title = type switch
        {
            PetPanelType.PlanList => "📋 交易计划列表",
            PetPanelType.Reminder => "⏰ 自定义提醒",
            PetPanelType.History => "🕐 提醒历史",
            PetPanelType.Gallery => "🎨 宠物外观",
            PetPanelType.Settings => "⚙ 宠物设置",
            PetPanelType.Intraday => "📈 分时图",
            _ => ""
        };

        EnsurePanelWindow();
        // 按面板类型设置宽度（对齐原版 Electron el-dialog width）
        var panelWidth = type switch
        {
            PetPanelType.PlanList => 780.0,
            PetPanelType.Reminder => 700.0,
            PetPanelType.History => 720.0,
            PetPanelType.Gallery => 720.0,
            PetPanelType.Settings => 780.0,
            PetPanelType.Intraday => 800.0,
            _ => (double?)null
        };
        _panelWindow!.ShowPanel(title, panel, type.ToString(), panelWidth, type switch { PetPanelType.Settings => true, _ => false });
        // 用户拖动过/有记忆位置的面板不再自动定位（位置记忆）
        if (!_panelWindow.UserMoved)
        {
            PositionPanelWindow();
            // SizeToContent 下尺寸需布局完成才可知，延迟一轮再校正位置
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    if (_panelWindow is { IsVisible: true, UserMoved: false })
                        PositionPanelWindow();
                }));
        }

        // 切换为工作心情
        SpriteControl.Mood = "working";
    }

    /// <summary>
    /// 打开分时图面板并加载指定股票（计划列表点击股票名入口）。
    /// 面板已打开时直接切换股票，不重复开关面板。
    /// </summary>
    public void ShowIntradayChart(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;

        if (_currentPanel == PetPanelType.Intraday && _intradayPanel != null)
        {
            _intradayPanel.LoadStock(stockCode);
            return;
        }

        ShowPanel(PetPanelType.Intraday);
        _intradayPanel?.LoadStock(stockCode);
    }

    /// <summary>
    /// 懒创建独立面板窗口，并绑定关闭回调。
    /// </summary>
    private void EnsurePanelWindow()
    {
        if (_panelWindow != null) return;

        _panelWindow = new PetPanelWindow
        {
            Owner = this,
            Topmost = Topmost
        };
        _panelWindow.CloseRequested += () =>
        {
            _currentPanel = PetPanelType.None;
            SpriteControl.Mood = "idle";
        };
        _panelWindow.Closed += (_, _) => _panelWindow = null;
    }

    /// <summary>
    /// 把面板窗口定位到宠物右上方，随宠物窗口移动。
    /// </summary>
    private void PositionPanelWindow()
    {
        if (_panelWindow == null) return;
        var panelW = _panelWindow.ActualWidth > 0 ? _panelWindow.ActualWidth : 720;
        var panelH = _panelWindow.ActualHeight > 0 ? _panelWindow.ActualHeight : 600;

        var wa = SystemParameters.WorkArea;
        double x = Left + ActualWidth + 10;
        if (x + panelW > wa.Right) x = Math.Max(wa.Left + 4, Left - panelW - 10);
        x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - panelW - 4));

        double y = Top;
        y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - panelH - 4));

        _panelWindow.Left = x;
        _panelWindow.Top = y;
    }

    private void HidePanel()
    {
        _currentPanel = PetPanelType.None;
        // 清理面板窗口内容引用，防止 UserControl 泄漏累积
        if (_panelWindow != null)
            _panelWindow.PanelContent.Content = null;
        _panelWindow?.Hide();
        SpriteControl.Mood = "idle";
    }

    /// <summary>
    /// 公开关闭面板方法（供子面板按钮调用）
    /// </summary>
    public void ClosePanel() => HidePanel();

    private readonly System.Windows.Threading.DispatcherTimer _bubbleTimer = new();

    /// <summary>
    /// 显示气泡消息（对应 PetBubble.vue）。type 对齐原版 level 配色。
    /// actions 非空时渲染动作按钮并隐藏 × 关闭按钮（对齐原版 actions/close 互斥）。
    /// </summary>
    public void ShowBubble(string text, string type = "encourage", int? durationMs = null, string? title = null,
        IReadOnlyList<StockReview.Core.Services.BubbleAction>? actions = null)
    {
        var style = BubbleStyles.TryGetValue(type, out var s) ? s : BubbleStyles["encourage"];

        // 停止上一个气泡的定时器
        _bubbleTimer.Stop();

        // 标题：优先使用传入的 title（对齐 Electron bubble.title 数据字段），
        // 无传入时回退到样式常量（默认空字符串 → 隐藏标题行）
        var displayTitle = !string.IsNullOrEmpty(title) ? title : style.Title;
        BubbleTitle.Text = displayTitle;
        BubbleTitle.Visibility = string.IsNullOrEmpty(displayTitle) ? Visibility.Collapsed : Visibility.Visible;

        // 内容
        BubbleText.Text = text;

        // 边框（对齐 PetBubble.vue border 配色）
        BubbleBorder.BorderBrush = new SolidColorBrush(style.Border);
        BubbleBorder.BorderThickness = type == "critical" ? new Thickness(2.5) :
                                       (type == "warning" || type == "force") ? new Thickness(2) : new Thickness(1.5);

        // 背景渐变（对齐 PetBubble.vue linear-gradient）
        var bgBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            Opacity = _currentSettings.BubbleBackgroundOpacity
        };
        bgBrush.GradientStops.Add(new GradientStop(style.Fill, 0));
        bgBrush.GradientStops.Add(new GradientStop(style.FillEnd, 1));
        BubbleBorder.Background = bgBrush;

        // 阴影色（对齐 PetBubble.vue box-shadow 配色）
        BubbleShadow.Color = style.Border;
        BubbleShadow.Opacity = type switch
        {
            "critical" => 0.35,
            "force" => 0.20,
            "warning" => 0.25,
            "alert" => 0.22,
            _ => 0.18
        };

        // 尾巴颜色跟随背景
        BubbleTriangle.Fill = new SolidColorBrush(style.FillEnd);
        BubbleTriangle.Stroke = new SolidColorBrush(style.Border);

        // 动作按钮：动态注入（对齐 PetBubble.vue bubble-actions 渲染）
        RenderBubbleActions(actions);

        BubblePopup.IsOpen = true;

        // 屏幕闪烁效果（仅当设置启用）
        if (_currentSettings.ScreenFlashEnabled)
        {
            var flashAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.35,
                Duration = TimeSpan.FromMilliseconds(150),
                AutoReverse = true,
                AccelerationRatio = 0.2,
                DecelerationRatio = 0.2
            };
            BeginAnimation(OpacityProperty, flashAnim);
        }

        // 全屏遮罩（仅 critical 类型且设置启用时）
        if (_currentSettings.FullscreenOverlayEnabled && type == "critical")
        {
            ShowOverlay();
        }

        var defaultDuration = _currentSettings.BubbleDisplayDuration > 0
            ? _currentSettings.BubbleDisplayDuration
            : 8000;
        // 有动作按钮的气泡不自动消失（对齐原版 close=false：需操作后才关闭）
        if (IsActionBubbleActive)
        {
            _bubbleTimer.Stop();
            return;
        }
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(durationMs ?? defaultDuration);
        _bubbleTimer.Tick -= BubbleTimer_Tick;
        _bubbleTimer.Tick += BubbleTimer_Tick;
        _bubbleTimer.Start();
    }

    private void HideBubble()
    {
        _bubbleTimer.Stop();
        _bubbleTimer.Tick -= BubbleTimer_Tick;
        BubblePopup.IsOpen = false;
        HideOverlay();
    }

    private void BubbleTimer_Tick(object? sender, EventArgs e) => HideBubble();

    /// <summary>气泡动作点击回调（由 PetWindowManager 订阅处理，对应 DesktopPet.vue handleBubbleAction）</summary>
    public event Action<StockReview.Core.Services.BubbleAction>? BubbleActionPerformed;

    /// <summary>是否正显示待操作的动作气泡（需操作后才能消失，不允许被互动文案气泡顶掉）</summary>
    private bool IsActionBubbleActive => BubblePopup.IsOpen && BubbleActionsPanel.Visibility == Visibility.Visible;

    /// <summary>
    /// 渲染气泡动作按钮：无动作时隐藏面板并显示 × 关闭按钮（对齐 PetBubble.vue actions/close 互斥逻辑）。
    /// </summary>
    private void RenderBubbleActions(IReadOnlyList<StockReview.Core.Services.BubbleAction>? actions)
    {
        BubbleActionsPanel.Children.Clear();
        if (actions != null)
        {
            foreach (var a in actions)
            {
                if (string.IsNullOrEmpty(a.Type)) continue;
                var btn = new System.Windows.Controls.Button
                {
                    Content = string.IsNullOrEmpty(a.Label) ? a.Type : CleanActionLabel(a.Label),
                    Tag = a,
                    Style = (Style)FindResource("BubbleActionBtnStyle")
                };
                btn.Click += BubbleAction_Click;
                BubbleActionsPanel.Children.Add(btn);
            }
        }
        var hasActions = BubbleActionsPanel.Children.Count > 0;
        BubbleActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        BubbleCloseBtn.Visibility = hasActions ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>去掉动作按钮文案的装饰性 emoji 前缀（历史数据存有带对勾前缀的标签，易被误认为复选框图标）</summary>
    private static string CleanActionLabel(string label)
    {
        var t = label.TrimStart('\u2705', '\u23F0', '\u26A0', ' ');
        return string.IsNullOrEmpty(t) ? label : t;
    }

    private void BubbleAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: StockReview.Core.Services.BubbleAction action })
        {
            Log.Information("[宠物] 气泡动作点击: {Type}", action.Type);
            BubbleActionPerformed?.Invoke(action);
        }
    }

    /// <summary>显示全屏遮罩（对应 FullscreenOverlayEnabled，critical 提示时全屏警示）。</summary>
    private void ShowOverlay()
    {
        if (_currentSettings.FullscreenOverlayEnabled)
        {
            _overlayWindow ??= new OverlayWindow();
            _overlayWindow.Show();
        }
    }

    private void HideOverlay()
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.Hide();
            _overlayWindow.Close();
            _overlayWindow = null;
        }
    }

    /// <summary>
    /// 气泡定位（对齐 Electron setBubbleLayout / DesktopPet.bubblePlacement）。
    /// 默认优先居中在宠物头顶（top）；仅当上方空间不足时回退到 right / left。
    /// </summary>
    private CustomPopupPlacement[] BubblePlacementMethod(Size popupSize, Size targetSize, Point offset)
    {
        const double GAP = 10;
        const double SAFE = 6;
        const double MAX_W = 320;

        var targetTL = SpriteControl.PointToScreen(new Point(0, 0));
        double targetW = targetSize.Width > 0 ? targetSize.Width : Math.Max(1, SpriteControl.ActualWidth);
        double targetH = targetSize.Height > 0 ? targetSize.Height : Math.Max(1, SpriteControl.ActualHeight);
        double bubbleW = Math.Min(MAX_W, Math.Max(100, popupSize.Width));
        double bubbleH = Math.Max(50, popupSize.Height);
        var work = GetScreenWorkArea(targetTL);
        double roomTop = targetTL.Y - work.Top;
        double roomLeft = targetTL.X - work.Left;
        double roomRight = work.Right - (targetTL.X + targetW);
        double bubbleLeftWhenTop = targetTL.X + targetW / 2 - bubbleW / 2;
        bool topFitsH = bubbleLeftWhenTop >= work.Left + SAFE &&
                        bubbleLeftWhenTop + bubbleW <= work.Right - SAFE;
        bool topFitsV = roomTop >= bubbleH + GAP + SAFE;
        double x, y;
        if (topFitsH && topFitsV)
        {
            // top：水平居中于宠物头顶（对齐 Electron placement='top'）
            x = (targetW - bubbleW) / 2;
            y = -(bubbleH + GAP);
        }
        else if (roomRight >= bubbleW + GAP + SAFE || roomRight >= roomLeft)
        {
            // right：右侧垂直居中（对齐 Electron placement='right'）
            x = targetW + GAP;
            y = (targetH - bubbleH) / 2;
        }
        else
        {
            // left：左侧垂直居中（对齐 Electron placement='left'）
            x = -bubbleW - GAP;
            y = (targetH - bubbleH) / 2;
        }
        var placement = new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal);
        return new CustomPopupPlacement[] { placement };
    }

    /// <summary>获取宠物所在屏幕的工作区（WPF 逻辑像素），用于气泡空间判定</summary>
    private Rect GetScreenWorkArea(Point screenPointDip)
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
            double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            // Screen.FromPoint 需要物理像素；PointToScreen 返回 DIP，先换算
            var fPoint = new System.Drawing.Point((int)(screenPointDip.X * sx), (int)(screenPointDip.Y * sy));
            var screen = System.Windows.Forms.Screen.FromPoint(fPoint);
            return new Rect(
                screen.WorkingArea.Left / sx,
                screen.WorkingArea.Top / sy,
                screen.WorkingArea.Width / sx,
                screen.WorkingArea.Height / sy);
        }
        catch
        {
            return new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        }
    }

    // 气泡分类样式：标题 + 边框色 + 背景色 + 背景渐变终止色（对齐 PetBubble.vue 各 level 配色）
    private sealed record BubbleStyle(string Title, System.Windows.Media.Color Border, System.Windows.Media.Color Fill, System.Windows.Media.Color FillEnd);

    private static readonly Dictionary<string, BubbleStyle> BubbleStyles = new()
    {
        // 对齐 PetBubble.vue level-default：暖色渐变 + 琥珀边框
        ["encourage"] = new("加油鸭", Color.FromArgb(0x59, 0xFF, 0xB7, 0x4D), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF5, 0xEE)),
        ["hint"] = new("小提醒", Color.FromArgb(0x59, 0xFF, 0xB7, 0x4D), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF5, 0xEE)),
        ["tease"] = new("小吐槽", Color.FromArgb(0x59, 0xFF, 0xB7, 0x4D), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF5, 0xEE)),
        ["playful"] = new("开心一刻", Color.FromArgb(0x59, 0xFF, 0xB7, 0x4D), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF5, 0xEE)),
        // 对齐 PetBubble.vue level-alert：琥珀边框加深
        ["alert"] = new("注意啦", Color.FromArgb(0xFF, 0xFF, 0xB7, 0x4D), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF3, 0xE0)),
        // 对齐 PetBubble.vue level-warning：橙色边框 + 脉冲
        ["warning"] = new("警告", Color.FromArgb(0xFF, 0xFF, 0x8A, 0x65), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF5, 0xEE)),
        // 对齐 PetBubble.vue level-force：红色边框 + 红色阴影
        ["force"] = new("加急！", Color.FromArgb(0xFF, 0xEF, 0x53, 0x50), Color.FromArgb(0xFF, 0xFF, 0xF8, 0xF0), Color.FromArgb(0xFF, 0xFF, 0xF0, 0xF0)),
        // 对齐 PetBubble.vue level-critical：金红色 + 加粗边框 + 脉冲
        ["critical"] = new("紧急", Color.FromArgb(0xFF, 0xFF, 0x6F, 0x00), Color.FromArgb(0xFF, 0xFF, 0xF3, 0xE0), Color.FromArgb(0xFF, 0xFF, 0xE0, 0xB2)),
    };

    /// <summary>
    /// 设置宠物心情
    /// </summary>
    public void SetMood(string mood)
    {
        SpriteControl.Mood = mood;
    }

    /// <summary>
    /// 设置点击穿透
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (enabled)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }
        else
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
        }
    }

    /// <summary>按持久化设置对宠物窗口生效（点穿透/拖拽开关/尺寸缩放/透明度/动画速度等）。</summary>
    public void ApplyPetSettings(PetSettings s)
    {
        _currentSettings = s;

        SetClickThrough(s.ClickThrough);
        DragMoveEnabled = s.DragMoveEnabled;
        Width = 200 * s.PetSize;
        Height = 240 * s.PetSize;

        if (DataContext is PetViewModel vm)
        {
            vm.PetSize = 140 * s.PetSize;
            vm.PetOpacity = s.PetOpacity;
            vm.AnimationSpeed = s.AnimationSpeed;
        }
    }

    /// <summary>打开设置面板时接线「保存→生效」。</summary>
    private void EnsureSettingsWired(Panels.PetSettingsPanel panel)
    {
        if (_settingsWired) return;
        _settingsWired = true;
        if (panel.DataContext is PetSettingsPanelViewModel vm)
            vm.SettingsSaved += () => ApplyPetSettings(PetSettingsStore.Load());
    }

    // === 位置持久化 ===

    private void LoadSavedPosition()
    {
        try
        {
            var statePath = Path.Combine(App.DataDir, "pet-window-state.json");
            if (File.Exists(statePath))
            {
                var json = File.ReadAllText(statePath);
                var state = JsonSerializer.Deserialize<PetWindowState>(json);
                if (state != null)
                {
                    // 确保位置在屏幕可见范围内
                    Left = Math.Max(0, state.X);
                    Top = Math.Max(0, state.Y);
                }
            }
            else
            {
                // 默认在右下角
                Left = SystemParameters.WorkArea.Width - 220;
                Top = SystemParameters.WorkArea.Height - 260;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 加载位置失败");
            Left = SystemParameters.WorkArea.Width - 220;
            Top = SystemParameters.WorkArea.Height - 260;
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _savePosTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        // 强制气泡 Popup 重新定位（DragMove 期间 Popup 不会自动跟随窗口移动）
        if (BubblePopup.IsOpen)
        {
            BubblePopup.HorizontalOffset += 0.1;
            BubblePopup.HorizontalOffset -= 0.1;
        }
        // 面板窗口跟随宠物窗口移动（用户手动定位过的面板不跟随，位置记忆）
        if (_panelWindow is { IsVisible: true, UserMoved: false })
            PositionPanelWindow();
        _savePosTimer.Stop();
        _savePosTimer.Start();
    }

    private void SavePosition()
    {
        try
        {
            var statePath = Path.Combine(App.DataDir, "pet-window-state.json");
            var state = new PetWindowState { X = Left, Y = Top };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(statePath, json);
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口前立即清理气泡 & 遮罩 & 菜单（WPF Popup 是独立 HWND，
        // 若不显式关闭，父 Win 关后 Popup 会"漂浮"到 Dispatcher 超时才消失）
        try
        {
            _bubbleTimer.Stop();
            _bubbleTimer.Tick -= BubbleTimer_Tick;
            BubblePopup.IsOpen = false;
            MenuPopup.IsOpen = false;
            HideOverlay();
            _panelWindow?.Close();
        }
        catch { /* 清理失败不阻塞关闭 */ }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 双保险：OnClosing 被取消等情况仍在 Closed 兜底清理
        try
        {
            _bubbleTimer.Stop();
            _bubbleTimer.Tick -= BubbleTimer_Tick;
            BubblePopup.IsOpen = false;
            MenuPopup.IsOpen = false;
            HideOverlay();
            _panelWindow?.Close();
        }
        catch { /* no-op */ }
        base.OnClosed(e);
    }

    /// <summary>外部（App.OnExit / Tray 退出）调用：立即关闭气泡 & 窗口（同步）。</summary>
    public void ShutdownNow()
    {
        try
        {
            _bubbleTimer.Stop();
            _bubbleTimer.Tick -= BubbleTimer_Tick;
            BubblePopup.IsOpen = false;
            MenuPopup.IsOpen = false;
            HideOverlay();
            if (_panelWindow != null)
            {
                try { _panelWindow.Close(); } catch { }
            }
        }
        catch { }
        try
        {
            Close();
        }
        catch { }
    }

    // === Win32 API ===
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void SetPopupTopmost(Popup popup, bool topmost)
    {
        if (popup.Child == null) return;
        var hwndSource = (HwndSource?)PresentationSource.FromVisual(popup.Child);
        if (hwndSource == null) return;
        SetWindowPos(hwndSource.Handle, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    private void BubblePopup_Opened(object sender, EventArgs e)
    {
        if (Topmost)
            SetPopupTopmost(BubblePopup, true);
    }

    private void MenuPopup_Opened(object sender, EventArgs e)
    {
        if (Topmost)
            SetPopupTopmost(MenuPopup, true);
    }

    // === 枚举 ===
    private enum PetPanelType
    {
        None,
        PlanList,
        Reminder,
        History,
        Gallery,
        Settings,
        Intraday
    }

    private class PetWindowState
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}

/// <summary>全屏半透明遮罩窗（对应 FullscreenOverlayEnabled 全屏警示效果）。</summary>
internal class OverlayWindow : Window
{
    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        // 覆盖主显示器工作区
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }
}
