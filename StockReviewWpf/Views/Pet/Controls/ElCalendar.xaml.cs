using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StockReviewWpf.Views.Pet.Controls;

/// <summary>
/// 自绘 el-date-picker 日历（跨对话框共用）：年月切换 + 禁用过期日 + 今天高亮 + 选中蓝底。
/// 弃用系统 Calendar——透明 Popup 内渲染异常且样式不可控。
/// </summary>
public partial class ElCalendar : UserControl
{
    /// <summary>选中日期</summary>
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(ElCalendar),
            new PropertyMetadata(null, (d, _) => ((ElCalendar)d).RenderMonth()));

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>禁用今天之前（原版 disablePastDate）</summary>
    public bool DisablePast { get; set; } = true;

    public event EventHandler<DateTime>? DateSelected;

    private DateTime _viewMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);

    private static readonly Brush SelBg = Frozen("#409EFF");
    private static readonly Brush HoverBg = Frozen("#ECF5FF");
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
    }

    public void SetViewMonth(DateTime month)
    {
        _viewMonth = new DateTime(month.Year, month.Month, 1);
        // 弹窗每次打开都会 SetViewMonth：若 SelectedDate 与上次相同则不触发属性回调，
        // 必须在此强制重渲染，否则日历停在用户上次浏览的月份（IsLoaded 保护构造期调用）
        if (IsLoaded) RenderMonth();
    }

    private void PrevMonth(object sender, RoutedEventArgs e)
    {
        _viewMonth = _viewMonth.AddMonths(-1);
        RenderMonth();
    }

    private void NextMonth(object sender, RoutedEventArgs e)
    {
        _viewMonth = _viewMonth.AddMonths(1);
        RenderMonth();
    }

    private void RenderMonth()
    {
        MonthTitle.Text = $"{_viewMonth.Year}年{_viewMonth.Month}月";
        DaysGrid.Children.Clear();
        var today = DateTime.Today;
        var first = new DateTime(_viewMonth.Year, _viewMonth.Month, 1);
        var leading = (int)first.DayOfWeek; // 周日=0 与格子列对齐

        var cellDate = first.AddDays(-leading);
        for (var i = 0; i < 42; i++, cellDate = cellDate.AddDays(1))
        {
            var d = cellDate;
            var inMonth = d.Month == _viewMonth.Month;
            var isPast = DisablePast && d < today;
            var isToday = d == today;
            var isSelected = SelectedDate == d;

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

            // 透明命中区 + 圆形日期底（无 Button：factory 模板不能挂实例，Border 直挂事件更直接）
            var hit = new Border
            {
                Background = Transparent,
                CornerRadius = new CornerRadius(6),
                Cursor = isPast ? null : System.Windows.Input.Cursors.Hand,
                Child = bg,
            };
            if (!isPast && !isSelected)
            {
                hit.MouseEnter += (_, _) => hit.Background = HoverBg;
                hit.MouseLeave += (_, _) => hit.Background = Transparent;
            }
            if (!isPast)
                hit.MouseLeftButtonUp += (_, _) =>
                {
                    SelectedDate = d;
                    DateSelected?.Invoke(this, d);
                };
            Grid.SetRow(hit, i / 7);
            Grid.SetColumn(hit, i % 7);
            DaysGrid.Children.Add(hit);
        }
    }
}
