using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 应用设置服务
/// 持久化到 appConfig 表（对应 localStorage 的 settings 键）
/// </summary>
public class SettingsService
{
    private readonly IDatabaseService _db;
    private const string SettingsKey = "settings";

    private AppSettings _settings = new();
    private bool _initialized;

    // ============ 默认设置 ============

    public static readonly AppSettings DefaultSettings = new()
    {
        Theme = "light",
        Language = "zh-CN",
        ChartColors = new ChartColors
        {
            Up = "#F56C6C",
            Down = "#67C23A",
            Neutral = "#909399"
        },
        DefaultEntryTypes = new List<string> { "突破", "回踩", "低吸", "打板", "趋势", "其他" },
        ShowWeekends = true,
        DefaultPositionStatus = "持仓中",
        EnableOcrAutoFill = true,
        EnableStockSearch = true,
        DateFormat = "YYYY-MM-DD",
        PriceDecimals = 2,
        PercentDecimals = 2
    };

    public SettingsService(IDatabaseService db)
    {
        _db = db;
    }

    public AppSettings Settings => _settings;
    public bool IsInitialized => _initialized;
    public bool Loading { get; private set; }
    public string? Error { get; private set; }

    // ============ 计算属性 ============

    public bool IsDarkTheme => _settings.Theme == "dark";

    // ============ 初始化 ============

    public void Initialize()
    {
        if (_initialized) return;
        Loading = true;
        try
        {
            var row = _db.GetById("appConfig", SettingsKey);
            if (row != null && row.TryGetValue("value", out var val) && val != null)
            {
                var json = val.ToString();
                // 大小写不敏感：兼容 旧版备份的 camelCase 键（默认精确匹配会全部反序列化失败）
                var saved = JsonSerializer.Deserialize<AppSettings>(json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (saved != null)
                {
                    // 合并：保留默认值中存在但 saved 中缺失的字段。
                    // 值类型字段无法用 null 判断缺失，传入原始 JSON 按属性存在性合并，
                    // 避免 旧版备份缺字段时被默认值反向覆盖
                    using var doc = JsonDocument.Parse(json!);
                    _settings = MergeSettings(DefaultSettings, saved, doc.RootElement);
                }
                else
                {
                    _settings = new AppSettings(DefaultSettings);
                }
            }
            else
            {
                _settings = new AppSettings(DefaultSettings);
            }
            _initialized = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[SettingsService] 初始化设置失败");
            _settings = new AppSettings(DefaultSettings);
            _initialized = true;
        }
        finally
        {
            Loading = false;
        }
    }

    // ============ 保存设置 ============

    public void SaveSettings(Action<AppSettings> updates)
    {
        Loading = true;
        Error = null;
        try
        {
            updates(_settings);
            var json = JsonSerializer.Serialize(_settings);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = SettingsKey,
                ["value"] = json
            });
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error(e, "[SettingsService] 保存设置失败");
            throw;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>
    /// 合并设置：保留默认值中存在但 saved 中缺失的字段。
    /// 值类型字段（bool/int）缺失时反序列化为 0/false，无法用 null 判断缺失，
    /// 改按原始 JSON 的属性存在性合并（兼容 PascalCase 与旧版的 camelCase 键）
    /// </summary>
    private static AppSettings MergeSettings(AppSettings defaults, AppSettings saved, JsonElement savedRaw)
    {
        bool Has(string name) => savedRaw.TryGetProperty(name, out _)
            || savedRaw.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out _);

        var result = new AppSettings(defaults);
        result.Theme = saved.Theme ?? defaults.Theme;
        result.Language = saved.Language ?? defaults.Language;
        result.ChartColors = saved.ChartColors ?? defaults.ChartColors;
        result.DefaultEntryTypes = saved.DefaultEntryTypes ?? defaults.DefaultEntryTypes;
        result.ShowWeekends = Has(nameof(AppSettings.ShowWeekends)) ? saved.ShowWeekends : defaults.ShowWeekends;
        result.DefaultPositionStatus = saved.DefaultPositionStatus ?? defaults.DefaultPositionStatus;
        result.EnableOcrAutoFill = Has(nameof(AppSettings.EnableOcrAutoFill)) ? saved.EnableOcrAutoFill : defaults.EnableOcrAutoFill;
        result.EnableStockSearch = Has(nameof(AppSettings.EnableStockSearch)) ? saved.EnableStockSearch : defaults.EnableStockSearch;
        result.DateFormat = saved.DateFormat ?? defaults.DateFormat;
        result.PriceDecimals = Has(nameof(AppSettings.PriceDecimals)) ? saved.PriceDecimals : defaults.PriceDecimals;
        result.PercentDecimals = Has(nameof(AppSettings.PercentDecimals)) ? saved.PercentDecimals : defaults.PercentDecimals;
        return result;
    }
}

// ============ 数据模型 ============

public class AppSettings
{
    public string Theme { get; set; } = "light";
    public string Language { get; set; } = "zh-CN";
    public ChartColors? ChartColors { get; set; }
    public List<string>? DefaultEntryTypes { get; set; }
    public bool ShowWeekends { get; set; } = true;
    public string DefaultPositionStatus { get; set; } = "持仓中";
    public bool EnableOcrAutoFill { get; set; } = true;
    public bool EnableStockSearch { get; set; } = true;
    public string DateFormat { get; set; } = "YYYY-MM-DD";
    public int PriceDecimals { get; set; } = 2;
    public int PercentDecimals { get; set; } = 2;

    public AppSettings() { }
    public AppSettings(AppSettings other)
    {
        Theme = other.Theme;
        Language = other.Language;
        ChartColors = other.ChartColors != null ? new ChartColors(other.ChartColors) : null;
        DefaultEntryTypes = other.DefaultEntryTypes?.ToList();
        ShowWeekends = other.ShowWeekends;
        DefaultPositionStatus = other.DefaultPositionStatus;
        EnableOcrAutoFill = other.EnableOcrAutoFill;
        EnableStockSearch = other.EnableStockSearch;
        DateFormat = other.DateFormat;
        PriceDecimals = other.PriceDecimals;
        PercentDecimals = other.PercentDecimals;
    }
}

public class ChartColors
{
    public string Up { get; set; } = "#F56C6C";
    public string Down { get; set; } = "#67C23A";
    public string Neutral { get; set; } = "#909399";

    public ChartColors() { }
    public ChartColors(ChartColors other)
    {
        Up = other.Up;
        Down = other.Down;
        Neutral = other.Neutral;
    }
}
