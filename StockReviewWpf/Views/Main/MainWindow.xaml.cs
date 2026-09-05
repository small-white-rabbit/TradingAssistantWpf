using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StockReviewWpf.Views.Main;

/// <summary>
/// 主窗口 - 对应原版的主窗口
/// frame:false 无边框窗口 + 自定义 TitleBar(36px) + NavBar(60px)
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 最大化时去圆角（窗口贴边后圆角会露底色）
        StateChanged += (s, e) =>
        {
            RootBorder.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0)
                : new CornerRadius(8);
        };
        // 内存治理（2026-09-06 v2）：隐藏到托盘时释放 WebView2 浏览器进程 + 提示 GC；
        // 恢复显示时重建"隐藏期被释放且当时正激活"的视图（详见 MainViewModel 两方法）
        IsVisibleChanged += (s, e) =>
        {
            if (DataContext is not ViewModels.Main.MainViewModel vm) return;
            if ((bool)e.NewValue) vm.RecoverWebViewsOnShow();
            else vm.ReleaseWebViewsOnHide();
        };
        // 首次导航卡顿修复：启动完成 1.5s 后（首页渲染完毕、避开启动高峰），
        // 在 UI 线程空闲时逐个预创建各导航视图进缓存 → 所有"首次导航"变缓存命中
        Loaded += async (s, e) =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            if (DataContext is ViewModels.Main.MainViewModel vm)
                // 丢弃 Task：预热完成不阻塞任何后续逻辑，但内部已有 try/catch，异常不会逃逸到 AppDomain。
                // 不用 await 是因为预热是后台优化，用户不应等待其完成才能开始交互。
                _ = vm.PreWarmViewCache();
        };
    }
    /// <summary>窗口真正关闭（非隐藏到托盘）时释放缓存的 WebView 资源</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as ViewModels.Main.MainViewModel)?.DisposeWebViewCache();
    }

    /// <summary>把预载的 WebView 视图挂到隐藏停靠区（触发 Loaded 后台加载页面）。
    /// 挂载前恢复 Hidden：停靠中的视图需要参与布局（measure/arrange）才能完成预热。
    /// （防御性处理：当前流程仅在启动预热期调用，理论上 dock 不会已折叠。）</summary>
    public void AddToPreloadDock(System.Windows.Controls.Control view)
    {
        PreloadDock.Visibility = Visibility.Hidden;
        PreloadDock.Children.Add(view);
    }

    /// <summary>预热完成后折叠预载停靠区。Hidden 的元素仍参与布局 pass——全量预热后
    /// 最多 7 棵完整视图树会在每次窗口 resize/布局变化时被无谓地 measure/arrange，
    /// Collapsed 彻底退出布局。子元素仍在视觉树中（Loaded 状态保持），后续从停靠区
    /// 摘除挂到内容区时正常触发 Unloaded→Loaded，行为与 Hidden 时完全一致。</summary>
    public void CollapsePreloadDock() => PreloadDock.Visibility = Visibility.Collapsed;

    /// <summary>从隐藏停靠区摘除视图（挂到内容区前必须先摘除，否则 WPF 报“已有父级”）</summary>
    public void RemoveFromPreloadDock(System.Windows.Controls.Control view)
    {
        if (PreloadDock.Children.Contains(view)) PreloadDock.Children.Remove(view);
    }

    /// <summary>视图当前是否挂在预载停靠区</summary>
    public bool IsPreloadDocked(System.Windows.Controls.Control view) => PreloadDock.Children.Contains(view);

    /// <summary>
    /// WindowStyle=None 最大化会铺满整个显示器（盖住任务栏，且 DPI 缩放下
    /// MaxHeight 工作区约束会错位导致底部黑边）。通过 WM_GETMINMAXINFO
    /// 把最大化约束到所在显示器的工作区（物理像素，多显示器/高 DPI 安全）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMaxPosition.x = info.rcWork.left;
                mmi.ptMaxPosition.y = info.rcWork.top;
                mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    // === Win32 最大化约束 ===
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.Main.MainViewModel vm)
        {
            Title = vm.AppTitle;
            vm.PropertyChanged += OnMainViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// 监听 CurrentView 变化，播放 fadeInUp 入场动画。
    /// 动画对象真正复用：Duration/Easing/两个 DoubleAnimation 均为静态单例，
    /// 每次导航仅 BeginAnimation 重启，零新增分配（旧实现每次 new Storyboard + 2 动画）。
    /// 时长 260ms/位移 14px（原 400ms/20px）：缓存命中时切换本身 &lt;10ms，
    /// 过长动画反而让用户"等动画"——260ms 是"有动效但不拖沓"的体感平衡点。
    /// </summary>
    private static readonly Duration _pageAnimDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly CubicEase _pageAnimEase = new() { EasingMode = EasingMode.EaseOut };
    private static readonly DoubleAnimation _pageFadeAnim = new(0, 1, _pageAnimDuration) { EasingFunction = _pageAnimEase };
    private static readonly DoubleAnimation _pageSlideAnim = new(14, 0, _pageAnimDuration) { EasingFunction = _pageAnimEase };

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.Main.MainViewModel.CurrentView)) return;
        if (ContentArea == null) return;

        // 确保 ContentArea 有 RenderTransform 容器
        if (ContentArea.RenderTransform is not TranslateTransform || ContentArea.RenderTransform.IsFrozen)
        {
            ContentArea.RenderTransform = new TranslateTransform(0, 0);
        }

        // 合成友好属性直启动画（Opacity + RenderTransform，GPU 合成不触发布局）。
        // WebView 页面（统计汇总等）一律跳过 20px 位移只做纯渐显：
        // - 内容已就绪：slide 表现为"载入后往上挪 20px"的布局跳动；
        // - 内容未就绪（预载未完成就导航）：slide 在播放中途页面渐显出现，
        //   收尾的 20px 上滑同样被感知为"载入后再往上调整"（RootGrid 渐显时
        //   ContentArea 的 slide 常仍在进行）。两种状态位移都只有害处，统一禁用。
        // WPF 原生页面动画从渐显中开始，位移无此感知，保留完整 fade+slide
        var isWebView = (DataContext as ViewModels.Main.MainViewModel)?.CurrentView
            is Views.Web.WebChartView;
        ContentArea.BeginAnimation(UIElement.OpacityProperty, _pageFadeAnim);
        if (!isWebView)
            ContentArea.RenderTransform.BeginAnimation(TranslateTransform.YProperty, _pageSlideAnim);

        // 导航耗时打点：内容替换后首个布局+渲染 pass（Render 优先级回调 = 布局完成后）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var viewName = (DataContext as ViewModels.Main.MainViewModel)?.CurrentView?.GetType().Name ?? "?";
        Dispatcher.BeginInvoke(() =>
        {
            Serilog.Log.Information("[导航] {View} 切换完成（布局+首帧渲染）耗时 {Ms}ms",
                viewName, sw.ElapsedMilliseconds);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// 标题栏拖拽
    /// </summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
        else if (e.ClickCount == 2)
        {
            // 双击最大化/还原
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// 最小化
    /// </summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// 最大化/还原
    /// </summary>
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else
            WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 刷新（重载当前视图）
    /// </summary>
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // 重新触发当前视图的加载
        if (DataContext is ViewModels.Main.MainViewModel vm)
        {
            vm.RefreshCurrentViewCommand?.Execute(null);
        }
    }

    /// <summary>
    /// 关闭窗口（驻留托盘，对应原版 关闭即最小化到托盘的行为）
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // 若托盘已初始化，则隐藏主窗口而非退出进程
        var tray = App.Host?.Services.GetService(typeof(StockReviewWpf.Services.TrayService)) as StockReviewWpf.Services.TrayService;
        if (tray != null && tray.IsInitialized)
        {
            Hide();
        }
        else
        {
            Close();
        }
    }
}


