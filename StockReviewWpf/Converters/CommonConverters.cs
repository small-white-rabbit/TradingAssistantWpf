using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StockReviewWpf.Converters;

/// <summary>
/// 数值乘法转换器（value × parameter），用于纸张轮播轨道位移
/// </summary>
public class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (double.TryParse(value?.ToString(), out var v) && double.TryParse(parameter?.ToString(), out var p))
            return v * p;
        return 0d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// 索引 +1（ItemsControl.AlternationIndex → 页码，从 1 开始）
/// </summary>
public class IndexPlusOneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (i + 1).ToString() : "1";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// 判断绑定值是否等于 ConverterParameter，返回 bool（用于 RadioButton IsChecked 绑定）
/// </summary>
public class EqualityToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return true;
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
            return parameter.ToString()!;
        return System.Windows.Data.Binding.DoNothing;
    }
}

/// <summary>
/// 反转布尔值
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }
}

/// <summary>
/// 非空转可见性
/// </summary>
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value != null;
        if (parameter is string p)
        {
            // 支持逗号组合（如 "inverse,notEmpty" = 空值时可见）：
            // notEmpty 先算基础可见性，inverse 再取反，单参数用法向后兼容
            var parts = p.Split(',').Select(x => x.Trim());
            if (parts.Contains("notEmpty", StringComparer.OrdinalIgnoreCase))
                visible = !string.IsNullOrWhiteSpace(value as string);
            if (parts.Contains("inverse", StringComparer.OrdinalIgnoreCase))
                visible = !visible;
        }
        return visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 字符串等于 ConverterParameter 时返回 Visible，否则 Collapsed
/// </summary>
public class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return System.Windows.Visibility.Visible;
        if (value == null || parameter == null) return System.Windows.Visibility.Collapsed;
        return value.ToString() == parameter.ToString()
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据字符串值的正负返回涨跌颜色画笔（红涨绿跌）。
/// 无参数：正/零 → UpBrush(红)，负 → DownBrush(绿)，空/null → TextSecondaryBrush。
/// ConverterParameter="#F56C6C|#67C23A"：用「涨色|跌色」覆盖主题画刷，
/// 用于卡片对齐 Electron 原版 Element Plus 涨跌配色（.up #f56c6c / .down #67c23a）。
/// </summary>
public class UpDownBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return TryFind("TextSecondaryBrush");
        var str = value.ToString() ?? "";
        var trimmed = str.TrimStart('+', ' ');
        if (string.IsNullOrEmpty(trimmed))
            return TryFind("TextSecondaryBrush");
        // 非数字不再当作"涨"染色，统一中性色
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return TryFind("TextSecondaryBrush");

        var palette = ParsePalette(parameter);
        if (palette != null)
            return num < 0 ? palette.Value.down : palette.Value.up;
        return num < 0 ? TryFind("DownBrush") : TryFind("UpBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static object TryFind(string key)
    {
        return System.Windows.Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }

    /// <summary>解析 "#up|#down" 形式的自定义涨跌配色；格式非法时返回 null（回退主题画刷）。</summary>
    private static (System.Windows.Media.Brush up, System.Windows.Media.Brush down)? ParsePalette(object? parameter)
    {
        if (parameter is not string spec) return null;
        var parts = spec.Split('|');
        if (parts.Length != 2) return null;
        try
        {
            var up = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(parts[0].Trim()));
            var down = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(parts[1].Trim()));
            up.Freeze();
            down.Freeze();
            return (up, down);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 成功率着色（复刻规格 1.1.4）：&gt;50% → DangerBrush(红 #F56C6C)，≤50% → SuccessBrush(绿 #67C23A)。
/// 入参为成功率数字（字符串，如 "66.6667"）。
/// </summary>
public class SuccessRateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return TryFind("TextSecondaryBrush");
        var s = value.ToString() ?? "";
        s = s.TrimEnd('%').Trim();
        if (s.Length == 0) return TryFind("TextSecondaryBrush");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 50
            ? TryFind("DangerBrush")
            : TryFind("SuccessBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static object TryFind(string key)
    {
        return System.Windows.Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }
}

/// <summary>
/// 布尔值反转后转可见性：true→Collapsed, false→Visible
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        return System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 整数大于0时返回 Visible，否则 Collapsed
/// </summary>
public class IntGreaterZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return System.Windows.Visibility.Collapsed;
        if (value is int i) return i > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (value is long l) return l > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (int.TryParse(value.ToString(), out var v)) return v > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 整数等于0时返回 Visible，否则 Collapsed
/// </summary>
public class IntIsZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 统一口径：仅真实等于 0 才可见；null 与不可解析一律隐藏（与严格 === 语义一致）
        if (value == null) return System.Windows.Visibility.Collapsed;
        if (value is int i) return i == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (value is long l) return l == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (int.TryParse(value.ToString(), out var v)) return v == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// double? 非空时返回 Visible，否则 Collapsed
/// </summary>
public class DoubleNotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return System.Windows.Visibility.Collapsed;
        if (value is double d) return d != 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 base64 data URL（或原始 base64）字符串转换为 WPF ImageSource。
/// 用于把 ImageService.ReadImage 返回的截图 data URL 绑定到 Image.Source。
/// 绑定示例：Source="{Binding DisplayScreenshot, Converter={StaticResource Base64Image}}"
/// </summary>
public class Base64ImageConverter : IValueConverter
{
    // 内容键 LRU 缓存：跨视图实例稳定命中，避免每次重建卡片都重新解码。
    // 优化前 1920px × 30 张 ≈ 250-400MB，是 WPF 版内存翻倍的主因之一。
    // 降采样到 1280px（覆盖 1080p 截图 + 150% DPI 缩放）+ 容量缩至 12 → 峰值 ~60-100MB。
    private const int CacheCapacity = 12;
    private static readonly object _lock = new();
    private static readonly LinkedList<string> _lru = new();
    private static readonly Dictionary<string, BitmapImage> _cache = new();

    private static BitmapImage? GetCached(string key)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var bmp)) return null;
            _lru.Remove(key);
            _lru.AddFirst(key);
            return bmp;
        }
    }

    private static void PutCached(string key, BitmapImage bmp)
    {
        lock (_lock)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = bmp;
            _lru.AddFirst(key);
            while (_lru.Count > CacheCapacity)
            {
                var last = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(last);
            }
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return null;
        try
        {
            // parameter=full 解码原图（预览弹窗）；parameter=thumb 小缩略图按 480 解码；
            // 默认按 1280 采样（大卡片 1200 宽 + 150% DPI 缩放可覆盖，大幅降低内存占用）
            var full = parameter is string p && p.Equals("full", StringComparison.OrdinalIgnoreCase);
            var thumb = parameter is string t && t.Equals("thumb", StringComparison.OrdinalIgnoreCase);
            var targetWidth = thumb ? 480 : 1280;
            var cacheKey = full ? "full:" + s : thumb ? "t:" + s : s;
            var cached = GetCached(cacheKey);
            if (cached != null) return cached;

            // 相对文件路径（如 2026-05-28/insights_xxx.png）：数据库截图列为文件路径而非 base64，
            // 经 ImageService 解析到 data/images/<日期>/ 目录后从磁盘加载（对应原版 app-image:// 协议）
            if (!s.StartsWith("data:") && (s.Contains('/') || s.Contains('\\')))
            {
                var bmpFile = LoadFromFile(s, full, targetWidth);
                if (bmpFile != null) { PutCached(cacheKey, bmpFile); return bmpFile; }
                return null;
            }

            var base64 = s;
            if (s.StartsWith("data:"))
            {
                var comma = s.IndexOf(',');
                if (comma < 0) return null;
                base64 = s.Substring(comma + 1);
            }
            var buffer = System.Convert.FromBase64String(base64);
            // 探测原图宽度：按目标宽度采样；原图不足则保持原尺寸，绝不放大解码
            int srcWidth;
            using (var probe = new MemoryStream(buffer))
            {
                srcWidth = BitmapFrame.Create(probe).PixelWidth;
            }
            var decodeWidth = Math.Min(targetWidth, srcWidth);
            // MemoryStream 在 BitmapCacheOption.OnLoad 下 EndInit 后即可释放，
            // 但不 dispose 仍会在 Gen2 堆积，对 250+ 张截图场景贡献内存峰值
            using var ms = new MemoryStream(buffer);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            // 原图比目标宽才降采样；否则全尺寸解码（避免 DecodePixelWidth 把小图拉大）
            if (!full && decodeWidth < srcWidth) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            PutCached(cacheKey, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();

    /// <summary>
    /// 相对路径 → 磁盘文件加载（对应原版 resolveImagePath + app-image:// 协议）。
    /// 经 DI 中的 ImageService 解析（含旧目录回退），失败返回 null。
    /// </summary>
    private static BitmapImage? LoadFromFile(string relativePath, bool full, int targetWidth)
    {
        try
        {
            var host = App.Host;
            var imageService = host?.Services.GetService(typeof(StockReview.Core.Data.ImageService))
                as StockReview.Core.Data.ImageService;
            var filePath = imageService?.ResolveImagePath(relativePath.Replace('\\', '/'));
            if (filePath is null || !File.Exists(filePath)) return null;

            var buffer = File.ReadAllBytes(filePath);
            // 探测原图宽度：仅对大图降采样（避免把小图拉大）
            int srcWidth;
            using (var probe = new MemoryStream(buffer))
            {
                srcWidth = BitmapFrame.Create(probe).PixelWidth;
            }
            using var ms = new MemoryStream(buffer);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            if (!full && srcWidth > targetWidth) bmp.DecodePixelWidth = targetWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 多值转换器：当某日期等于当前选中的日期（SelectedDate）时返回高亮画刷，否则透明。
/// 用于日期选择条中选中日期的高亮。
/// values[0] = 条目日期字符串, values[1] = 当前选中日期字符串。
/// </summary>
public class DateHighlightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is string itemDate && values[1] is string selected
            && !string.IsNullOrEmpty(selected) && itemDate == selected)
        {
            return System.Windows.Application.Current.TryFindResource("PrimaryBrush")
                   ?? System.Windows.Media.Brushes.DodgerBlue;
        }
        return System.Windows.Media.Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 日期字符串 (yyyy-MM-dd) <-> DateTime 互转，用于 DatePicker 绑定。
/// </summary>
/// <summary>
/// 多值相等转换器（IMultiValueConverter 版本），用于 MultiBinding 中比较多个值。
/// 典型用法：比较列表项与当前选中值，驱动高亮。
/// values 中任意两个相等即返回 true（通常用于「项 == 选中值」两参数场景）。
/// </summary>
public class MultiEqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var first = values[0]?.ToString();
        for (var i = 1; i < values.Length; i++)
        {
            if (string.Equals(first, values[i]?.ToString(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class DateTimeStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && DateTime.TryParse(s, out var dt))
            return dt;
        // 解析失败不注入当前时间（脏数据进表单），保持目标控件自身默认值
        return System.Windows.Data.Binding.DoNothing;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd");
        return "";
    }
}

/// <summary>
/// 多值转换器：根据擒牛卡片状态返回对应边框画刷（对齐原版 getCardColorClass + .pick-card 各状态边框色）。
/// values[0] = IsSelected(bool), values[1] = Rank(int), values[2] = HasNextDay(bool)。
/// 选中且排名第1且有次日数据 -> 捕获红 #F56C6C（captured-card）；
/// 选中 -> 蓝 #409EFF（selected-card）；排名第1且有次日数据但未选中 -> 橙 #E6A23C（top-card）；
/// 其余 -> 透明（原版 border: 2px solid transparent）。
/// </summary>
public class PickCardBrushConverter : IMultiValueConverter
{
    private static System.Windows.Media.Brush Hex(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSelected = values.Length > 0 && values[0] is bool b0 && b0;
        var rank = values.Length > 1 && values[1] is int r ? r : 0;
        var hasNext = values.Length > 2 && values[2] is bool b2 && b2;

        if (isSelected && rank == 1 && hasNext) return Hex("#F56C6C");
        if (isSelected) return Hex("#409EFF");
        if (rank == 1 && hasNext) return Hex("#E6A23C");
        return System.Windows.Media.Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 多值转换器：根据排名返回奖牌/序号文本（对齐原版 rank-badge）。
/// values[0] = Rank(int), values[1] = HasNextDay(bool), values[2] = IsSelected(bool, 可选)。
/// 排名1：选中 -> 🏆，未选中 -> 🥇；2 -> 🥈；3 -> 🥉；其余 -> #rank。
/// </summary>
public class RankMedalConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var rank = values.Length > 0 && values[0] is int r ? r : 0;
        var hasNext = values.Length > 1 && values[1] is bool b && b;
        var isSelected = values.Length > 2 && values[2] is bool s && s;
        if (!hasNext) return "";
        return rank switch
        {
            1 => isSelected ? "🏆" : "🥇",
            2 => "🥈",
            3 => "🥉",
            _ => $"#{rank}"
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 当整型值为 0 时返回 Collapsed，否则 Visible。用于空状态提示。
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        return n == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 将 yyyy-MM-dd / yyyy-MM 日期字符串截取 MM-dd 部分显示（列表视图用）。
/// </summary>
public class DateMonthDayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return "";
        return s.Length >= 10 ? s.Substring(5, 5) : s;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool -> 标题文本：true=编辑，false=新增。parameter 指定业务前缀（如 "Trade" -> 编辑记录/新增记录）
/// </summary>
public class EditTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEdit = value is true;
        var prefix = parameter as string;
        if (prefix == "Trade")
            return isEdit ? "编辑记录" : "新增记录";
        return isEdit ? "编辑心得" : "写心得";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// string -> Visibility：值不等于 parameter 时可见（用于"非首次建仓"显示首次日期等）
/// </summary>
public class StringNotEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        var p = parameter as string ?? "";
        return s != p ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Bool -> Brush (true=SuccessBrush, false=DangerBrush)
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? (Brush)Application.Current.FindResource("SuccessBrush")
                      : (Brush)Application.Current.FindResource("DangerBrush");
        return Brushes.Transparent;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
