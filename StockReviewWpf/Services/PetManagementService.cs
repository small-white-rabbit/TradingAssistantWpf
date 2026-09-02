using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReviewWpf.Services;

/// <summary>
/// 宠物外观包管理 - 对应原版宠物包 IPC handlers
/// 参考 awesome-codex-pet 仓库格式：pets/<pet-id>/{pet.json, spritesheet.webp}
/// </summary>
public class PetManagementService
{
    private readonly HttpClient _httpClient;
    private readonly string _petsDir;
    private readonly IDatabaseService _db;

    private const string CodexPetRawBase = "https://raw.githubusercontent.com/legeling/awesome-codex-pet/main";
    private const string CodexPetCatalogUrl = CodexPetRawBase + "/pets.json";

    // 宠物 ID 格式校验
    private static readonly Regex PetIdRegex = new(@"^[a-z0-9]+(-[a-z0-9]+)*--[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public PetManagementService(HttpClient httpClient, string dataDir, IDatabaseService db)
    {
        _httpClient = httpClient;
        _petsDir = Path.Combine(dataDir, "pets");
        _db = db;
        Directory.CreateDirectory(_petsDir);
    }

    // ============ appConfig 读写 ============
    private string? ReadAppConfig(string key)
    {
        try
        {
            var config = _db.GetAll("appConfig");
            foreach (var c in config)
            {
                if (c.TryGetValue("key", out var k) && k?.ToString() == key)
                {
                    c.TryGetValue("value", out var v);
                    return v?.ToString();
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            // 问题③排查关键点：读取异常被吞会让 GetActivePet 返回 null → 回落默认流萤
            Log.Warning(ex, "[宠物] 读取 appConfig.{Key} 失败", key);
            return null;
        }
    }

    private bool WriteAppConfig(string key, string? value)
    {
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?> { ["key"] = key, ["value"] = value });
            return true;
        }
        catch { return false; }
    }

    // ============ 列出已安装的宠物包 ============
    public List<InstalledPetInfo> ListInstalledPets()
    {
        var result = new List<InstalledPetInfo>();
        try
        {
            foreach (var dir in Directory.GetDirectories(_petsDir))
            {
                var petJsonPath = Path.Combine(dir, "pet.json");
                // 精灵图 webp（在线包）或 png（本地/种子包）任一存在即可
                var hasSprite = File.Exists(Path.Combine(dir, "spritesheet.webp"))
                             || File.Exists(Path.Combine(dir, "spritesheet.png"));
                if (!File.Exists(petJsonPath) || !hasSprite) continue;

                try
                {
                    var json = File.ReadAllText(petJsonPath, Encoding.UTF8);
                    var meta = JsonSerializer.Deserialize<JsonElement>(json);
                    var folderName = Path.GetFileName(dir);
                    result.Add(new InstalledPetInfo
                    {
                        Id = meta.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? folderName : folderName,
                        DisplayName = meta.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? folderName : folderName,
                        Description = meta.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "",
                        SpriteVersionNumber = meta.TryGetProperty("spriteVersionNumber", out var svEl) ? svEl.GetInt32() : 1,
                        FolderName = folderName,
                        CustomLayout = meta.TryGetProperty("customLayout", out var clEl) ? clEl.GetRawText() : null
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "跳过损坏的 pet.json: {Folder}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "列出已安装宠物失败");
        }
        return result;
    }

    // ============ 安装宠物包（从 GitHub 下载） ============
    public async Task<(bool success, InstalledPetInfo? info, string? error)> InstallPetAsync(string petId)
    {
        if (string.IsNullOrEmpty(petId) || !PetIdRegex.IsMatch(petId))
            return (false, null, "无效的宠物标识符，期望格式 pet-slug--author-slug");

        var targetDir = Path.Combine(_petsDir, petId);
        try
        {
            Directory.CreateDirectory(targetDir);

            // 下载 pet.json
            var petJsonUrl = $"{CodexPetRawBase}/pets/{petId}/pet.json";
            var petJsonText = await _httpClient.GetStringAsync(petJsonUrl);
            File.WriteAllText(Path.Combine(targetDir, "pet.json"), petJsonText, Encoding.UTF8);
            var petMeta = JsonSerializer.Deserialize<JsonElement>(petJsonText);

            // 下载 spritesheet.webp
            var spriteUrl = $"{CodexPetRawBase}/pets/{petId}/spritesheet.webp";
            var spriteBytes = await _httpClient.GetByteArrayAsync(spriteUrl);
            await File.WriteAllBytesAsync(Path.Combine(targetDir, "spritesheet.webp"), spriteBytes);

            return (true, new InstalledPetInfo
            {
                Id = petMeta.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? petId : petId,
                DisplayName = petMeta.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? petId : petId,
                Description = petMeta.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "",
                SpriteVersionNumber = petMeta.TryGetProperty("spriteVersionNumber", out var svEl) ? svEl.GetInt32() : 1,
                FolderName = petId
            }, null);
        }
        catch (Exception ex)
        {
            // 清理半成品
            try
            {
                if (Directory.Exists(targetDir) && Directory.GetFiles(targetDir).Length == 0)
                    Directory.Delete(targetDir, true);
            }
            catch { /* 忽略 */ }
            return (false, null, ex.Message);
        }
    }

    // ============ 卸载宠物包 ============
    public (bool success, bool wasMissing, string? error) UninstallPet(string petId)
    {
        if (string.IsNullOrEmpty(petId) || !PetIdRegex.IsMatch(petId))
            return (false, false, "无效的宠物标识符");

        try
        {
            var petDir = Path.Combine(_petsDir, petId);
            if (!Directory.Exists(petDir)) return (true, true, null);
            Directory.Delete(petDir, true);

            // 若被卸载的是当前激活宠物，则清空激活
            var activeId = ReadAppConfig("activePetId");
            if (activeId == petId)
            {
                WriteAppConfig("activePetId", null);
            }
            return (true, false, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    // ============ 读取单只宠物元数据 ============
    public (bool success, JsonElement? meta, string? error) ReadPetMeta(string petId)
    {
        try
        {
            if (string.IsNullOrEmpty(petId)) return (false, null, "缺少 petId");
            var petJsonPath = Path.Combine(_petsDir, petId, "pet.json");
            if (!File.Exists(petJsonPath)) return (false, null, "未找到该宠物的 pet.json");
            var json = File.ReadAllText(petJsonPath, Encoding.UTF8);
            var meta = JsonSerializer.Deserialize<JsonElement>(json);
            return (true, meta, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ============ 保存精灵图自定义布局 ============
    public (bool success, string? customLayoutJson, string? error) SaveLayout(string petId, PetLayout layout)
    {
        try
        {
            if (string.IsNullOrEmpty(petId)) return (false, null, "缺少 petId");
            if (layout?.Rows == null) return (false, null, "layout 格式不正确，需要 rows 数组");

            var petJsonPath = Path.Combine(_petsDir, petId, "pet.json");
            if (!File.Exists(petJsonPath)) return (false, null, "未找到该宠物的 pet.json");

            var json = File.ReadAllText(petJsonPath, Encoding.UTF8);
            var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            meta["customLayout"] = JsonSerializer.SerializeToElement(new
            {
                rows = layout.Rows,
                imageWidth = layout.ImageWidth,
                imageHeight = layout.ImageHeight
            });
            File.WriteAllText(petJsonPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            return (true, meta["customLayout"].GetRawText(), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ============ 读取当前激活宠物 ID ============
    public string? GetActivePet()
    {
        return ReadAppConfig("activePetId");
    }

    // ============ 设置当前激活宠物 ============
    public bool SetActivePet(string? petId)
    {
        return WriteAppConfig("activePetId", petId);
    }

    // ============ 获取在线目录 ============
    // 目录缓存：5 分钟内重复打开画廊不再请求网络（打开慢的主因）
    private JsonElement? _catalogCache;
    private DateTime _catalogCacheAt = DateTime.MinValue;

    public async Task<(bool success, JsonElement? catalog, string? error)> GetCatalogAsync()
    {
        if (_catalogCache != null && (DateTime.UtcNow - _catalogCacheAt).TotalMinutes < 5)
            return (true, _catalogCache, null);
        try
        {
            var json = await _httpClient.GetStringAsync(CodexPetCatalogUrl);
            var catalog = JsonSerializer.Deserialize<JsonElement>(json);
            _catalogCache = catalog;
            _catalogCacheAt = DateTime.UtcNow;
            return (true, catalog, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ============ DB 备份键（宠物重要数据双写） ============
    private static readonly HashSet<string> PetDbBackupKeys = new()
    {
        "pet_custom_reminders",
        "pet_signal_weight_multipliers",
        "pet_multifactor_weights",
        "pet_signal_stats",
        "pet_auto_optimized_rapid_windows",
        "pet_auto_optimized_sell"
    };

    public (bool success, string? data) DbBackupGet(string key)
    {
        if (!PetDbBackupKeys.Contains(key)) return (false, null);
        return (true, ReadAppConfig(key));
    }

    public bool DbBackupSet(string key, string? value)
    {
        if (!PetDbBackupKeys.Contains(key)) return false;
        return WriteAppConfig(key, value);
    }
}

// ============ 数据模型 ============
public class InstalledPetInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public int SpriteVersionNumber { get; set; } = 1;
    public string FolderName { get; set; } = "";
    public string? CustomLayout { get; set; }
}

public class PetLayout
{
    public List<PetLayoutRow> Rows { get; set; } = new();
    public int ImageWidth { get; set; } = 1536;
    public int ImageHeight { get; set; }
}

public class PetLayoutRow
{
    public int Y { get; set; }
    public int Height { get; set; } = 208;
    public int X { get; set; }
}
