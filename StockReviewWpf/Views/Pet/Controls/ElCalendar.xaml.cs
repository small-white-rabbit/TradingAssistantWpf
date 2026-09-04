using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StockReviewWpf.Views.Pet.Controls;

/// <summary>
/// 自绘 el-date-picker 日历（跨对话框共用）：年月切换 + 禁用过期日 + 今天高亮 + 选中蓝底。
/// 弃用系统 Calendar——透明 Popup 内渲染异常且样式不可控。
///
/// 命中测试三条硬约束（违反任意一条都会表现为"点哪选到哪的另一格 / 切月弹层消失"）：
/// 1. 月份箭头用不可聚焦的 Border，不用 Button（Button 的焦点变更事件带 KeyboardDevice，
///    会被上层"按键关闭弹层"钩子误判；且按下时抢 Mouse Capture 干扰 Popup 自关闭逻辑）。
/// 2. 格子命中区与视觉格子严格重合（固定行高 + 显式 32×32 居中），不用星号行自适应。
/// 3. 提交需"按下与抬起落在同一格"，消除"在 A 格按下、拖到 B 格抬起选中 B"的拖拽误选。
/// </summary>
public partial class ElCalendar : UserControl
{
    /// <summary>选中日期（写入时会归一化到当天 00:00，便于与格子日期直接相等比较）</summary>
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(ElCalendar),
            new PropertyMetadata(null, (d, _) => ((ElCalendar)d).RenderMonth()));

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value?.Date);
    }

    /// <summary>禁用今天之前（原版 disablePastDate）</summary>
    public bool DisablePast { get; set; } = true;

    public event EventHandler<DateTime>? DateSelected;

    private DateTime _viewMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);

    /// <summary>按下阶段记录的格子日期；抬起时须一致才提交（防拖拽误选）</summary>
    private DateTime? _pressedCell;

    private static readonly Brush SelBg = Frozen("#409EFF");
    private static readonly Brush HoverBg = Frozen("#ECF5FF");
    private static readonly Brush ArrowHoverBg = Frozen("#F2F6FC");
    private static readonly Brush Transparent = Brushes.Transparent;
    private static readonly Brush SelFg = Brushes.White;
    private static readonly Brush InMonthFg = Frozen("#606266");
    private static readonly Brush OutMonthFg = Frozen("#C0C4CC");
    private static readonly Brush DisabledFg = Frozen("#DCDFE6");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public ElCalendar()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderMonth();
        // 冒泡到末尾清空按下记录：避免"在别处按下、抬手落在日期格"时用陈旧记录误提交。
        // handledEventsToo=true：格子处理器即使把事件标记 Handled 也要执行。
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _pressedCell = null), true);
    }

    public void SetViewMonth(DateTime month)
    {
        _viewMonth = new DateTime(month.Year, month.Month, 1);
        // 弹窗每次打开都会 SetViewMonth：若 SelectedDate 与上次相同则不触发属性回调，
        // 必须在此强制重渲染，否则日历停在用户上次浏览的月份（IsLoaded 保护构造期调用）
        if (IsLoaded) RenderMonth();
    }

    private void PrevMonth(object sender, RoutedEventArgs e) => ShiftMonth(-1);

    private void NextMonth(object sender, RoutedEventArgs e) => ShiftMonth(1);

    private void ShiftMonth(int delta)
    {
        _viewMonth = _viewMonth.AddMonths(delta);
        RenderMonth();
    }

    private void Arrow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = ArrowHoverBg;
    }

    private void Arrow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = Transparent;
    }

    private void RenderMonth()
    {
        // 格子整体重建，按下记录必须一并失效
        _pressedCell = null;

        MonthTitle.Text = $"{_viewMonth.Year}年{_viewMonth.Month}月";
        DaysGrid.Children.Clear();
        var today = DateTime.Today;
        var first = new DateTime(_viewMonth.Year, _viewMonth.Month, 1);
        var leading = (int)first.DayOfWeek; // 周日=0 与格子列对齐
        var selected = SelectedDate?.Date;

        var cellDate = first.AddDays(-leading);
        for (var i = 0; i < 42; i++, cellDate = cellDate.AddDays(1))
        {
            var d = cellDate;
            var inMonth = d.Month == _viewMonth.Month;
            var isPast = DisablePast && d < today;
            var isToday = d == today;
            var isSelected = selected == d;

            var bg = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Background = isSelected ? SelBg : Transparent,
                BorderBrush = isToday && !isSelected ? SelBg : Transparent,
                BorderThickness = new Thickness(isToday && !isSelected ? 1 : 0),
                Child = new TextBlock
                {
                    Text = d.Day.ToString(), FontSize = 12,
                    Foreground = isSelected ? SelFg : isPast ? DisabledFg : inMonth ? InMonthFg : OutMonthFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };

            // 命中区 = 视觉格子（32×32 居中，与固定行高三一一对应），圆形日期底居中于命中区。
            // 无 Button：不可聚焦、不抢 Mouse Capture，工厂模板里 Border 直挂事件最直接。
            var hit = new Border
            {
                Width = 32, Height = 32,
                Background = Transparent,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = isPast ? null : Cursors.Hand,
                Child = bg,
            };
            if (!isPast && !isSelected)
            {
                hit.MouseEnter += (_, _) => hit.Background = HoverBg;
                hit.MouseLeave += (_, _) => hit.Background = Transparent;
            }
            if (!isPast)
            {
                hit.MouseLeftButtonDown += (_, _) => _pressedCell = d;
                hit.MouseLeftButtonUp += (_, _) =>
                {
                    // 按下与抬起不在同一格（拖拽划过）→ 不提交，避免选到邻格
                    if (_pressedCell != d) return;
                    SelectedDate = d;
                    DateSelected?.Invoke(this, d);
                };
            }
            Grid.SetRow(hit, i / 7);
            Grid.SetColumn(hit, i % 7);
            DaysGrid.Children.Add(hit);
        }
    }
}
