using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace StockReview.Core.Data;

/// <summary>
/// 图片服务 - 图表截图的存储、压缩与读取
/// 管理 data/images/ 目录下的按日期组织的图片文件
/// 功能：保存(压缩JPEG)/读取/删除/批量/统计/清理/路径解析(新+旧格式)/孤儿清理
/// </summary>
public class ImageService
{
    private readonly IDatabaseService _db;
    private string _dataDir = "";

    // 图片压缩配置（JPEG 质量 0.85，不缩放）
    private const double JpegQuality = 0.85;
    private const int MaxSize = 0; // 0 = 不缩放

    // 截图相关表
    private static readonly string[] ScreenshotTables = { "trades", "strongStocks", "dailyPicks", "patternCases", "insights" };

    public ImageService(IDatabaseService db) => _db = db;

    public void SetDataDir(string dataDir) => _dataDir = dataDir;

    private string GetDataDir() =>
        !string.IsNullOrEmpty(_dataDir) ? _dataDir : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    private string ImagesDir => Path.Combine(GetDataDir(), "images");
    private string LegacyScreenshotsDir => Path.Combine(GetDataDir(), "screenshots");

    // ============ 保存图片 ============

    /// <summary>
    /// 保存 base64 图片（对应 screenshot:save）
    /// </summary>
    public (bool success, string? filePath, string? error) SaveImage(string base64Data, string? category = null)
    {
        try
        {
            var matches = System.Text.RegularExpressions.Regex.Match(base64Data, @"^data:(.+);base64,(.+)$");
            if (!matches.Success) throw new ArgumentException("无效的 base64 图片数据");
            var originalBuffer = Convert.FromBase64String(matches.Groups[2].Value);

            var (compressedBuffer, ext) = CompressImage(originalBuffer);
            var (absolutePath, relativePath) = GenerateNewFilePath();
            var finalPath = ext == "jpg" ? absolutePath : absolutePath.Replace(".jpg", ".png");
            var finalRelative = ext == "jpg" ? relativePath : relativePath.Replace(".jpg", ".png");

            File.WriteAllBytes(finalPath, compressedBuffer);
            return (true, finalRelative, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[图片] 保存失败");
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// 批量保存（对应 screenshot:saveBatch）
    /// </summary>
    public (bool success, List<(object id, bool success, string? filePath)>? results, string? error) SaveBatch(
        IEnumerable<(object id, string base64Data)> items)
    {
        try
        {
            var results = new List<(object id, bool success, string? filePath)>();
            foreach (var (id, base64Data) in items)
            {
                var (ok, path, err) = SaveImage(base64Data);
                results.Add((id, ok, ok ? path : err));
            }
            return (true, results, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ============ 读取图片 ============

    /// <summary>
    /// 读取图片为 base64 data URL（对应 screenshot:read）
    /// </summary>
    public (bool success, string data, string? error) ReadImage(string? relativePath)
    {
        try
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith("data:"))
                return (true, relativePath ?? "", null);

            var filePath = ResolveImagePath(relativePath);
            if (filePath == null) return (false, "", "截图文件不存在");

            var buffer = File.ReadAllBytes(filePath);
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLower();
            var mimeType = ext switch
            {
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "webp" => "image/webp",
                _ => "image/png"
            };
            return (true, $"data:{mimeType};base64,{Convert.ToBase64String(buffer)}", null);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    /// <summary>
    /// 批量读取（对应 screenshot:readBatch）
    /// </summary>
    public (bool success, List<(object id, string base64)>? results, string? error) ReadBatch(
        IEnumerable<(object id, string path)> items)
    {
        try
        {
            var results = new List<(object id, string base64)>();
            foreach (var (id, p) in items)
            {
                if (string.IsNullOrEmpty(p) || p.StartsWith("data:"))
                {
                    results.Add((id, p ?? ""));
                    continue;
                }
                var filePath = ResolveImagePath(p);
                if (filePath != null && File.Exists(filePath))
                {
                    var buffer = File.ReadAllBytes(filePath);
                    var ext = Path.GetExtension(filePath).TrimStart('.').ToLower();
                    var mimeType = ext switch
                    {
                        "png" => "image/png",
                        "jpg" or "jpeg" => "image/jpeg",
                        "webp" => "image/webp",
                        _ => "image/png"
                    };
                    results.Add((id, $"data:{mimeType};base64,{Convert.ToBase64String(buffer)}"));
                }
                else
                {
                    results.Add((id, ""));
                }
            }
            return (true, results, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ============ 删除 ============

    public (bool success, string? error) DeleteImage(string? relativePath)
    {
        try
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith("data:"))
                return (true, null);
            var filePath = ResolveImagePath(relativePath);
            if (filePath != null && File.Exists(filePath)) File.Delete(filePath);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ============ 统计 ============

    public object GetStats()
    {
        var stats = new
        {
            totalSize = 0L,
            totalFiles = 0,
            dates = new Dictionary<string, object>(),
            categories = new Dictionary<string, dynamic>
            {
                ["trades"] = new { files = 0, size = 0L },
                ["strongStocks"] = new { files = 0, size = 0L },
                ["dailyPicks"] = new { files = 0, size = 0L }
            }
        };

        if (!Directory.Exists(ImagesDir)) return stats;

        var dateDict = new Dictionary<string, (long size, int files)>();
        var catFiles = new Dictionary<string, (int files, long size)>
        {
            ["trades"] = (0, 0),
            ["strongStocks"] = (0, 0),
            ["dailyPicks"] = (0, 0)
        };
        var totalSize = 0L;
        var totalFiles = 0;

        foreach (var dateDir in Directory.GetDirectories(ImagesDir))
        {
            var dateName = Path.GetFileName(dateDir);
            var files = Directory.GetFiles(dateDir)
                .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLower()))
                .ToList();
            var size = 0L;
            foreach (var f in files)
            {
                var fileSize = new FileInfo(f).Length;
                size += fileSize;
                var category = Path.GetFileName(f).Split('_')[0];
                if (catFiles.ContainsKey(category))
                {
                    var prev = catFiles[category];
                    catFiles[category] = (prev.files + 1, prev.size + fileSize);
                }
            }
            dateDict[dateName] = (size, files.Count);
            totalSize += size;
            totalFiles += files.Count;
        }

        return new
        {
            totalSize,
            totalFiles,
            dates = dateDict,
            categories = catFiles
        };
    }

    // ============ 清理 ============

    public int Cleanup(DateTime beforeDate, string? category = null)
    {
        if (!Directory.Exists(ImagesDir)) return 0;
        var beforeTs = beforeDate;
        var deleted = 0;

        foreach (var dateDir in Directory.GetDirectories(ImagesDir))
        {
            var dateName = Path.GetFileName(dateDir);
            if (DateTime.TryParse(dateName, out var dirDate) && dirDate >= beforeTs) continue;

            var files = Directory.GetFiles(dateDir);
            foreach (var f in files)
            {
                if (category != null && !Path.GetFileName(f).StartsWith(category + "_")) continue;
                File.Delete(f);
                deleted++;
            }
            if (!Directory.EnumerateFileSystemEntries(dateDir).Any()) Directory.Delete(dateDir);
        }
        return deleted;
    }

    public int CleanupOrphaned(IEnumerable<string> referencedPaths)
    {
        var referencedFilenames = new HashSet<string>();
        foreach (var p in referencedPaths)
        {
            if (string.IsNullOrEmpty(p) || p.StartsWith("data:")) continue;
            referencedFilenames.Add(Path.GetFileName(p));
            var parts = p.Replace("\\", "/").Split('/');
            if (parts.Length == 2) referencedFilenames.Add($"{parts[0]}_{parts[1]}");
        }

        var deleted = 0;
        if (!Directory.Exists(ImagesDir)) return deleted;

        foreach (var dateDir in Directory.GetDirectories(ImagesDir))
        {
            foreach (var file in Directory.GetFiles(dateDir))
            {
                if (!referencedFilenames.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            if (!Directory.EnumerateFileSystemEntries(dateDir).Any()) Directory.Delete(dateDir);
        }
        return deleted;
    }

    // ============ 路径解析（对应 resolveImagePath） ============

    /// <summary>
    /// 解析图片相对路径为绝对路径
    /// 支持新格式: 2026-05-28/trades_xxx.jpg
    /// 支持旧格式: trades/20260528_xxx.jpg -> images/2026-05-28/trades_20260528_xxx.jpg
    /// 防路径穿越
    /// </summary>
    public string? ResolveImagePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith("data:")) return null;

        var normalizedRel = relativePath.Replace("\\", "/");
        // 防路径穿越
        if (normalizedRel.Split('/').Any(seg => seg == "..")) return null;

        var imagesDir = Path.GetFullPath(ImagesDir);
        var legacyDir = Path.GetFullPath(LegacyScreenshotsDir);

        string? SafeJoin(string root, string rel)
        {
            var p = Path.GetFullPath(Path.Combine(root, rel));
            if (p != root && !p.StartsWith(root + Path.DirectorySeparatorChar)) return null;
            return File.Exists(p) ? p : null;
        }

        // 新格式: 2026-05-28/xxx.jpg
        if (System.Text.RegularExpressions.Regex.IsMatch(normalizedRel, @"^\d{4}-\d{2}-\d{2}\/"))
        {
            var p = SafeJoin(imagesDir, normalizedRel);
            if (p != null) return p;
        }

        // 旧格式: trades/20260528_xxx.jpg -> images/2026-05-28/trades_20260528_xxx.jpg
        var parts = normalizedRel.Split('/');
        if (parts.Length == 2)
        {
            var m = System.Text.RegularExpressions.Regex.Match(parts[1], @"^(\d{4})(\d{2})(\d{2})_");
            if (m.Success)
            {
                var dateStr = $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
                var p = SafeJoin(imagesDir, $"{dateStr}/{parts[0]}_{parts[1]}");
                if (p != null) return p;
            }
            // 回退旧目录
            var legacyP = SafeJoin(legacyDir, normalizedRel);
            if (legacyP != null) return legacyP;
        }

        return SafeJoin(imagesDir, normalizedRel);
    }

    // ============ 导出辅助 ============

    /// <summary>
    /// 从导出数据中收集所有截图路径
    /// </summary>
    public HashSet<string> CollectScreenshotPaths(Dictionary<string, object> data)
    {
        var paths = new HashSet<string>();

        void AddPaths(object? value)
        {
            if (value == null) return;
            if (value is string s)
            {
                if (s.StartsWith("data:")) return;
                if (s.StartsWith("["))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<string>>(s);
                        if (parsed != null) { foreach (var v in parsed) if (!string.IsNullOrEmpty(v) && !v.StartsWith("data:")) paths.Add(v); }
                        return;
                    }
                    catch { }
                }
                paths.Add(s);
                return;
            }
            if (value is IEnumerable<string> arr)
            {
                foreach (var v in arr) if (!string.IsNullOrEmpty(v) && !v.StartsWith("data:")) paths.Add(v);
            }
        }

        foreach (var table in ScreenshotTables)
        {
            if (data.TryGetValue(table, out var tableData) && tableData is JsonElement je)
            {
                foreach (var item in je.EnumerateArray())
                {
                    if (item.TryGetProperty("screenshot", out var ss))
                    {
                        AddPaths(ss.ValueKind == JsonValueKind.String ? ss.GetString() : null);
                    }
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// 统计需要的截图数量
    /// </summary>
    public int CountNeededScreenshots(Dictionary<string, object> data)
    {
        var count = 0;
        foreach (var table in ScreenshotTables)
        {
            if (data.TryGetValue(table, out var tableData) && tableData is JsonElement je)
            {
                foreach (var item in je.EnumerateArray())
                {
                    if (item.TryGetProperty("screenshot", out var ss) && ss.ValueKind == JsonValueKind.String)
                    {
                        var s = ss.GetString();
                        if (!string.IsNullOrEmpty(s) && !s.StartsWith("data:")) count++;
                    }
                }
            }
        }
        return count;
    }

    // ============ 图片压缩 ============

    /// <summary>
    /// 压缩图片为 JPEG（对应 compressImage）
    /// </summary>
    private (byte[] buffer, string ext) CompressImage(byte[] originalBuffer)
    {
        try
        {
            using var ms = new MemoryStream(originalBuffer);
            using var img = Image.FromStream(ms);
            if (img.Width == 0 || img.Height == 0) return (originalBuffer, "png");

            Image finalImage = img;
            var disposed = false;

            if (MaxSize > 0 && img.Width > MaxSize)
            {
                var scale = (double)MaxSize / img.Width;
                var newWidth = (int)(img.Width * scale);
                var newHeight = (int)(img.Height * scale);
                var resized = new Bitmap(newWidth, newHeight);
                using var g = Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, newWidth, newHeight);
                finalImage = resized;
                disposed = true;
            }

            try
            {
                using var jpegMs = new MemoryStream();
                var jpegEncoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegEncoder != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)(JpegQuality * 100));
                    finalImage.Save(jpegMs, jpegEncoder, encoderParams);
                }
                else
                {
                    finalImage.Save(jpegMs, ImageFormat.Jpeg);
                }

                var jpegBuffer = jpegMs.ToArray();
                // 如果 JPEG 不比原图小，保留 PNG
                return jpegBuffer.Length < originalBuffer.Length ? (jpegBuffer, "jpg") : (originalBuffer, "png");
            }
            finally
            {
                if (disposed) finalImage.Dispose();
            }
        }
        catch
        {
            return (originalBuffer, "png");
        }
    }

    // ============ 文件路径生成 ============

    private (string absolutePath, string relativePath) GenerateNewFilePath()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get);
        var dateStr = now.ToString("yyyy-MM-dd");
        var dateCompact = now.ToString("yyyyMMdd");
        var timeStr = now.ToString("HHmmss");
        var randomStr = new Random().Next(0, 0xFFFF).ToString("x4");
        var filename = $"trades_{dateCompact}_{timeStr}_{randomStr}.jpg";
        var dateDir = Path.Combine(ImagesDir, dateStr);
        Directory.CreateDirectory(dateDir);
        return (Path.Combine(dateDir, filename), $"{dateStr}/{filename}");
    }

    // ============ 旧版兼容方法 ============

    /// <summary>
    /// 获取图片完整路径（兼容旧 API）
    /// </summary>
    public string GetImagePath(string relativePath) => ResolveImagePath(relativePath) ?? Path.Combine(ImagesDir, relativePath);

    /// <summary>
    /// 获取图片 Base64（兼容旧 API）
    /// </summary>
    public string? GetImageBase64(string relativePath)
    {
        var (ok, data, _) = ReadImage(relativePath);
        return ok ? data : null;
    }
}
