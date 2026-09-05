using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;

namespace StockReviewWpf.Services;

/// <summary>
/// 双通道 OCR 服务：从交易截图（行情软件截图）中识别股票代码。
/// 对应原版的 useOCR.js + ocrEnhancer.js 管线：
///   - 通道一（云端）：配置了百度密钥时优先百度 OCR general_basic
///   - 通道二（本地）：多区域裁剪 + 图像预处理 + Tesseract 数字白名单识别
///   - 领域后处理：独立 6 位代码候选 + 名称/代码互相佐证 + 模糊纠错（Levenshtein ≤ 1）
/// </summary>
public sealed class StockOcrService
{
    // 裁剪区域：相对整图比例 (x, y, w, h)。与原版 CROP_REGIONS 完全一致，
    // 全部位于右上角（代码+名称约 95% 概率在右上角），不做左上/整图等低概率区域。
    private static readonly (double X, double Y, double W, double H, string Label)[] CropRegions =
    {
        (0.70, 0.00, 0.30, 0.08, "右上角8%"),
        (0.65, 0.00, 0.35, 0.10, "右上角10%"),
        (0.75, 0.00, 0.25, 0.05, "右上角5%")
    };

    // 百度通道专用裁剪区域（用户指定规格，与原版 一致只做右上角）：
    //   主区域「右上角1/6」= 右半幅上1/3（0.5宽 × 0.3333高，面积≈1/6），通常足够框住代码+名称；
    //   回退「右上角1/4」= 右上四分之一（0.5宽 × 0.5高，面积=1/4），主区域未命中时放大兜底。
    // 先精确后放大，命中即停。
    private static readonly (double X, double Y, double W, double H, string Label)[] BaiduCropRegions =
    {
        (0.50, 0.0000, 0.50, 0.3333, "右上角1/6"),
        (0.50, 0.0000, 0.50, 0.5000, "右上角1/4")
    };

    private readonly Dictionary<string, string> _stockNameMap;
    private readonly StockReview.Core.Data.IDatabaseService _db;
    private readonly OcrService _baiduOcr;

    public StockOcrService(StockReview.Core.Data.IDatabaseService db, OcrService baiduOcr)
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

    /// <summary>按代码查询名称（对应原版 getStockName）</summary>
    public string GetNameByCode(string code)
    {
        var c = (code ?? "").Trim().PadLeft(6, '0');
        return c.Length == 6 && _stockNameMap.TryGetValue(c, out var name) ? name : "";
    }

    /// <summary>按代码部分/名称子串搜索（对应原版 searchStocks，最多 50 条）</summary>
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
                // 百度通道裁剪规格（用户指定）：主区域「右上角1/6」，未命中回退「右上角1/4」（见 BaiduCropRegions）。
                // 原实现只裁 0.25×0.04 单一窄条，代码位置稍有偏差即漏识别、降级本地引擎；
                // 现按精确规格先小后大送百度识别，任一区域命中即返回。
                // 解码 + 多区域裁剪 + base64 编码全部在同一后台线程内闭环完成：
                // WPF 位图是 Dispatcher 亲和对象，「线程A解码、线程B裁剪」的用法即使逐级 Freeze
                // 仍会在冻结级联中触发跨线程异常（曾导致百度通道 100% 降级本地）。
                // 收进单个 Task.Run 后位图仅在创建线程内使用，跨线程传递的只是 string。
                var regionImages = await Task.Run(() =>
                {
                    var full = DecodeAndLoadBitmap(base64Image);
                    if (full == null) return new List<(string Label, string Base64)>();
                    var list = new List<(string Label, string Base64)>();
                    foreach (var region in BaiduCropRegions)
                    {
                        var b64 = CropRegionToBase64(full, region.X, region.Y, region.W, region.H);
                        if (!string.IsNullOrEmpty(b64)) list.Add((region.Label, b64));
                    }
                    return list;
                });

                foreach (var (label, cropped64) in regionImages)
                {
                    var (ok, text, err) = await _baiduOcr.RecognizeAsync(cropped64, apiKey, secretKey);
                    Serilog.Log.Information("[OCR] 百度识别 region={Region} ok={Ok} text=\"{Text}\"",
                        label, ok, text ?? "");
                    if (!ok)
                    {
                        Serilog.Log.Information("[OCR] 百度调用失败 region={Region} err={Error}", label, err);
                        continue;
                    }

                    var resolved = ResolveFromText(text, "baidu[" + label + "]");
                    if (resolved != null)
                        return resolved;
                    Serilog.Log.Information("[OCR] 百度区域 {Region} 未解析出股票代码", label);
                }
                Serilog.Log.Information("[OCR] 百度多区域均未识别到代码，降级本地引擎");
            }
            catch (Exception ex)
            {
                Serilog.Log.Information(ex, "[OCR] 百度识别异常，降级本地引擎");
            }
        }
        else
        {
            Serilog.Log.Information("[OCR] 未配置百度密钥，直接走本地 Tesseract 引擎");
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

        var engine = GetEngine();
        if (engine == null) return OcrResult.MakeFailed("OCR 引擎初始化失败（缺少 tessdata）");

        lock (EngineLock)
        {
            foreach (var region in CropRegions)
            {
                try
                {
                    var cropped = Crop(full, region.X, region.Y, region.W, region.H);
                    if (cropped == null) continue;
                    var preprocessed = Preprocess(cropped);

                    var text = RunTesseract(engine, preprocessed);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    Serilog.Log.Information("[OCR] 本地区域 {Region} 识别文本：{Text}", region.Label, text);
                    var resolved = ResolveFromText(text, region.Label);
                    if (resolved != null)
                        return resolved;
                }
                catch (Exception ex)
                {
                    // 该区域失败，尝试下一个；记录日志避免静默吞异常导致无从诊断。
                    // 异常可能让引擎进入坏状态：丢弃缓存，下次识别重建
                    Serilog.Log.Information(ex, "[OCR] 本地区域 {Region} 处理异常，尝试下一区域", region.Label);
                    DiscardEngine();
                    break;
                }
            }
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

    /// <summary>解码 base64 并加载为位图（百度通道多区域裁剪共用，避免重复解码）</summary>
    private static BitmapSource? DecodeAndLoadBitmap(string base64Image)
    {
        var (ok, bytes) = DecodeBase64(base64Image);
        if (!ok || bytes.Length == 0) return null;
        try { return LoadBitmap(bytes); }
        catch { return null; }
    }

    /// <summary>裁剪指定区域并编码为 PNG base64（百度通道多区域裁剪复用，替代原单一窄条 CropTopRightToBase64）</summary>
    private static string? CropRegionToBase64(BitmapSource full, double xFrac, double yFrac, double wFrac, double hFrac)
    {
        var cropped = Crop(full, xFrac, yFrac, wFrac, hFrac);
        if (cropped == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// 从 OCR 文本解析股票代码/名称（云端与本地通道共用的领域后处理）。
    /// 修复原实现的三个准确率问题：
    ///   1. 旧逻辑取「第一段 1~6 位数字」再左补零——裁剪区里的价格(12.34→000012)、
    ///      时间(09:41→000009)、指数点位(3245.67→003245) 等噪声数字被当成代码，几乎必然识别错；
    ///   2. 旧逻辑对任意提取值做 Levenshtein≤2 纠错，几千个 6 位代码中几乎任何错误数字
    ///      都能在距离 2 内命中某只真实股票，把垃圾数字「自信地纠错」成错误结果；
    ///   3. 云端文本里识别出的中文名称是最强信号，旧逻辑却只看数字、完全不用。
    /// 新策略（按置信度从高到低）：
    ///   a. 剔除时间、小数等噪声数字后，只收集「独立的 6 位数字」作为代码候选；
    ///   b. 名称与代码互相佐证：候选代码精确命中且其名称也出现在文本中 → 定案；
    ///   c. 唯一名称命中（代码可能识别错）→ 按名称定案；
    ///   d. 候选代码精确命中股票列表 → 定案（多个时优先有名称佐证者）；
    ///   e. 仅对独立 6 位候选做 Levenshtein≤1 纠错（自原 ≤2 收紧）；
    ///   f. 都不中 → 返回首个候选（名称为空，交由上层提示人工确认），不再伪造补零代码。
    /// </summary>
    private OcrResult? ResolveFromText(string? text, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 剔除时间（09:41 / 09:41:23）与小数（价格、百分比、指数点位），避免噪声数字混入候选
        var cleaned = Regex.Replace(text, @"\d{1,2}:\d{2}(:\d{2})?", " ");
        cleaned = Regex.Replace(cleaned, @"\d+\.\d+", " ");

        // 去空格版本：中文名称匹配（OCR 偶尔在汉字间插空格，如「贵 州 茅 台」）
        var compact = Regex.Replace(cleaned, @"\s+", "");

        // 已知股票名称命中（云端文本含中文，最强信号；按名称长度降序，长名优先避免被短名抢先）
        var nameHits = _stockNameMap
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && compact.Contains(kv.Value))
            .OrderByDescending(kv => kv.Value.Length)
            .ToList();

        // 独立 6 位数字候选（前后都不是数字，杜绝从长数字串中截段，也不再左补零伪造）。
        // OCR（百度按词返回/Tesseract）常把 6 位代码拆成多段（如「600 519」），
        // 因此除原文本外，再从「跨空格拼接数字」的版本中补充候选（拼接出的长数字串不会命中 6 位规则，安全）。
        var codeCandidates = new List<string>();
        var joined = Regex.Replace(cleaned, @"(?<=\d)\s+(?=\d)", "");
        foreach (var src in new[] { cleaned, joined })
        {
            foreach (Match m in Regex.Matches(src, @"(?<!\d)\d{6}(?!\d)"))
                if (!codeCandidates.Contains(m.Value)) codeCandidates.Add(m.Value);
        }

        // b. 名称+代码互相佐证（最高置信度）
        var corroborated = codeCandidates.FirstOrDefault(c => _stockNameMap.ContainsKey(c) && nameHits.Any(n => n.Key == c));
        if (corroborated != null)
            return OcrResult.MakeSuccess(corroborated, _stockNameMap[corroborated], sourceLabel);

        // c. 唯一名称命中（代码缺失或识别错时按名称定案）
        if (nameHits.Count == 1)
            return OcrResult.MakeSuccess(nameHits[0].Key, nameHits[0].Value, sourceLabel + "+name");

        // d. 候选代码精确命中
        var exact = codeCandidates.FirstOrDefault(c => _stockNameMap.ContainsKey(c));
        if (exact != null)
            return OcrResult.MakeSuccess(exact, _stockNameMap[exact], sourceLabel);

        // 多个名称命中且无代码佐证（大区域截到自选列表等场景），取名称最长者
        if (nameHits.Count > 1)
            return OcrResult.MakeSuccess(nameHits[0].Key, nameHits[0].Value, sourceLabel + "+name");

        // e. 独立 6 位候选纠错（Levenshtein≤1）
        foreach (var cand in codeCandidates)
        {
            var fuzzy = FuzzyMatch(cand, 1);
            if (fuzzy.HasValue)
                return OcrResult.MakeSuccess(fuzzy.Value.Code, fuzzy.Value.Name, sourceLabel + "+fuzzy");
        }

        // f. 返回首个候选（可能是未收录标的），名称为空
        if (codeCandidates.Count > 0)
            return OcrResult.MakeSuccess(codeCandidates[0], "", sourceLabel);

        return null;
    }

    // === 引擎复用 ===
    // TesseractEngine 初始化需加载 traineddata（百毫秒级），逐次识别重建拖慢每次截图回填。
    // 进程内缓存单实例；TesseractEngine 非线程安全（识别在 Task.Run 中可能并发调用），
    // 识别全程持锁串行化；Monitor 可重入，catch 内 DiscardEngine 不会自锁
    private static TesseractEngine? _cachedEngine;
    private static readonly object EngineLock = new();

    // 内存治理（2026-09-06）：eng 语言模型常驻 native 内存约几十 MB，而 OCR 仅在
    // 粘贴截图时低频使用。改为闲置 10 分钟自动 Dispose（GetEngine 内重置计时），
    // 下次识别重建（初始化百毫秒级）。到期回调在 EngineLock 内释放，与识别互斥安全。
    private const int EngineIdleTimeoutMs = 10 * 60 * 1000;
    private static System.Threading.Timer? _engineIdleTimer;

    private static TesseractEngine? GetEngine()
    {
        lock (EngineLock)
        {
            _engineIdleTimer ??= CreateIdleTimer();
            _engineIdleTimer.Change(EngineIdleTimeoutMs, System.Threading.Timeout.Infinite);
            return _cachedEngine ??= CreateEngine();
        }
    }

    private static System.Threading.Timer CreateIdleTimer() =>
        new System.Threading.Timer(_ => DiscardEngine(), null, EngineIdleTimeoutMs, System.Threading.Timeout.Infinite);

    private static void DiscardEngine()
    {
        lock (EngineLock)
        {
            try { _cachedEngine?.Dispose(); } catch { /* 释放失败仅影响下次重建 */ }
            _cachedEngine = null;
        }
    }

    private static TesseractEngine? CreateEngine()
    {
        try
        {
            // 仅识别数字，单行模式（对应原版 setParameters 白名单 + PSM 7）
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
        // 强制 96dpi 以便后续像素裁剪计算一致。
        // Freeze：后台线程(Task.Run)解码、UI 线程裁剪的跨线程场景必须冻结，
        // 否则 Crop 访问 PixelWidth 抛 InvalidOperationException（百度通道曾因此 100% 降级本地）
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
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
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 预处理（移植原版 preprocessImage v5 自适应阈值法）：
    ///   1. 亮度取 max(r,g,b)——针对深色背景的白色/绿色/红色数字，灰度平均会压暗彩色文字，max 通道能保留
    ///   2. 取亮度中位数估计背景（行情截图背景像素占绝大多数）
    ///   3. 动态阈值 = clamp(背景+50, 背景+80, 160)，亮于阈值判为文字
    ///   4. 二值化为黑字白底后 3x 最近邻放大（提升小字识别率）
    /// </summary>
    private static BitmapSource Preprocess(BitmapSource src)
    {
        // 取像素（Bgra32）
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);

        // 亮度 = max(r,g,b)
        var brightness = new byte[w * h];
        for (var i = 0; i < brightness.Length; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];
            brightness[i] = (byte)Math.Max(r, Math.Max(g, b));
        }

        // 中位数背景亮度
        var sorted = (byte[])brightness.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        // 动态阈值：背景 + 至少 50 的偏移，但不超过 160
        var threshold = Math.Max(median + 50, Math.Min(median + 80, 160));
        Serilog.Log.Debug("[OCR] 预处理: 背景亮度={Median}, 阈值={Threshold}", median, threshold);

        // 二值化：亮=文字=0(黑)，暗=背景=255(白)
        var binary = new byte[w * h];
        for (var i = 0; i < binary.Length; i++)
            binary[i] = brightness[i] > threshold ? (byte)0 : (byte)255;

        // 3x 最近邻放大
        const int scale = 3;
        var nw = w * scale;
        var nh = h * scale;
        var scaled = new byte[nw * nh];
        for (var y = 0; y < nh; y++)
        {
            var sy = Math.Min(y / scale, h - 1);
            for (var x = 0; x < nw; x++)
            {
                var sx = Math.Min(x / scale, w - 1);
                scaled[y * nw + x] = binary[sy * w + sx];
            }
        }

        var result = new WriteableBitmap(nw, nh, 96, 96, PixelFormats.Gray8, null);
        result.WritePixels(new Int32Rect(0, 0, nw, nh), scaled, nw, 0);
        result.Freeze();
        return result;
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
