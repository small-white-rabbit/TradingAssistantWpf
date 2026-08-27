using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;

namespace StockReviewWpf.Services;

/// <summary>
/// 双通道 OCR 服务：从交易截图（行情软件截图）中识别股票代码。
/// 对应 Electron 原版的 useOCR.js + ocrEnhancer.js 管线：
///   - 通道一（云端）：配置了百度密钥时优先百度 OCR general_basic
///   - 通道二（本地）：多区域裁剪 + 图像预处理 + Tesseract 数字白名单识别
///   - 领域后处理：6 位代码提取 + 股票列表模糊纠错（Levenshtein ≤ 2）
/// </summary>
public sealed class StockOcrService
{
    // 裁剪区域：相对整图比例 (x, y, w, h)。优先右上角代码窄条，再依次尝试其它常见位置。
    // 对应 Electron CROP_REGIONS：右上角小条、右上四分之一、顶部整条。
    private static readonly (double X, double Y, double W, double H, string Label)[] CropRegions =
    {
        (0.75, 0.00, 0.25, 0.04, "右上角代码条"),
        (0.55, 0.00, 0.45, 0.12, "右上区域"),
        (0.00, 0.00, 1.00, 0.06, "顶部整条")
    };

    private readonly Dictionary<string, string> _stockNameMap;
    private readonly StockReview.Core.Data.DatabaseService _db;
    private readonly OcrService _baiduOcr;

    public StockOcrService(StockReview.Core.Data.DatabaseService db, OcrService baiduOcr)
    {
        _db = db;
        _baiduOcr = baiduOcr;
        _stockNameMap = LoadStockNameMap();
    }

    private static Dictionary<string, string> LoadStockNameMap()
    {
        var map = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(App.AppBaseDir, "Resources", "stockList.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (raw != null)
                {
                    foreach (var kv in raw)
                    {
                        var code = kv.Key.Trim();
                        if (code.Length is >= 1 and <= 6)
                            map[code.PadLeft(6, '0')] = kv.Value.Trim();
                    }
                }
            }
        }
        catch
        {
            // 映射缺失时降级：仅做 6 位代码提取，不做模糊纠错
        }
        return map;
    }

    /// <summary>按代码查询名称（对应 Electron getStockName）</summary>
    public string GetNameByCode(string code)
    {
        var c = (code ?? "").Trim().PadLeft(6, '0');
        return c.Length == 6 && _stockNameMap.TryGetValue(c, out var name) ? name : "";
    }

    /// <summary>按代码部分/名称子串搜索（对应 Electron searchStocks，最多 50 条）</summary>
    public List<(string Code, string Name)> SearchStocks(string keyword)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(keyword)) return result;
        var kw = keyword.Trim();
        var kwLower = kw.ToLowerInvariant();
        foreach (var kv in _stockNameMap)
        {
            if (kv.Key.Contains(kw) || kv.Value.ToLowerInvariant().Contains(kwLower))
            {
                result.Add((kv.Key, kv.Value));
                if (result.Count >= 50) break;
            }
        }
        return result;
    }

    /// <summary>
    /// 双通道识别入口（对应原版 useOCR 三层管线的引擎层）：
    /// 配置了百度密钥（appConfig['ocrConfig']）时优先云端识别，失败/未配置降级本地 Tesseract。
    /// </summary>
    public async Task<OcrResult> RecognizeStockCodeAsync(string base64Image)
    {
        var (apiKey, secretKey) = LoadBaiduKeys();
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(secretKey))
        {
            try
            {
                var cropped64 = await Task.Run(() => CropTopRightToBase64(base64Image));
                if (!string.IsNullOrEmpty(cropped64))
                {
                    var (ok, text, _) = await _baiduOcr.RecognizeAsync(cropped64, apiKey, secretKey);
                    var code = ExtractCode(text);
                    if (ok && !string.IsNullOrEmpty(code))
                    {
                        if (_stockNameMap.TryGetValue(code, out var name))
                            return OcrResult.MakeSuccess(code, name, "baidu");
                        var cand = FuzzyMatch(code, 2);
                        if (cand.HasValue)
                            return OcrResult.MakeSuccess(cand.Value.Code, cand.Value.Name, "baidu+fuzzy");
                        return OcrResult.MakeSuccess(code, "", "baidu");
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[OCR] 百度识别失败，降级本地引擎");
            }
        }
        return await Task.Run(() => RecognizeStockCode(base64Image));
    }

    /// <summary>
    /// 从 base64 截图识别股票代码（本地 Tesseract 通道）。返回识别到的代码（6 位，未匹配到名称也可能返回原始识别值）。
    /// </summary>
    public OcrResult RecognizeStockCode(string base64Image)
    {
        var (ok, bytes) = DecodeBase64(base64Image);
        if (!ok || bytes.Length == 0)
            return OcrResult.MakeFailed("无效的图片数据");

        BitmapSource? full = null;
        try
        {
            full = LoadBitmap(bytes);
        }
        catch (Exception ex)
        {
            return OcrResult.MakeFailed("图片解码失败: " + ex.Message);
        }
        if (full == null) return OcrResult.MakeFailed("图片解码失败");

        using var engine = CreateEngine();
        if (engine == null) return OcrResult.MakeFailed("OCR 引擎初始化失败（缺少 tessdata）");

        string? bestRawCode = null;
        string? bestSource = null;

        foreach (var region in CropRegions)
        {
            try
            {
                var cropped = Crop(full, region.X, region.Y, region.W, region.H);
                if (cropped == null) continue;
                var preprocessed = Preprocess(cropped);

                var text = RunTesseract(engine, preprocessed);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var m = System.Text.RegularExpressions.Regex.Match(text, @"\d{1,6}");
                if (!m.Success) continue;

                var rawCode = m.Value.PadLeft(6, '0');
                bestRawCode ??= rawCode;
                bestSource = region.Label;

                // 命中已知股票列表则直接返回（与 Electron tesseract+fuzzy 同策略）
                if (_stockNameMap.TryGetValue(rawCode, out var name))
                    return OcrResult.MakeSuccess(rawCode, name, region.Label);
            }
            catch
            {
                // 该区域失败，尝试下一个
            }
        }

        if (bestRawCode != null)
        {
            // 领域后处理：模糊纠错到已知代码
            var cand = FuzzyMatch(bestRawCode, 2);
            if (cand.HasValue)
                return OcrResult.MakeSuccess(cand.Value.Code, cand.Value.Name, "tesseract+fuzzy[" + bestSource + "]");
            return OcrResult.MakeSuccess(bestRawCode, "", bestSource ?? "tesseract");
        }

        return OcrResult.MakeFailed("未在截图中识别到股票代码");
    }

    /// <summary>读取百度 OCR 密钥（appConfig['ocrConfig']，由设置页保存）</summary>
    private (string ApiKey, string SecretKey) LoadBaiduKeys()
    {
        try
        {
            var row = _db.GetById("appConfig", "ocrConfig");
            if (row != null && row.TryGetValue("value", out var v) && v != null)
            {
                using var doc = JsonDocument.Parse(v.ToString()!);
                if (doc.RootElement.TryGetProperty("apiKey", out var ak)
                    && doc.RootElement.TryGetProperty("secretKey", out var sk))
                    return (ak.GetString() ?? "", sk.GetString() ?? "");
            }
        }
        catch
        {
            // 配置缺失/损坏时仅本地引擎
        }
        return ("", "");
    }

    /// <summary>裁剪右上角代码窄条并编码为 PNG base64（对应 Electron cropImageToRightTop）</summary>
    private static string? CropTopRightToBase64(string base64Image)
    {
        var (ok, bytes) = DecodeBase64(base64Image);
        if (!ok) return null;
        var full = LoadBitmap(bytes);
        if (full == null) return null;
        var cropped = Crop(full, 0.75, 0.00, 0.25, 0.04);
        if (cropped == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>从 OCR 文本提取 6 位股票代码（不足 6 位左补零，与原版 extractStockCode 一致）</summary>
    private static string? ExtractCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\d{1,6}");
        return m.Success ? m.Value.PadLeft(6, '0') : null;
    }

    private static TesseractEngine? CreateEngine()
    {
        try
        {
            // 仅识别数字，单行模式（对应 Electron setParameters 白名单 + PSM 7）
            var engine = new TesseractEngine(Path.Combine(App.AppBaseDir, "tessdata"), "eng", EngineMode.Default);
            engine.DefaultPageSegMode = PageSegMode.SingleLine;
            engine.SetVariable("tessedit_char_whitelist", "0123456789");
            return engine;
        }
        catch
        {
            return null;
        }
    }

    private static string RunTesseract(TesseractEngine engine, BitmapSource img)
    {
        using var pix = BitmapToPix(img);
        if (pix == null) return "";
        using var page = engine.Process(pix);
        return page.GetText();
    }

    // ===== 图像处理 =====

    private static BitmapSource? LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        // 强制 96dpi 以便后续像素裁剪计算一致
        return new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    }

    private static BitmapSource? Crop(BitmapSource src, double xFrac, double yFrac, double wFrac, double hFrac)
    {
        if (src == null) return null;
        var x = (int)Math.Max(0, xFrac * src.PixelWidth);
        var y = (int)Math.Max(0, yFrac * src.PixelHeight);
        var w = (int)Math.Min(src.PixelWidth - x, wFrac * src.PixelWidth);
        var h = (int)Math.Min(src.PixelHeight - y, hFrac * src.PixelHeight);
        if (w <= 0 || h <= 0) return null;
        var rect = new Int32Rect(x, y, w, h);
        var cropped = new CroppedBitmap(src, rect);
        return cropped;
    }

    /// <summary>
    /// 预处理：放大 3x（提升小字识别率）+ 灰度 + 二值化（Otsu 风格阈值）。
    /// 模拟 Electron preprocessImage({scale:3, binarize:true, sharpen:true})。
    /// </summary>
    private static BitmapSource Preprocess(BitmapSource src)
    {
        // 1. 放大（提升小字识别率）
        var scale = 3.0;
        var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));

        // 2. 灰度（Tesseract 对灰度数字识别稳定；实测优于激进二值化）
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        return gray;
    }

    private static BitmapSource BinarizeOtsu(BitmapSource gray)
    {
        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        var stride = width; // Gray8: 1 byte/pixel
        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, stride, 0);

        // Otsu 阈值
        var hist = new int[256];
        foreach (var p in pixels) hist[p]++;
        var total = pixels.Length;
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, wB = 0, maxVar = 0;
        var threshold = 127;
        for (var i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += i * hist[i];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar)
            {
                maxVar = between;
                threshold = i;
            }
        }

        // 行情软件代码区多为深色字浅色底 → 数字像素 < threshold 视为前景（黑）保留为 0，背景置 255
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = pixels[i] < threshold ? (byte)0 : (byte)255;

        var outImg = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
        outImg.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        return outImg;
    }

    private static Pix? BitmapToPix(BitmapSource src)
    {
        try
        {
            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            var width = gray.PixelWidth;
            var height = gray.PixelHeight;
            var stride = width;
            var pixels = new byte[width * height];
            gray.CopyPixels(pixels, stride, 0);
            // 写到临时 PNG 后用 Tesseract 加载（Pix.Create 的像素写入 API 不便，走临时文件最稳）
            var tmp = Path.Combine(Path.GetTempPath(), "sr_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(gray));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);
                var pix = Pix.LoadFromFile(tmp);
                return pix;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    // ===== 领域后处理：模糊纠错 =====

    private (string Code, string Name)? FuzzyMatch(string rawCode, int maxDistance)
    {
        if (_stockNameMap.Count == 0) return null;
        var best = (code: "", name: "", dist: int.MaxValue);
        foreach (var kv in _stockNameMap)
        {
            var d = Levenshtein(rawCode, kv.Key);
            if (d < best.dist)
            {
                best = (kv.Key, kv.Value, d);
                if (d == 0) break;
            }
        }
        if (best.dist <= maxDistance)
            return (best.code, best.name);
        return null;
    }

    private static int Levenshtein(string a, string b)
    {
        a = a ?? "";
        b = b ?? "";
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;
        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }

    private static (bool Ok, byte[] Bytes) DecodeBase64(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return (false, Array.Empty<byte>());
        try
        {
            var comma = base64.IndexOf(',', StringComparison.Ordinal);
            var data = comma >= 0 ? base64.Substring(comma + 1) : base64;
            return (true, Convert.FromBase64String(data));
        }
        catch
        {
            return (false, Array.Empty<byte>());
        }
    }
}

/// <summary>
/// OCR 识别结果。
/// </summary>
public sealed class OcrResult
{
    public bool Success { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Error { get; init; } = "";

    public static OcrResult MakeSuccess(string code, string name, string source) =>
        new() { Success = true, Code = code, Name = name, Source = source };

    public static OcrResult MakeFailed(string error) =>
        new() { Success = false, Error = error };
}
