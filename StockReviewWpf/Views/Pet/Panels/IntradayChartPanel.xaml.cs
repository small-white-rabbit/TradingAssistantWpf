using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Futu;
using StockReview.Core.MarketData;
using StockReview.Core.Services;
using ScottPlot;

namespace StockReviewWpf.Views.Pet.Panels;

/// <summary>
/// 分时图面板：标准 A 股分时结构 —— 09:30-15:00 交易分钟时间轴（午休压缩）、
/// 价格区以昨收为中轴对称（左轴价格/右轴涨跌幅）、价格线+均价线+昨收红绿填充、底部分钟量能柱。
/// 数据链路：富途订阅推送(实时增量) + 富途轮询(初始全量) → 东财 → 腾讯 → 新浪。
/// </summary>
public partial class IntradayChartPanel : UserControl
{
    private readonly MarketDataAggregator? _aggregator;
    private readonly FutuAdapter? _futu;
    private readonly ReminderHistoryService? _reminderHistory;

    // 当前展示状态（供富途订阅推送实时刷新）
    private string _currentCode = "";
    private string _currentStockName = "";
    private string _lastSourceSuffix = "";
    private DateTime _lastFutuConnectAttempt = DateTime.MinValue;
    private List<IntradayPoint> _points = new();
    private DateTime _lastLiveRender = DateTime.MinValue;

    // 涨幅基准与均价累计状态（推送增量更新用）
    private decimal _preClose;
    private long _cumVolume;
    private decimal _cumAmount;

    /// <summary>提醒列表开关持久化键（appConfig 表，"1"=开 "0"=关）</summary>
    private const string ReminderSwitchKey = "intraday_reminder_list_visible";

    /// <summary>开关切换：控制列表主体显示并持久化到 appConfig</summary>
    private void ReminderSwitch_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 IsChecked="True" 会触发 Checked 事件，此时列表元素尚未实例化 → 空引用
        if (ReminderListPanel == null) return;
        var on = ReminderSwitch.IsChecked == true;
        ReminderListPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var db = App.Host?.Services.GetService(typeof(StockReview.Core.Data.DatabaseService)) as StockReview.Core.Data.DatabaseService;
            db?.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = ReminderSwitchKey,
                ["value"] = on ? "1" : "0"
            });
        }
        catch { /* 持久化失败不影响交互 */ }
    }

    // 深色主题配色（与 Electron 版 IntradayChartWindow 对齐）
    private static readonly ScottPlot.Color BgColor = new(15, 20, 32);
    private static readonly ScottPlot.Color GridColor = new(30, 36, 51);
    private static readonly ScottPlot.Color TickColor = new(144, 153, 170);
    private static readonly ScottPlot.Color PriceLineColor = new(255, 255, 255);   // 价格线（正白）
    private static readonly ScottPlot.Color AvgLineColor = new(0xFF, 0xD7, 0x00); // 均价线（正黄，虚线）
    private static readonly ScottPlot.Color UpColor = new(245, 108, 108);          // 红=涨
    private static readonly ScottPlot.Color DownColor = new(103, 194, 58);         // 绿=跌
    private static readonly ScottPlot.Color DividerColor = new(42, 48, 64);
    private static readonly ScottPlot.Color HighMarkColor = new(0xfb, 0x92, 0x3c); // 最高标记（橙）
    private static readonly ScottPlot.Color LowMarkColor = new(0x22, 0xc5, 0x5e);  // 最低标记（绿）

    public IntradayChartPanel()
    {
        InitializeComponent();
        _aggregator = App.Host?.Services.GetRequiredService<MarketDataAggregator>();
        _futu = App.Host?.Services.GetService(typeof(FutuAdapter)) as FutuAdapter;
        _reminderHistory = App.Host?.Services.GetService(typeof(ReminderHistoryService)) as ReminderHistoryService;
        Loaded += (_, _) => RenderEmpty();

        // 提醒列表开关：初始值从 appConfig 恢复（持久化在 ReminderSwitch_Changed）
        try
        {
            var db = App.Host?.Services.GetService(typeof(StockReview.Core.Data.DatabaseService)) as StockReview.Core.Data.DatabaseService;
            var row = db?.GetById("appConfig", ReminderSwitchKey);
            if (row != null && row.TryGetValue("value", out var v) && v is "0")
                ReminderSwitch.IsChecked = false;
        }
        catch { /* 读取失败用默认开 */ }

        // 富途订阅推送：秒级实时更新分时末端（订阅制优先于轮询刷新）
        // 注意：面板由 PetWindow.ShowPanel 动态挂载/摘除（缓存复用），
        // 必须按 Loaded/Unloaded 配对订阅——Loaded 里先 -= 再 += 保证幂等，
        // 否则既会在切走后泄漏（持控件引用），也会在切回后丢失推送。
        Loaded += (_, _) =>
        {
            if (_futu == null) return;
            _futu.OnQuotePush -= OnFutuQuotePush;   // 防重复订阅
            _futu.OnQuotePush += OnFutuQuotePush;
        };
        Unloaded += (_, _) =>
        {
            if (_futu != null)
                _futu.OnQuotePush -= OnFutuQuotePush;
        };
    }

    /// <summary>外部入口：加载指定股票的分时图（计划列表点击股票名调用）。</summary>
    public void LoadStock(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;
        _ = QueryAsync(stockCode);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // 关闭浮动面板：面板现在宿主在独立 PetPanelWindow，退回其 Owner 宠物窗口关闭
        if (Window.GetWindow(this) is Views.Pet.PetPanelWindow panelWindow &&
            panelWindow.Owner is Views.Pet.PetWindow petWindow)
            petWindow.ClosePanel();
    }

    /// <summary>面板标题栏拖动宿主面板窗口（分时图弹窗隐藏了窗口级标题栏，由面板标题头承担拖动）。</summary>
    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            Window.GetWindow(this) is Views.Pet.PetPanelWindow panelWindow)
            panelWindow.DragFromContent();
    }

    /// <summary>加载指定股票分时（外部 LoadStock 或初始化调用，无查询栏）。</summary>
    private async Task QueryAsync(string code)
    {
        if (string.IsNullOrEmpty(code)) return;
        if (_aggregator == null)
        {
            StatusText.Text = "行情服务不可用";
            return;
        }

        StatusText.Text = $"正在获取 {code} 分时数据…";
        try
        {
            // 富途优先：未连接 OpenD 时主动连接（60s 失败冷却，避免每次查询都阻塞 ~1s）
            await EnsureFutuConnectedAsync();

            var points = await _aggregator.GetIntradayAsync(code);
            if (points.Count == 0)
            {
                StatusText.Text = $"未获取到 {code} 的分时数据（可能非交易时段或数据源不可用）";
                RenderEmpty();
                return;
            }

            _currentCode = code;
            _currentStockName = "";
            _points = points;
            InitLiveState(points);
            RenderChart(points);
            DrawReminderMarkers(code);

            // 富途订阅制：OpenD 已连接时订阅该股，后续走秒级推送实时刷新（优先于手动轮询）
            var subscribed = false;
            if (_futu is { IsConnected: true })
                subscribed = _futu.Subscribe(new List<string> { code });

            // 标签：订阅请求已受理（或该股已在订阅列表）= 实时推送链路已建立，直接标"富途-订阅"；
            // 否则回退显示初始数据的获取方式（GetKL 请求-应答，"源-轮询"）。
            // 旧逻辑要等"收到推送且价格变化"才切换标签，午休/收盘后打开会一直误显示"富途-轮询"。
            UpdateStatusText(subscribed ? "[富途-订阅]"
                : _aggregator.LastIntradaySource is { } src ? $"[{src}-轮询]" : "");
            StatusText.Text = ""; // 数据已在顶部信息栏展示，清掉"正在获取…"提示
            Serilog.Log.Information("[分时图] {Code} 加载成功: {Count} 点 源={Source} 订阅={Sub}",
                code, points.Count, _aggregator.LastIntradaySource ?? "?", subscribed);

            // 股票名称：经通用行情链（东财→腾讯→新浪）异步获取，到达后刷新状态栏
            _ = LoadStockNameAsync(code);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取失败: {ex.Message}";
            RenderEmpty();
        }
    }

    /// <summary>初始化涨幅基准与均价累计状态（供富途推送增量更新）</summary>
    private void InitLiveState(List<IntradayPoint> points)
    {
        _preClose = points[^1].PreClose;
        if (_preClose <= 0) _preClose = points[0].Price; // 数据源未提供昨收时退化为首点基准
        _cumVolume = points.Sum(p => p.Volume);
        // 无分钟成交额的源用 价×量 近似累计
        _cumAmount = points.Sum(p => p.Amount > 0 ? p.Amount : p.Price * p.Volume);
    }

    /// <summary>
    /// 查询前确保富途 OpenD 已连接（富途是分时降级链首源）：
    /// Connect 内含 ~1s 回调等待，放后台线程避免卡 UI；失败后 60s 冷却内不再重试。
    /// </summary>
    private async Task EnsureFutuConnectedAsync()
    {
        if (_futu == null || _futu.IsConnected) return;
        if ((DateTime.Now - _lastFutuConnectAttempt).TotalSeconds < 60) return;
        _lastFutuConnectAttempt = DateTime.Now;

        var ok = await Task.Run(() => _futu.Connect());
        Serilog.Log.Information("[分时图] 查询前主动连接富途: {Result}", ok ? "成功" : "失败");
    }

    /// <summary>股票名称：经通用行情链（东财→腾讯→新浪）获取，成功后刷新状态栏显示</summary>
    private async Task LoadStockNameAsync(string code)
    {
        try
        {
            var quote = await _aggregator!.GetQuoteAsync(code);
            if (quote is { Name.Length: > 0 } && SameCode(_currentCode, code))
            {
                _currentStockName = quote.Name.Trim();
                UpdateStatusText(_lastSourceSuffix);
            }
        }
        catch
        {
            // 名称获取失败不影响分时展示
        }
    }

    /// <summary>更新顶部信息栏（对应 Electron header）：名称/代码/源标签 + 价格/涨跌（红涨绿跌）</summary>
    private void UpdateStatusText(string suffix)
    {
        _lastSourceSuffix = suffix ?? "";
        if (_points.Count == 0) return;
        var last = _points[^1];
        var chg = _preClose > 0 ? last.Price - _preClose : 0;
        var pct = _preClose > 0 ? (last.Price - _preClose) / _preClose * 100 : last.ChangePercent;
        var conv = new System.Windows.Media.BrushConverter();
        var brush = (System.Windows.Media.Brush)conv.ConvertFromString(pct >= 0 ? "#F87171" : "#4ADE80")!;

        // 左侧：名称（大）+ 代码（小）+ 数据源标签
        var hasName = !string.IsNullOrWhiteSpace(_currentStockName);
        StockNameText.Text = hasName ? _currentStockName : "--";
        StockCodeText.Text = _currentCode;
        var tag = _lastSourceSuffix.Trim('[', ']');
        if (tag.Length > 0)
        {
            SourceTagText.Text = tag;
            SourceTag.Visibility = Visibility.Visible;
            // 标签格式「源-方式」区分获取特征（对齐 Electron）：富途-订阅=推送（绿），其余-轮询（琥珀）
            var push = tag.EndsWith("-订阅");
            SourceTag.Background = (System.Windows.Media.Brush)conv.ConvertFromString(push ? "#16351F" : "#3D2E10")!;
            SourceTag.BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(push ? "#2A7D46" : "#8A6420")!;
            SourceTagText.Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(push ? "#4ADE80" : "#FBBF24")!;
        }
        else SourceTag.Visibility = Visibility.Collapsed;

        // 右侧：价格（大）+ 涨跌额/涨跌幅
        PriceText.Text = last.Price.ToString("0.00");
        PriceText.Foreground = brush;
        ChangeText.Text = $"{chg:+0.00;-0.00}  {pct:+0.00;-0.00}%";
        ChangeText.Foreground = brush;

        // 标题栏：📈 名称 分时图（对应 Electron title-bar-text）
        TitleText.Text = $"📈 {(hasName ? _currentStockName : _currentCode)} 分时图";
    }

    /// <summary>富途推送回调（富途线程）：按分钟增量更新分时末端并节流重绘</summary>
    private void OnFutuQuotePush(string stockCode, decimal price, long volume, decimal turnover)
    {
        if (_points.Count == 0 || price <= 0) return;
        if (!SameCode(stockCode, _currentCode)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if ((DateTime.Now - _lastLiveRender).TotalMilliseconds < 500) return;
            var last = _points[^1];
            if (last.Price == price) return;

            // 推送的 volume/turnover 为当日累计值，先转分钟增量
            var deltaVol = Math.Max(0, volume - _cumVolume);
            var deltaAmt = turnover > _cumAmount ? turnover - _cumAmount : 0m;
            _cumVolume += deltaVol;
            _cumAmount += Math.Max(deltaAmt, price * deltaVol);

            var now = DateTime.Now;
            if (now - last.Time >= TimeSpan.FromMinutes(1))
            {
                // 新分钟：追加分时点
                _points.Add(new IntradayPoint
                {
                    Time = now,
                    Price = price,
                    AvgPrice = _cumVolume > 0 ? _cumAmount / _cumVolume : price,
                    Volume = deltaVol,
                    Amount = deltaAmt,
                    PreClose = _preClose,
                    ChangePercent = _preClose > 0 ? (price - _preClose) / _preClose * 100 : 0
                });
                if (_points.Count > 241) _points.RemoveAt(0); // 分时图保留全天
            }
            else
            {
                // 同一分钟：更新末点价格/量额与均价、涨幅
                last.Price = price;
                last.Volume += deltaVol;
                last.Amount += deltaAmt;
                last.AvgPrice = _cumVolume > 0 ? _cumAmount / _cumVolume : price;
                last.ChangePercent = _preClose > 0 ? (price - _preClose) / _preClose * 100 : 0;
            }

            _lastLiveRender = DateTime.Now;
            RenderChart(_points);
            DrawReminderMarkers(_currentCode);
            UpdateStatusText("[富途-订阅]");
        }, DispatcherPriority.Background);
    }

    /// <summary>宽松比较股票代码：忽略 SH/SZ 前缀与大小写</summary>
    private static bool SameCode(string a, string b)
    {
        var da = new string(a.Where(char.IsDigit).ToArray());
        var db = new string(b.Where(char.IsDigit).ToArray());
        return da.Length > 0 && da == db;
    }

    private void RenderEmpty()
    {
        _points = new List<IntradayPoint>();
        EmptyOverlay.Visibility = Visibility.Visible;
        // 重置顶部信息栏（对应 Electron header 初始态）
        StockNameText.Text = "--";
        StockCodeText.Text = "";
        PriceText.Text = "--";
        PriceText.Foreground = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFromString("#9AA4B8")!;
        ChangeText.Text = "";
        SourceTag.Visibility = Visibility.Collapsed;
        TitleText.Text = "📈 分时图";
        var plot = Chart.Plot;
        plot.Clear();
        StyleDarkTheme(plot);
        Chart.Refresh();
    }

    /// <summary>深色主题基础样式（背景/网格/刻度）</summary>
    private static void StyleDarkTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = BgColor;
        plot.DataBackground.Color = BgColor;
        plot.Grid.MajorLineColor = GridColor;
        foreach (ScottPlot.IAxis axis in new ScottPlot.IAxis[] { plot.Axes.Left, plot.Axes.Bottom, plot.Axes.Right })
        {
            axis.TickLabelStyle.ForeColor = TickColor;
            axis.Label.ForeColor = TickColor;
            axis.MajorTickStyle.Color = TickColor;
        }
    }

    /// <summary>
    /// 渲染标准分时图（对应 Electron 版 draw）：
    /// 上区（约 74%）价格区 —— 以昨收为中轴上下对称（振幅不小于 ±3%），
    /// 白色价格线 + 黄色虚线均价线 + 昨收红绿半透明填充 + 昨收标签 + 最高/最低标记；
    /// 下区（约 26%）量能区 —— 分钟量柱，红=较上一分钟上涨，绿=下跌。
    /// 左轴价格 / 右轴涨跌幅（像素级对齐），X 轴为交易分钟索引（午休压缩）。
    /// </summary>
    private void RenderChart(List<IntradayPoint> points)
    {
        EmptyOverlay.Visibility = Visibility.Collapsed;
        var plot = Chart.Plot;
        plot.Clear();
        StyleDarkTheme(plot);

        var preClose = _preClose > 0 ? _preClose : points[0].Price;
        var pc = (double)preClose;

        var xs = points.Select(p => (double)MinuteIndex(p.Time)).ToArray();
        var prices = points.Select(p => (double)p.Price).ToArray();
        var avgs = points.Select(p => (double)p.AvgPrice).ToArray();
        var totalMin = ChartWindowMinutes(points);

        // 价格区：以昨收为中线，上下对称；振幅取价格与均价的最大涨跌幅，至少 ±3%
        // （对应 Electron symPct = max(limitPct * 1.05, 3)，避免窄幅波动被过分放大）
        double limitPct = prices.Concat(avgs.Where(v => v > 0))
            .Select(v => Math.Abs(v - pc) / pc * 100).DefaultIfEmpty(3).Max();
        var symPct = Math.Max(limitPct * 1.05, 3.0);
        var dev = pc * symPct / 100.0;
        var yTop = pc + dev;
        var yBottom = pc - dev;
        var volBand = (yTop - yBottom) * 0.26; // 底部量能带
        var yMin = yBottom - volBand;

        // 昨收红绿填充（价格线下行沿昨收回填的闭合多边形）
        var coords = new List<Coordinates>();
        for (var i = 0; i < xs.Length; i++) coords.Add(new Coordinates(xs[i], prices[i]));
        for (var i = xs.Length - 1; i >= 0; i--) coords.Add(new Coordinates(xs[i], pc));
        var fill = plot.Add.Polygon(coords.ToArray());
        fill.FillColor = (prices[^1] >= pc ? UpColor : DownColor).WithAlpha(0.16);
        fill.LineWidth = 0;

        // 价格线（正白） / 均价线（正黄虚线）
        var priceLine = plot.Add.Scatter(xs, prices);
        priceLine.Color = PriceLineColor;
        priceLine.LineWidth = 1.5f;
        priceLine.MarkerSize = 0;
        if (avgs.Any(v => v > 0))
        {
            var avgLine = plot.Add.Scatter(xs, avgs);
            avgLine.Color = AvgLineColor;
            avgLine.LineWidth = 1.2f;
            avgLine.LinePattern = ScottPlot.LinePattern.Dashed;
            avgLine.MarkerSize = 0;
        }

        // 昨收中轴虚线 + 昨收标签
        var pcLine = plot.Add.HorizontalLine(pc);
        pcLine.Color = TickColor.WithAlpha(0.7);
        pcLine.LinePattern = ScottPlot.LinePattern.Dashed;
        pcLine.LineWidth = 1;
        var pcLabel = plot.Add.Text($"昨收 {preClose:0.00}", -2, pc);
        pcLabel.Alignment = ScottPlot.Alignment.LowerLeft;
        pcLabel.LabelFontColor = TickColor;
        pcLabel.LabelFontSize = 9;

        // 价格区/量能区分隔线
        var divider = plot.Add.HorizontalLine(yBottom);
        divider.Color = DividerColor;
        divider.LineWidth = 1;

        // 量能区：分钟量柱
        var maxV = Math.Max(points.Max(p => (double)p.Volume), 1);
        var bars = new List<ScottPlot.Bar>();
        for (var i = 0; i < points.Count; i++)
        {
            var upBar = i == 0 || points[i].Price >= points[i - 1].Price;
            bars.Add(new ScottPlot.Bar
            {
                Position = xs[i],
                ValueBase = yMin + volBand * 0.05,
                Value = yMin + volBand * 0.05 + (double)points[i].Volume / maxV * volBand * 0.85,
                FillColor = (upBar ? UpColor : DownColor).WithAlpha(0.85),
            });
        }
        plot.Add.Bars(bars);

        // 日内最高/最低点标记：从标记点向右延伸虚线 + 圆点 + 右端价格标注
        var hiIdx = 0; var loIdx = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (prices[i] > prices[hiIdx]) hiIdx = i;
            if (prices[i] < prices[loIdx]) loIdx = i;
        }
        MarkHighLow(plot, xs[hiIdx], prices[hiIdx], $"最高 {prices[hiIdx]:F2}", HighMarkColor, totalMin);
        MarkHighLow(plot, xs[loIdx], prices[loIdx], $"最低 {prices[loIdx]:F2}", LowMarkColor, totalMin);

        // 当前价圆点（颜色随涨跌）
        var lastDot = plot.Add.Marker(xs[^1], prices[^1], ScottPlot.MarkerShape.FilledCircle, 5);
        lastDot.Color = prices[^1] >= pc ? UpColor : DownColor;

        // X 轴：交易时间刻度（午休压缩；上午窗口与全天窗口刻度不同）
        if (totalMin == 120)
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                new double[] { 0, 30, 60, 90, 120 },
                new[] { "09:30", "10:00", "10:30", "11:00", "11:30" });
        else
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                new double[] { 0, 60, 120, 180, 240 },
                new[] { "09:30", "10:30", "11:30/13:00", "14:00", "15:00" });

        // 左轴：昨收对称价格刻度；右轴：对应涨跌幅刻度
        var tickVals = new[] { pc - dev, pc - dev / 2, pc, pc + dev / 2, pc + dev };
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            tickVals, tickVals.Select(v => v.ToString("F2")).ToArray());
        var pctVals = tickVals.Select(v => (v - pc) / pc * 100).ToArray();
        plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            pctVals, pctVals.Select(v => v.ToString("+0.00;-0.00;0.00") + "%").ToArray());

        plot.Axes.SetLimits(-4, totalMin + 4, yMin, yTop);

        // 右轴百分比刻度：透明占位曲线挂到右轴并单独设限，使右轴涨跌幅与左轴价格像素级对齐
        var pctTop = (yTop - pc) / pc * 100;
        var pctBottom = (yMin - pc) / pc * 100;
        var anchor = plot.Add.Scatter(new double[] { -4, totalMin + 4 }, new double[] { pctBottom, pctTop });
        anchor.Axes.YAxis = plot.Axes.Right;
        anchor.Color = PriceLineColor.WithAlpha(0);
        anchor.LineWidth = 0;
        anchor.MarkerSize = 0;
        plot.Axes.SetLimits(-4, totalMin + 4, pctBottom, pctTop, plot.Axes.Bottom, plot.Axes.Right);

        Chart.Refresh();
    }

    /// <summary>最高/最低点标记：向右延伸的半透明虚线 + 圆点 + 右端上方标注</summary>
    private static void MarkHighLow(ScottPlot.Plot plot, double x, double y, string label,
        ScottPlot.Color color, int totalMin)
    {
        var line = plot.Add.Line(x, y, totalMin + 4, y);
        line.Color = color.WithAlpha(0.45);
        line.LinePattern = ScottPlot.LinePattern.Dashed;
        line.LineWidth = 1;

        var marker = plot.Add.Marker(x, y, ScottPlot.MarkerShape.FilledCircle, 5);
        marker.Color = color;

        var txt = plot.Add.Text(label, totalMin + 2, y);
        txt.Alignment = ScottPlot.Alignment.LowerRight;
        txt.LabelFontColor = color;
        txt.LabelFontSize = 9;
    }

    /// <summary>
    /// 当前图表窗口总分钟数（对应 Electron chartWindowMinutes）：
    /// 查看今日数据且未到 13:00 → 仅上午 120（上午一整段铺满画布）；
    /// 其余（13:00 后 / 历史 / 非交易日）→ 全天 240。
    /// </summary>
    private static int ChartWindowMinutes(List<IntradayPoint> points)
    {
        if (points.Count > 0 && points[^1].Time.Date == DateTime.Today && DateTime.Now < DateTime.Today.AddHours(13))
            return 120;
        return 240;
    }

    /// <summary>
    /// 分时时间 → 交易分钟索引（对应 Electron tradingMinuteOf）：
    /// 09:30-11:30 → 0-120；午休 11:30-13:00 → 120（上午末尾）；13:00-15:00 → 120-240（午休拼接无间隙）
    /// </summary>
    private static int MinuteIndex(DateTime t)
    {
        var m = (int)(t - t.Date.AddHours(9).AddMinutes(30)).TotalMinutes;
        if (m <= 120) return Math.Max(0, m);
        return Math.Min(240, 120 + Math.Max(0, (int)(t - t.Date.AddHours(13)).TotalMinutes));
    }

    /// <summary>
    /// 在分时图上渲染当日该股的提醒标记 + 填充下方提醒列表（对齐 Electron IntradayChartWindow）：
    /// 图表上仅画一个色点（红=严重/强制，琥珀=警告，灰=提示），提醒内容收纳到底部列表，
    /// 不在图上绘制文字（旧版在图上贴标题文字，密集时互相遮挡且压缩图表可用空间）。
    /// </summary>
    private void DrawReminderMarkers(string code)
    {
        if (_reminderHistory == null || _points.Count == 0) return;
        try
        {
            var today = TradePlanService.FormatLocalDate(DateTime.Now);
            var records = _reminderHistory.History
                .Where(h => h.DateStr == today
                            && !string.IsNullOrEmpty(h.StockCode)
                            && SameCode(h.StockCode!, code))
                .OrderBy(r => r.Timestamp)
                .ToList();

            // 无记录：模块整体隐藏
            if (records.Count == 0)
            {
                ReminderModule.Visibility = Visibility.Collapsed;
                return;
            }

            var plot = Chart.Plot;
            var rows = new List<ReminderRow>(records.Count);

            foreach (var r in records)
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp).LocalDateTime;
                // 映射到时间不晚于提醒时间的最后一个分时点
                var idx = _points.FindLastIndex(p => p.Time <= time);
                if (idx < 0) idx = 0;
                var x = (double)MinuteIndex(_points[idx].Time);
                var price = _points[idx].Price;

                var isCritical = r.Level is "critical" or "force";
                var isWarning = r.Level is "warning" or "alert";
                var color = isCritical ? new ScottPlot.Color(245, 108, 108)
                          : isWarning ? new ScottPlot.Color(230, 162, 60)
                          : new ScottPlot.Color(144, 153, 170);

                // 图上仅画标记点（不带竖线/文字）
                var marker = plot.Add.Marker(x, (double)price);
                marker.MarkerShape = isCritical ? MarkerShape.FilledDiamond : MarkerShape.FilledCircle;
                marker.Color = color;
                marker.Size = 8;

                // 下方列表行：时间 + 色点 + 标签 + 触发价
                var fullLabel = string.IsNullOrEmpty(r.Title) ? (r.Content ?? "") : r.Title;
                rows.Add(new ReminderRow
                {
                    Time = time.ToString("HH:mm"),
                    DotColor = isCritical ? "#F56C6C" : isWarning ? "#E6A23C" : "#9099AA",
                    Label = fullLabel.Length > 18 ? fullLabel[..18] + "…" : fullLabel,
                    FullLabel = fullLabel,
                    Price = $"触发 {price:0.00}"
                });
            }

            ReminderList.ItemsSource = rows;
            ReminderCountText.Text = $"共 {rows.Count} 条";
            // 模块显示：开关关闭时仅隐藏列表主体（标题行+开关常驻，可随时重新打开）
            ReminderModule.Visibility = Visibility.Visible;
            ReminderListPanel.Visibility = ReminderSwitch.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;

            Chart.Refresh();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[分时图] 渲染提醒标记失败 {Code}", code);
        }
    }

    /// <summary>提醒列表行（XAML DataTemplate 绑定）</summary>
    private class ReminderRow
    {
        public string Time { get; set; } = "";
        public string DotColor { get; set; } = "#9099AA";
        public string Label { get; set; } = "";
        public string FullLabel { get; set; } = "";
        public string Price { get; set; } = "";
    }
}
