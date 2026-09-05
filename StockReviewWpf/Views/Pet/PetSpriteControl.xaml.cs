using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

namespace StockReviewWpf.Views.Pet;

/// <summary>
/// 精灵图集渲染器 — WPF 版，对应 Vue 版 PetSpriteBody.vue (622 行)
///
/// 渲染来自 awesome-codex-pet 的 spritesheet.webp 精灵图集。
/// 图集布局：
///   单元格 192x208，8 列
///   V1 (9 行)：idle/running-right/running-left/waving/jumping/failed/waiting/running/review
///   V2 (11 行)：上面 9 行 + 16 个环视方向（2 行 x 8）
///   V3 (11 行)：V1 9 行 + 第 9 行休息动作 + 第 10 行工作动作
///
/// 心情 -> 行 映射：
///   idle/sleeping   -> 0 (idle)
///   working         -> 10 (working) V3 / 8 (review) V1/V2
///   focused         -> 8 (review)
///   nervous/anxious  -> 6 (waiting)
///   excited          -> 4 (jumping)
///   celebrating/happy -> 3 (waving)
///   angry/crying/forbidden -> 5 (failed)
///   walking (poseKey) -> 1 (running-right) 或 2 (running-left)
///   resting (V3)     -> 9 (resting)
/// </summary>
public partial class PetSpriteControl : UserControl
{
    // === 图集常量 ===
    private const int CELL_WIDTH = 192;
    private const int CELL_HEIGHT = 208;
    private const int COLUMNS = 8;
    private const int V1_ROWS = 9;
    private const int V2_ROWS = 11;
    private const int V3_ROWS = 11;

    // 每行帧数与每帧时长（毫秒）
    private static readonly Dictionary<int, (int frames, int[] durations)> RowDurations = new()
    {
        [0]  = (6, new[] { 975, 390, 390, 488, 488, 1131 }),                // idle
        [1]  = (8, new[] { 429, 429, 429, 429, 429, 429, 429, 780 }),      // running-right
        [2]  = (8, new[] { 429, 429, 429, 429, 429, 429, 429, 780 }),      // running-left
        [3]  = (4, new[] { 507, 507, 507, 975 }),                          // waving
        [4]  = (5, new[] { 507, 507, 507, 507, 975 }),                      // jumping
        [5]  = (8, new[] { 507, 507, 507, 507, 507, 507, 507, 858 }),       // failed
        [6]  = (6, new[] { 546, 546, 546, 546, 546, 936 }),                 // waiting
        [7]  = (6, new[] { 429, 429, 429, 429, 429, 780 }),                 // running
        [8]  = (6, new[] { 546, 546, 546, 546, 546, 975 }),                 // review
        [9]  = (8, new[] { 585, 585, 585, 585, 585, 585, 585, 936 }),       // look-000-157 (V2)
        [10] = (8, new[] { 585, 585, 585, 585, 585, 585, 585, 936 }),       // look-180-337 (V2)
    };

    // V3 行覆盖
    private static readonly Dictionary<int, (int frames, int[] durations)> V3RowOverride = new()
    {
        [9]  = (6, new[] { 975, 390, 390, 488, 488, 1131 }),               // resting
        [10] = (6, new[] { 1300, 650, 650, 812, 812, 1900 })               // working
    };

    // mood -> row 映射
    private static readonly Dictionary<string, int> MoodToRow = new()
    {
        ["idle"] = 0, ["sleeping"] = 0, ["resting"] = 0,
        ["focused"] = 8, ["working"] = 8,
        ["nervous"] = 6, ["anxious"] = 6,
        ["excited"] = 4,
        ["celebrating"] = 3, ["happy"] = 3,
        ["angry"] = 5, ["crying"] = 5, ["forbidden"] = 5,
    };

    // 随机额外动作候选行
    private static readonly int[] AllVarietyRows = { 3, 4, 6, 7 };
    private const int VarietyMinDelay = 30 * 60 * 1000; // 30 分钟

    // === 依赖属性 ===
    public static readonly DependencyProperty PetIdProperty =
        DependencyProperty.Register(nameof(PetId), typeof(string), typeof(PetSpriteControl),
            new PropertyMetadata("firefly--lingxiaotian", OnPetIdChanged));

    public static readonly DependencyProperty SpriteVersionProperty =
        DependencyProperty.Register(nameof(SpriteVersion), typeof(int), typeof(PetSpriteControl),
            new PropertyMetadata(3, OnSpriteVersionChanged));

    public static readonly DependencyProperty MoodProperty =
        DependencyProperty.Register(nameof(Mood), typeof(string), typeof(PetSpriteControl),
            new PropertyMetadata("idle", OnMoodChanged));

    public static readonly DependencyProperty PoseKeyProperty =
        DependencyProperty.Register(nameof(PoseKey), typeof(string), typeof(PetSpriteControl),
            new PropertyMetadata("default", OnInputChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(PetSpriteControl),
            new PropertyMetadata(140.0, OnSizeChanged));

    public static readonly DependencyProperty PetOpacityProperty =
        DependencyProperty.Register(nameof(PetOpacity), typeof(double), typeof(PetSpriteControl),
            new PropertyMetadata(1.0, OnOpacityChanged));

    public static readonly DependencyProperty IsDraggingProperty =
        DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(PetSpriteControl),
            new PropertyMetadata(false, OnInputChanged));

    public static readonly DependencyProperty DragDirectionProperty =
        DependencyProperty.Register(nameof(DragDirection), typeof(string), typeof(PetSpriteControl),
            new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty MouseAngleProperty =
        DependencyProperty.Register(nameof(MouseAngle), typeof(double?), typeof(PetSpriteControl),
            new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty AnimationSpeedProperty =
        DependencyProperty.Register(nameof(AnimationSpeed), typeof(double), typeof(PetSpriteControl),
            new PropertyMetadata(1.0));

    public string PetId { get => (string)GetValue(PetIdProperty); set => SetValue(PetIdProperty, value); }
    public int SpriteVersion { get => (int)GetValue(SpriteVersionProperty); set => SetValue(SpriteVersionProperty, value); }
    public string Mood { get => (string)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public string PoseKey { get => (string)GetValue(PoseKeyProperty); set => SetValue(PoseKeyProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double PetOpacity { get => (double)GetValue(PetOpacityProperty); set => SetValue(PetOpacityProperty, value); }
    public bool IsDragging { get => (bool)GetValue(IsDraggingProperty); set => SetValue(IsDraggingProperty, value); }
    public string? DragDirection { get => (string?)GetValue(DragDirectionProperty); set => SetValue(DragDirectionProperty, value); }
    public double? MouseAngle { get => (double?)GetValue(MouseAngleProperty); set => SetValue(MouseAngleProperty, value); }
    public double AnimationSpeed { get => (double)GetValue(AnimationSpeedProperty); set => SetValue(AnimationSpeedProperty, value); }

    // === 渲染状态 ===
    private int _currentRow;
    private int _currentFrame;
    private BitmapSource? _spritesheet;
    private double _naturalWidth;
    private double _naturalHeight;
    /// <summary>行 -> 各列预裁剪帧。帧切换只换 Source，避免大图集平移触发分层窗口整窗重绘（闪烁根因）。</summary>
    private Dictionary<int, BitmapSource[]>? _frames;

    // 随机额外动作状态
    private int? _varietyRow;
    private double _varietyUntil;
    private double _nextVarietyAt;

    // 帧调度 — 复用单一 DispatcherTimer，避免每帧 new Timer 造成 GC 压力与内存泄漏
    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render);
    private double _lastFrameTime;
    private double _accumulatedMs;
    private bool _tickHooked;

    // === 全局鼠标追踪（对齐 Electron DesktopPet 的 V2 环视轮询） ===
    // Electron：getCursorScreenPoint + getPetWindowScreenBounds，窗口中心求 atan2；
    // 角度变化 >3° 用 300ms 快轮询（鼠标在动），否则 1000ms 慢轮询；距中心 <5px 不更新；拖拽中暂停。
    // 仅 V2 精灵存在 16 方向环视帧（图集第 9/10 行），V3 第 9/10 行是休息/工作动作，不追踪。
    private const double MouseTrackFastMs = 300;
    private const double MouseTrackSlowMs = 1000;
    private readonly DispatcherTimer _mouseTrackTimer = new(DispatcherPriority.Background);
    private double? _lastMouseAngle;
    private bool _mouseTrackFast = true;
    private bool _mouseTrackHooked;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    // ZZZ 动画
    private Storyboard? _zzzStoryboard;

    public PetSpriteControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 启动时随机延迟 15-35 秒后开始第一次随机动作
        _nextVarietyAt = Environment.TickCount64 + 15000 + new Random().NextDouble() * 20000;
        PreloadSprite();
        UpdateLayout();
        Advance();
        StartMouseTracking();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _frameTimer.Stop();
        _tickHooked = false;
        _mouseTrackTimer.Stop();
    }

    // === 精灵图加载 ===
    private void PreloadSprite()
    {
        try
        {
            var petsDir = Path.Combine(App.DataDir, "pets");
            var petDir = Path.Combine(petsDir, PetId);
            var spritePath = Path.Combine(petDir, "spritesheet.png");

            // 优先 PNG（WPF 原生），回退 WebP（在线宠物包仅提供 .webp，经 Imazen.WebP 解码）
            if (!File.Exists(spritePath))
            {
                spritePath = Path.Combine(petDir, "spritesheet.webp");
            }

            if (!File.Exists(spritePath))
            {
                Log.Warning("[宠物] 精灵图未找到: {Path}", spritePath);
                return;
            }

            _spritesheet = string.Equals(Path.GetExtension(spritePath), ".webp", StringComparison.OrdinalIgnoreCase)
                ? LoadWebpSprite(spritePath)
                : LoadPngSprite(spritePath);
            if (_spritesheet == null) return;

            _naturalWidth = _spritesheet.PixelWidth;
            _naturalHeight = _spritesheet.PixelHeight;

            BuildFrames();
            UpdateSpriteLayout();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[宠物] 精灵图加载失败");
        }
    }

    private static BitmapSource LoadPngSprite(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path, UriKind.Absolute);
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static BitmapSource? LoadWebpSprite(string path)
    {
        try
        {
            var pixels = Imazen.WebP.WebPDecoder.Decode(
                File.ReadAllBytes(path), out var width, out var height,
                Imazen.WebP.WebPPixelFormat.Bgra);
            var source = BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            // WebP 解码失败必须可见：在线宠物包仅 webp，静默吞掉会导致宠物空白
            Log.Error(ex, "[宠物] WebP 精灵图解码失败: {Path}", path);
            return null;
        }
    }

    // === 布局计算 ===
    private void UpdateSpriteLayout()
    {
        var frameWidth = Size;
        var frameHeight = Math.Round(Size * (CELL_HEIGHT / (double)CELL_WIDTH));

        RootGrid.Width = frameWidth;
        RootGrid.Height = frameHeight;
        RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, frameWidth, frameHeight));
        SpriteImage.Opacity = PetOpacity;

        UpdateSpriteFrame();
    }

    /// <summary>
    /// 加载时把图集按行/列裁剪为独立小位图（CroppedBitmap 冻结，GPU 可缓存）。
    /// 行高/列宽均按图集实际尺寸均分：不同宠物包网格可能非 192x208，
    /// 固定像素裁剪会导致帧错位（"精灵图显示不完全"的根因）。
    /// 行数优先按图集高度推断（格高=格宽×208/192），版本声明仅作回退。
    /// </summary>
    private void BuildFrames()
    {
        _frames = null;
        if (_spritesheet == null || _naturalWidth <= 0 || _naturalHeight <= 0) return;

        try
        {
            var totalRows = InferTotalRows();
            var naturalH = (int)_naturalHeight;
            var naturalW = (int)_naturalWidth;
            var map = new Dictionary<int, BitmapSource[]>();

            for (var r = 0; r < totalRows; r++)
            {
                var y0 = (int)Math.Round(_naturalHeight * r / totalRows);
                var y1 = (int)Math.Round(_naturalHeight * (r + 1) / totalRows);
                if (y1 <= y0 || y0 >= naturalH) continue;

                var cells = new BitmapSource[COLUMNS];
                for (var c = 0; c < COLUMNS; c++)
                {
                    // 列也按宽度均分，兼容格宽≠192 的图集
                    var x0 = (int)Math.Round(_naturalWidth * c / COLUMNS);
                    var x1 = (int)Math.Round(_naturalWidth * (c + 1) / COLUMNS);
                    if (x1 <= x0 || x0 >= naturalW) break;
                    var crop = new CroppedBitmap(_spritesheet,
                        new System.Windows.Int32Rect(x0, y0, x1 - x0, y1 - y0));
                    crop.Freeze();
                    cells[c] = crop;
                }
                map[r] = cells;
            }

            _frames = map;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[宠物] 精灵帧裁剪失败");
        }
    }

    /// <summary>按图集几何推断行数（格宽=W/8，格高=格宽×208/192）；与版本声明不符时以几何为准</summary>
    private int InferTotalRows()
    {
        var cellW = _naturalWidth / COLUMNS;
        if (cellW < 8) return GetTotalRows();
        var cellH = cellW * (CELL_HEIGHT / (double)CELL_WIDTH);
        var inferred = (int)Math.Round(_naturalHeight / cellH);
        if (inferred is >= 9 and <= 12) return inferred;
        return GetTotalRows();
    }

    /// <summary>把当前 (行,帧) 对应的裁剪位图设为 Image 源；行/列越界时保留上一帧，避免精灵闪空白。</summary>
    private void UpdateSpriteFrame()
    {
        if (_frames == null) return;
        if (_frames.TryGetValue(_currentRow, out var cells)
            && _currentFrame >= 0 && _currentFrame < cells.Length
            && cells[_currentFrame] != null)
        {
            SpriteImage.Source = cells[_currentFrame];
        }
    }

    // === 帧调度（事件驱动，对应 PetSpriteBody.vue 的 advance 函数） ===
    private void Advance()
    {
        var now = Environment.TickCount64;
        if (_lastFrameTime == 0) _lastFrameTime = now;
        var delta = Math.Min(now - _lastFrameTime, 100.0);
        _lastFrameTime = now;

        // 随机动作触发/结束
        MaybeTriggerVariety(now);
        EndVarietyIfDue(now);

        var desiredRow = ComputeRow();
        double nextDelay;

        if (IsLookRow(desiredRow) && MouseAngle.HasValue)
        {
            if (_currentRow != desiredRow)
            {
                _currentRow = desiredRow;
                _accumulatedMs = 0;
            }
            var col = AngleToLookCol(MouseAngle.Value);
            if (_currentFrame != col)
            {
                _currentFrame = col;
                UpdateSpriteFrame();
            }
            nextDelay = 500;
        }
        else
        {
            if (_currentRow != desiredRow)
            {
                _currentRow = desiredRow;
                _currentFrame = 0;
                _accumulatedMs = 0;
                UpdateSpriteFrame();
            }

            var dur = GetRowDuration(_currentRow);
            if (dur.frames > 0)
            {
                _accumulatedMs += delta;
                var safety = 0;
                while (_accumulatedMs >= dur.durations[_currentFrame] && safety < dur.frames)
                {
                    _accumulatedMs -= dur.durations[_currentFrame];
                    _currentFrame = (_currentFrame + 1) % dur.frames;
                    safety++;
                }
                UpdateSpriteFrame();
                nextDelay = Math.Max(16, dur.durations[_currentFrame] - _accumulatedMs);
            }
            else
            {
                nextDelay = 500;
            }
        }

        // 随机动作到期唤醒
        if (_varietyRow.HasValue)
        {
            nextDelay = Math.Min(nextDelay, Math.Max(0, _varietyUntil - now));
        }
        else if (now < _nextVarietyAt)
        {
            nextDelay = Math.Min(nextDelay, Math.Max(0, _nextVarietyAt - now));
        }

        ScheduleFrame(nextDelay);
    }

    private void ScheduleFrame(double delayMs)
    {
        _frameTimer.Stop();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, delayMs));
        if (!_tickHooked)
        {
            _frameTimer.Tick += OnFrameTick;
            _tickHooked = true;
        }
        _frameTimer.Start();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        _frameTimer.Stop();
        Advance();
    }

    // === 全局鼠标追踪（V2 精灵 16 方向环视，对齐 Electron DesktopPet 轮询） ===

    private void StartMouseTracking()
    {
        if (!_mouseTrackHooked)
        {
            _mouseTrackTimer.Tick += OnMouseTrackTick;
            _mouseTrackHooked = true;
        }
        _lastMouseAngle = null;
        _mouseTrackFast = true;
        _mouseTrackTimer.Interval = TimeSpan.FromMilliseconds(MouseTrackFastMs);
        _mouseTrackTimer.Start();
    }

    /// <summary>
    /// 轮询全局光标位置，以精灵帧中心（= Electron 紧凑宠物窗口的中心）为原点计算角度写入 MouseAngle。
    /// PointToScreen 与 GetCursorPos 同为物理像素坐标系，高 DPI 下角度无畸变。
    /// </summary>
    private void OnMouseTrackTick(object? sender, EventArgs e)
    {
        // 仅 V2 有环视帧；拖拽中暂停（ComputeRow 里拖拽行优先级更高，Electron 同样跳过轮询）
        if (SpriteVersion != 2 || IsDragging)
        {
            SetMouseTrackInterval(fast: true);
            return;
        }

        try
        {
            if (!GetCursorPos(out var pt)) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            // 精灵帧中心（Electron 窗口紧贴精灵，bounds 中心即帧中心；
            // WPF 宠物窗口是 200x240 透明窗口，精灵底部对齐，故不能用窗口矩形）
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            double dx = pt.X - center.X;
            double dy = pt.Y - center.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 5) return; // 光标基本在中心，保持上一次角度

            var angle = (180.0 * Math.Atan2(dy, dx) / Math.PI + 360.0) % 360.0;

            // 角度变化 >3° → 快轮询；稳定 → 慢轮询（对齐 Electron p>3 ? za(300) : Ea(1000)）
            var fast = true;
            if (_lastMouseAngle is { } last)
            {
                var d = Math.Abs(angle - last);
                if (d > 180) d = 360 - d;
                fast = d > 3;
            }
            _lastMouseAngle = angle;
            SetMouseTrackInterval(fast);
            MouseAngle = angle;
        }
        catch (Exception ex)
        {
            // 鼠标追踪失败不影响主动画
            Log.Debug(ex, "[宠物] 鼠标追踪轮询异常");
        }
    }

    /// <summary>切换快/慢轮询节拍（状态未变时不重启计时器）。</summary>
    private void SetMouseTrackInterval(bool fast)
    {
        if (fast == _mouseTrackFast) return;
        _mouseTrackFast = fast;
        _mouseTrackTimer.Stop();
        _mouseTrackTimer.Interval = TimeSpan.FromMilliseconds(fast ? MouseTrackFastMs : MouseTrackSlowMs);
        _mouseTrackTimer.Start();
    }

    // === 行计算 ===
    private int ComputeRow()
    {
        // 1) 随机额外动作
        if (_varietyRow.HasValue) return _varietyRow.Value;

        // 2) 拖拽中：使用行走方向
        if (IsDragging && DragDirection != null)
        {
            return DragDirection == "left" ? 2 : 1;
        }

        // 3) V2 环视
        if (SpriteVersion == 2 && MouseAngle.HasValue && IsIdleLike())
        {
            return AngleToLookRowCol(MouseAngle.Value).row;
        }

        // 4) walking pose
        if (PoseKey == "walking") return 1;

        // 5) mood 映射
        return GetMoodRow(Mood);
    }

    private int GetMoodRow(string mood)
    {
        if (SpriteVersion == 3)
        {
            if (mood == "sleeping" || mood == "resting") return 9;
            if (mood == "working") return 10;
        }
        return MoodToRow.TryGetValue(mood, out var row) ? row : 0;
    }

    private int GetTotalRows()
    {
        if (SpriteVersion == 2) return V2_ROWS;
        if (SpriteVersion == 3) return V3_ROWS;
        return V1_ROWS;
    }

    private (int frames, int[] durations) GetRowDuration(int row)
    {
        if (SpriteVersion == 3 && V3RowOverride.TryGetValue(row, out var v3))
        {
            return ScaleDuration(v3);
        }
        if (RowDurations.TryGetValue(row, out var base_))
        {
            return ScaleDuration(base_);
        }
        return (0, Array.Empty<int>());
    }

    private (int frames, int[] durations) ScaleDuration((int frames, int[] durations) base_)
    {
        if (Math.Abs(AnimationSpeed - 1.0) < 0.01) return base_;
        var scaled = new int[base_.durations.Length];
        for (var i = 0; i < base_.durations.Length; i++)
            scaled[i] = (int)Math.Round(base_.durations[i] * AnimationSpeed);
        return (base_.frames, scaled);
    }

    private bool IsLookRow(int row)
    {
        if (SpriteVersion == 3) return false;
        return row == 9 || row == 10;
    }

    private bool IsIdleLike() => Mood == "idle";

    // 角度 -> 环视行/列（16 方向，22.5 度间隔）
    private (int row, int col) AngleToLookRowCol(double angle)
    {
        var idx = (int)(Math.Round(((angle % 360 + 360) % 360) / 22.5) % 16);
        if (idx < 8) return (9, idx);
        return (10, idx - 8);
    }

    private int AngleToLookCol(double angle) => AngleToLookRowCol(angle).col;

    // === 随机额外动作 ===
    private void MaybeTriggerVariety(double now)
    {
        if (_varietyRow.HasValue) return;
        if (now < _nextVarietyAt) return;
        if (!IsIdleLike()) return;
        if (IsDragging) return;
        if (SpriteVersion == 2 && MouseAngle.HasValue) return;

        var primaryRow = GetMoodRow(Mood);
        var candidates = new List<int>();
        foreach (var r in AllVarietyRows)
            if (r != primaryRow) candidates.Add(r);
        if (SpriteVersion == 3) candidates.Add(8);

        if (candidates.Count == 0) return;
        var pick = candidates[new Random().Next(candidates.Count)];
        var dur = GetRowDuration(pick);
        if (dur.frames == 0) return;

        _varietyRow = pick;
        _currentFrame = 0;
        _accumulatedMs = 0;

        var oneCycle = 0;
        foreach (var d in dur.durations) oneCycle += d;
        _varietyUntil = now + oneCycle;
    }

    private void EndVarietyIfDue(double now)
    {
        if (_varietyRow.HasValue && now >= _varietyUntil)
        {
            _varietyRow = null;
            _currentFrame = 0;
            _accumulatedMs = 0;
            _nextVarietyAt = now + VarietyMinDelay;
        }
    }

    // === ZZZ 浮动符号 ===
    private void UpdateZzz()
    {
        var show = !_varietyRow.HasValue && (Mood == "sleeping" || Mood == "resting");
        ZzzCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _zzzStoryboard == null)
        {
            StartZzzAnimation();
        }
        else if (!show && _zzzStoryboard != null)
        {
            _zzzStoryboard.Stop();
            _zzzStoryboard = null;
        }
    }

    private void StartZzzAnimation()
    {
        // 对应 CSS @keyframes zzz-float
        var zzzs = new[] { (Zzz1, 0.0), (Zzz2, 0.75), (Zzz3, 1.5) };
        _zzzStoryboard = new Storyboard();

        foreach (var (tb, delay) in zzzs)
        {
            var opacityAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(3),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));

            var xAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(3),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
            xAnim.KeyFrames.Add(new LinearDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));

            var yAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(3),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            yAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            yAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
            yAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
            yAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-24, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));

            Storyboard.SetTarget(opacityAnim, tb);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(TextBlock.OpacityProperty));
            Storyboard.SetTarget(xAnim, tb);
            Storyboard.SetTargetProperty(xAnim, new PropertyPath("(Canvas.Left)"));
            Storyboard.SetTarget(yAnim, tb);
            Storyboard.SetTargetProperty(yAnim, new PropertyPath("(Canvas.Top)"));

            _zzzStoryboard.Children.Add(opacityAnim);
            _zzzStoryboard.Children.Add(xAnim);
            _zzzStoryboard.Children.Add(yAnim);
        }

        _zzzStoryboard.Begin();
    }

    // === 依赖属性回调 ===
    private static void OnPetIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c)
        {
            c._currentRow = 0;
            c._currentFrame = 0;
            c._accumulatedMs = 0;
            c._varietyRow = null;
            c._naturalWidth = 0;
            c._naturalHeight = 0;
            c._frames = null;
            c._nextVarietyAt = Environment.TickCount64 + 15000 + new Random().NextDouble() * 20000;
            c.PreloadSprite();
            c.Advance();
        }
    }

    private static void OnSpriteVersionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c) c.Advance();
    }

    private static void OnMoodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c)
        {
            if (c._varietyRow.HasValue)
            {
                c._varietyRow = null;
                c._nextVarietyAt = Environment.TickCount64 + VarietyMinDelay;
            }
            c._currentFrame = 0;
            c._accumulatedMs = 0;
            c.UpdateZzz();
            c.Advance();
        }
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c) c.Advance();
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c) c.UpdateSpriteLayout();
    }

    private static void OnOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PetSpriteControl c) c.SpriteImage.Opacity = c.PetOpacity;
    }
}
