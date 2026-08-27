using System;
using System.IO;
using System.Text.Json;
using Serilog;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.Services;

/// <summary>
/// 宠物设置持久化 - 与 pet-window-state.json 一致，存于数据目录。
/// </summary>
public static class PetSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>设置版本号：每次保存递增，供 SchedulerPetSettingsStore 缓存失效</summary>
    public static long Version;

    private static string SettingsPath() => Path.Combine(App.DataDir, "pet-settings.json");

    public static PetSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath()))
            {
                var initial = new PetSettings { Enabled = true };
                Save(initial);
                return initial;
            }
            var settings = JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(SettingsPath())) ?? new PetSettings();
            // 迁移旧数据源（sina 已替换为 tencent）
            if (settings.PrimarySource == "sina")
            {
                settings.PrimarySource = "eastmoney";
                Save(settings);
            }
            return settings;
        }
        catch (Exception ex)
        {
            // 文件损坏（空文件/非法 JSON）→ 重写默认设置自愈：
            // 否则每次启动都读到默认值，设置面板的取消操作也会把默认值当成"已保存"状态
            Log.Warning(ex, "[宠物] 设置加载失败，已重置为默认设置");
            var recovered = new PetSettings { Enabled = true };
            Save(recovered);
            return recovered;
        }
    }

    public static void Save(PetSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(settings, Options));
            System.Threading.Interlocked.Increment(ref Version);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[宠物] 设置保存失败");
        }
    }
}