using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReviewWpf.Services;

/// <summary>
/// 备份/恢复服务 - 对应 main.cjs 的 registerBackupHandlers
/// ZIP 打包（data.json + images/）+ ZIP 导入 + JSON 导入
/// </summary>
public class BackupService
{
    private readonly DatabaseService _db;
    private readonly ImageService _imageService;
    private readonly string _dataDir;
    private readonly string _imagesDir;
    private readonly string _backupsDir;
    private readonly string _legacyScreenshotsDir;

    public BackupService(DatabaseService db, ImageService imageService, string dataDir)
    {
        _db = db;
        _imageService = imageService;
        _dataDir = dataDir;
        _imagesDir = Path.Combine(dataDir, "images");
        _backupsDir = Path.Combine(dataDir, "backups");
        _legacyScreenshotsDir = Path.Combine(dataDir, "screenshots");
        Directory.CreateDirectory(_backupsDir);
    }

    // ============ 导出 ZIP ============
    public async Task<BackupExportResult> ExportZipAsync(string savePath, string? localStorageJson = null)
    {
        try
        {
            var data = (Dictionary<string, object>)_db.ExportAll();
            if (!string.IsNullOrEmpty(localStorageJson))
            {
                data["localStorage"] = JsonSerializer.Deserialize<JsonElement>(localStorageJson);
            }

            var screenshotPaths = _imageService.CollectScreenshotPaths(data);
            var addedImages = 0;
            var missingImages = 0;
            var missingPaths = new List<string>();

            using var zip = ZipFile.Open(savePath, ZipArchiveMode.Create);
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            zip.CreateEntry("data.json").Open().Write(jsonBytes, 0, jsonBytes.Length);

            foreach (var relPath in screenshotPaths)
            {
                try
                {
                    var imgPath = _imageService.ResolveImagePath(relPath);
                    if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
                    {
                        // 在 ZIP 中创建目录结构
                        var entryName = Path.Combine("images", relPath).Replace('\\', '/');
                        zip.CreateEntryFromFile(imgPath, entryName);
                        addedImages++;
                    }
                    else
                    {
                        missingImages++;
                        missingPaths.Add(relPath);
                    }
                }
                catch (Exception ex)
                {
                    missingImages++;
                    missingPaths.Add(relPath);
                    Log.Warning(ex, "导出截图失败: {Path}", relPath);
                }
            }

            Log.Information("[导出] 需要导出截图: {Total}, 成功: {Added}, 缺失: {Missing}",
                screenshotPaths.Count, addedImages, missingImages);

            var stats = new Dictionary<string, int>();
            foreach (var kv in data)
            {
                if (kv.Value is JsonElement je && je.ValueKind == JsonValueKind.Array)
                    stats[kv.Key] = je.GetArrayLength();
            }

            var totalRecords = stats.Values.Sum();
            var message = $"导出成功：{addedImages}/{screenshotPaths.Count} 个截图，{totalRecords} 条数据";
            if (missingImages > 0)
                message += $"\n⚠️ {missingImages} 个截图文件缺失";

            return new BackupExportResult(true, savePath, stats, addedImages, missingImages,
                screenshotPaths.Count, missingPaths, message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出 ZIP 失败");
            return new BackupExportResult(false, null, null, 0, 0, 0, null, ex.Message);
        }
    }

    // ============ 自动备份 ============
    public async Task<(bool success, string? filePath, int images)> AutoBackupAsync()
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var filePath = Path.Combine(_backupsDir, $"auto-backup-{timestamp}.zip");
            var data = (Dictionary<string, object>)_db.ExportAll();
            var screenshotPaths = _imageService.CollectScreenshotPaths(data);

            using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            zip.CreateEntry("data.json").Open().Write(jsonBytes, 0, jsonBytes.Length);

            var addedImages = 0;
            foreach (var relPath in screenshotPaths)
            {
                try
                {
                    var imgPath = _imageService.ResolveImagePath(relPath);
                    if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
                    {
                        var entryName = Path.Combine("images", relPath).Replace('\\', '/');
                        zip.CreateEntryFromFile(imgPath, entryName);
                        addedImages++;
                    }
                }
                catch { /* 忽略 */ }
            }

            Log.Information("[自动备份] 已保存至: {Path}", filePath);
            return (true, filePath, addedImages);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动备份失败");
            return (false, null, 0);
        }
    }

    // ============ 导入 ZIP ============
    public async Task<BackupImportResult> ImportZipAsync(string zipFilePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            var dataEntry = zip.GetEntry("data.json");
            if (dataEntry == null) return new BackupImportResult(false, "ZIP 中未找到 data.json", 0, 0, 0, 0, 0, 0, null);

            using var stream = dataEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var jsonText = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonText)
                       ?? new Dictionary<string, JsonElement>();

            // 验证有效数据表
            var validTables = new[] { "trades", "entryTypes", "strongStocks", "problemTags",
                "dailyPicks", "monthlySummaries", "dailySummaries", "todoTemplates",
                "appConfig", "patternCases", "insights" };
            var hasValidData = validTables.Any(t => data.TryGetValue(t, out var el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0);
            if (!hasValidData) return new BackupImportResult(false, "未找到有效的数据表", 0, 0, 0, 0, 0, 0, null);

            // 导入截图
            var importedImages = 0;
            var missingImages = 0;
            Directory.CreateDirectory(_imagesDir);

            var imageEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) &&
                       (e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var entry in imageEntries)
            {
                try
                {
                    // 去掉 images/ 前缀
                    var relPath = entry.FullName;
                    if (relPath.StartsWith("images/", StringComparison.OrdinalIgnoreCase) || relPath.StartsWith("images\\", StringComparison.OrdinalIgnoreCase))
                        relPath = relPath.Substring(7);

                    var targetPath = Path.Combine(_imagesDir, relPath);
                    // Zip Slip 防护
                    var resolvedTarget = Path.GetFullPath(targetPath);
                    var resolvedDir = Path.GetFullPath(_imagesDir);
                    if (!resolvedTarget.StartsWith(resolvedDir, StringComparison.OrdinalIgnoreCase)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    entry.ExtractToFile(targetPath, true);
                    importedImages++;
                }
                catch { missingImages++; }
            }

            // 导入数据 - 直接传递 JsonElement 装箱值，由 DatabaseService 内部解析
            var importData = data.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var (added, updated, _) = _db.ImportAll(importData);

            // Electron 备份的 localStorage 段迁移（交易计划/提醒历史/宠物设置等主源数据，
            // 在表导入之后执行，以 localStorage 值覆盖同名 appConfig 双写灾备值）
            var migratedKeys = ImportLocalStorage(data);

            // localStorage 数据
            string? localStorageJson = null;
            if (data.TryGetValue("localStorage", out var lsEl))
            {
                localStorageJson = lsEl.GetRawText();
            }
            if (migratedKeys > 0)
                added += migratedKeys;

            var dataForObject = data.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            var totalNeeded = _imageService.CountNeededScreenshots(dataForObject);
            // 验证截图
            var verifiedImages = 0;
            var screenshotPaths = _imageService.CollectScreenshotPaths(dataForObject);
            foreach (var p in screenshotPaths)
            {
                var resolvedPath = _imageService.ResolveImagePath(p);
                if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath)) verifiedImages++;
            }

            var message = $"导入完成：新增 {added} 条，更新 {updated} 条";
            if (totalNeeded > 0)
            {
                message += $"\n图片：{importedImages} 个已导入，{verifiedImages}/{totalNeeded} 个可找到";
            }

            return new BackupImportResult(true, message, added, updated,
                importedImages, verifiedImages, totalNeeded, missingImages, localStorageJson);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入 ZIP 失败");
            return new BackupImportResult(false, ex.Message, 0, 0, 0, 0, 0, 0, null);
        }
    }

    // ============ 导入 JSON ============
    public BackupImportResult ImportJson(string jsonText)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonText)
                       ?? new Dictionary<string, JsonElement>();
            var validTables = new[] { "trades", "entryTypes", "strongStocks", "problemTags",
                "dailyPicks", "monthlySummaries", "dailySummaries", "todoTemplates",
                "appConfig", "patternCases", "insights" };
            var hasValidData = validTables.Any(t => data.TryGetValue(t, out var el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0);
            if (!hasValidData) return new BackupImportResult(false, "未找到有效的数据表", 0, 0, 0, 0, 0, 0, null);

            var importData = data.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var (added2, updated2, _) = _db.ImportAll(importData);
            var migrated2 = ImportLocalStorage(data);
            return new BackupImportResult(true, $"导入完成：新增 {added2 + migrated2} 条，更新 {updated2} 条",
                added2 + migrated2, updated2, 0, 0, 0, 0, null);
        }
        catch (Exception ex)
        {
            return new BackupImportResult(false, ex.Message, 0, 0, 0, 0, 0, 0, null);
        }
    }

    // ============ 导入 JSON 文件 ============
    public BackupImportResult ImportJsonFile(string filePath)
    {
        try
        {
            var jsonText = File.ReadAllText(filePath, Encoding.UTF8);
            return ImportJson(jsonText);
        }
        catch (Exception ex)
        {
            return new BackupImportResult(false, ex.Message, 0, 0, 0, 0, 0, 0, null);
        }
    }

    // ============ Electron localStorage 段迁移 ============
    // Electron 版把交易计划/自定义提醒/提醒历史/宠物设置等存在 localStorage（主源），
    // 备份 ZIP 的 data.json 里以顶层 "localStorage" 键值对携带；WPF 版这些数据
    // 持久化在 SQLite appConfig 表。此处按键名把备份值迁移到 WPF 的存储位置，
    // 对应 Electron 导入后"写回 localStorage"的步骤（对齐 main.cjs/SettingsView.vue）。
    private int ImportLocalStorage(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("localStorage", out var ls) || ls.ValueKind != JsonValueKind.Object)
            return 0;

        var imported = 0;

        // 1) 原样写入 appConfig 的键（WPF 各服务按同名键读取，值均为 JSON 字符串）
        // 清单对齐 Electron src/utils/backupKeys.js 的 BACKUP_LOCALSTORAGE_KEYS
        var directKeys = new[]
        {
            "pet_trade_plans", "pet_custom_reminders", "pet_reminder_history",
            "pet_signal_events", "pet_signal_stats", "pet_evolution_attribution",
            "pet_signal_weight_multipliers", "pet_multifactor_weights",
            "pet_auto_optimized_rapid_windows", "pet_auto_optimized_sell", "pet_missed_sell_analysis",
            "showStrongStocks", "tradeDisplayMode",   // 交易记录显示偏好（YearMonthViewModel 同名键读取）
            "pet_position", "pet_on_top"              // 宠物窗口位置/置顶（PetViewModel 同名键读取）
        };
        foreach (var key in directKeys)
        {
            if (TryGetString(ls, key, out var value))
            {
                // pet_position 格式差异：Electron 存 JSON {"x":..,"y":..}，WPF 存 "x,y"
                if (key == "pet_position")
                    value = ConvertPetPosition(value);
                _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = key, ["value"] = value });
                imported++;
            }
        }

        // 2) 键名映射：Electron 键 → WPF appConfig 键
        if (TryGetString(ls, "pet_active_pet_id", out var activePetId))
        {
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = "activePetId", ["value"] = activePetId });
            imported++;
        }
        if (TryGetString(ls, "pet_settings", out var petSettings))
        {
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = "pet_settings", ["value"] = petSettings });
            imported++;
        }
        if (TryGetString(ls, "webdavConfig", out var webdav))
        {
            // Electron 把自动同步开关存独立键 autoSyncEnabled；WPF 合并在 webdavConfig.autoSync
            if (TryGetString(ls, "autoSyncEnabled", out var autoSync) && bool.TryParse(autoSync, out var enabled))
            {
                try
                {
                    var obj = new Dictionary<string, JsonElement>();
                    using (var doc = JsonDocument.Parse(webdav))
                        foreach (var p in doc.RootElement.EnumerateObject())
                            obj[p.Name] = p.Value.Clone();
                    obj["autoSync"] = JsonSerializer.SerializeToElement(enabled);
                    webdav = JsonSerializer.Serialize(obj);
                }
                catch { /* 解析失败保持原值 */ }
            }
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = "webdavConfig", ["value"] = webdav });
            imported++;
        }

        // 3) 组合键：OCR 密钥（Electron 散键 → WPF ocrConfig 整包）
        var hasOcrKey = TryGetString(ls, "baiduOcrApiKey", out var ocrKey);
        var hasOcrSecret = TryGetString(ls, "baiduOcrSecretKey", out var ocrSecret);
        if (hasOcrKey || hasOcrSecret)
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = "ocrConfig",
                ["value"] = JsonSerializer.Serialize(new
                {
                    apiKey = hasOcrKey ? ocrKey : "",
                    secretKey = hasOcrSecret ? ocrSecret : ""
                })
            });
            imported++;
        }

        // 4) 组合键：显示样式（Electron 散键 → WPF displayConfig 整包）
        var styles = new Dictionary<string, string>();
        foreach (var (lsKey, cfgKey) in new[]
                 {
                     ("insightListStyle", "insightListStyle"),
                     ("insightPaperScrollMode", "insightPaperScrollMode"),
                     ("diaryDisplayStyle", "diaryStyle"),
                     ("diaryListStyle", "diaryListStyle"),
                     ("paperScrollMode", "paperScrollMode")
                 })
        {
            if (TryGetString(ls, lsKey, out var styleValue)) styles[cfgKey] = styleValue;
        }
        if (styles.Count > 0)
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = "displayConfig",
                ["value"] = JsonSerializer.Serialize(new
                {
                    insightListStyle = styles.GetValueOrDefault("insightListStyle", "card"),
                    insightPaperScrollMode = styles.GetValueOrDefault("insightPaperScrollMode", "vertical"),
                    diaryStyle = styles.GetValueOrDefault("diaryStyle", "card"),
                    diaryListStyle = styles.GetValueOrDefault("diaryListStyle", "card"),
                    paperScrollMode = styles.GetValueOrDefault("paperScrollMode", "vertical")
                })
            });
            imported++;
        }

        if (imported > 0)
            Log.Information("[导入] Electron localStorage 数据迁移完成: {Count} 个键", imported);
        return imported;
    }

    private static bool TryGetString(JsonElement obj, string key, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        value = s;
        return true;
    }

    /// <summary>Electron pet_position（JSON {"x":..,"y":..}）→ WPF "x,y"；非 JSON 原样返回。</summary>
    private static string ConvertPetPosition(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object &&
                r.TryGetProperty("x", out var x) && r.TryGetProperty("y", out var y) &&
                x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number)
                return $"{x.GetDouble()},{y.GetDouble()}";
        }
        catch { /* 非 JSON（已是 WPF 格式）原样返回 */ }
        return value;
    }
}

// ============ 结果类型 ============
public record BackupExportResult(
    bool Success,
    string? FilePath,
    Dictionary<string, int>? Stats,
    int Images,
    int MissingImages,
    int TotalScreenshots,
    List<string>? MissingPaths,
    string Message);

public record BackupImportResult(
    bool Success,
    string Message,
    int Added,
    int Updated,
    int Images,
    int VerifiedImages,
    int TotalNeededScreenshots,
    int MissingImages,
    string? LocalStorageJson);
