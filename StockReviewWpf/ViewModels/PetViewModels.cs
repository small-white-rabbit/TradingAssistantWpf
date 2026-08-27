using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Data;
using StockReview.Core.Services;
using Serilog;
using StockReviewWpf.Models;

namespace StockReviewWpf.ViewModels;

// ============================================================================
// 文件说明：宠物相关 ViewModels
// 合并翻译自 4 个 Pinia stores:
//   1. petStore.js      → PetViewModel（宠物状态：情绪/位置/置顶）
//   2. petSettingsStore.js → PetSettingsViewModel（宠物设置：数据源/阈值/提醒/外观）
//   3. petAppearanceStore.js → PetAppearanceViewModel（外观包：已安装/在线目录/激活）
//   4. bubbleSchedulerStore.js → 已翻译为 BubbleSchedulerService（Core 层）
// ============================================================================

#region PetViewModel（对应 petStore.js）

/// <summary>
/// 宠物情绪类型
/// </summary>
public enum PetMoodType
{
    Idle,
    Focused,
    Nervous,
    Excited,
    Angry,
    Crying,
    Happy,
    Sleeping,
    Anxious,
    Forbidden,
    Celebrating,
    Working,
    Resting
}

/// <summary>
/// 宠物情绪配置
/// </summary>
public static class PetMoodConfig
{
    public static readonly Dictionary<PetMoodType, (string Emoji, string Label, string Animation)> Config = new()
    {
        [PetMoodType.Idle] = ("😊", "悠闲", "idle"),
        [PetMoodType.Focused] = ("🤔", "专注", "focused"),
        [PetMoodType.Nervous] = ("😰", "紧张", "nervous"),
        [PetMoodType.Excited] = ("🎉", "兴奋", "excited"),
        [PetMoodType.Angry] = ("😠", "生气", "angry"),
        [PetMoodType.Crying] = ("😢", "哭泣", "crying"),
        [PetMoodType.Happy] = ("🥰", "开心", "happy"),
        [PetMoodType.Sleeping] = ("😴", "睡觉", "sleeping"),
        [PetMoodType.Anxious] = ("😟", "焦虑", "anxious"),
        [PetMoodType.Forbidden] = ("🚫", "禁止", "forbidden"),
        [PetMoodType.Celebrating] = ("🎊", "庆祝", "celebrating"),
        [PetMoodType.Working] = ("💼", "工作中", "working"),
        [PetMoodType.Resting] = ("☕", "休息中", "resting")
    };

    // 可被工作/休息自动切换覆盖的"静息态"
    private static readonly HashSet<PetMoodType> PassiveMoods = new()
    {
        PetMoodType.Idle, PetMoodType.Working, PetMoodType.Resting, PetMoodType.Sleeping
    };

    public static bool IsPassive(PetMoodType mood) => PassiveMoods.Contains(mood);

    public static string GetEmoji(PetMoodType mood) =>
        Config.TryGetValue(mood, out var c) ? c.Emoji : "😊";
    public static string GetLabel(PetMoodType mood) =>
        Config.TryGetValue(mood, out var c) ? c.Label : "未知";
    public static string GetAnimation(PetMoodType mood) =>
        Config.TryGetValue(mood, out var c) ? c.Animation : "idle";
}

/// <summary>
/// 宠物状态 ViewModel — 对应 petStore.js
/// 管理宠物的情绪、位置、置顶等状态
/// </summary>
public partial class PetViewModel : ObservableObject
{
    // 存储键
    private const string KeyPosition = "pet_position";
    private const string KeyMood = "pet_mood";
    private const string KeyOnTop = "pet_on_top";

    private readonly DatabaseService _db;
    private readonly PetSettingsViewModel _settings;
    private readonly BubbleSchedulerService _bubbleScheduler;

    [ObservableProperty]
    private double _positionX = 100;

    [ObservableProperty]
    private double _positionY = 100;

    [ObservableProperty]
    private PetMoodType _mood = PetMoodType.Idle;

    [ObservableProperty]
    private bool _isOnTop = true;

    [ObservableProperty]
    private bool _isVisible = true;

    // 气泡相关
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBubble))]
    private string? _bubbleTitle;

    [ObservableProperty]
    private string? _bubbleContent;

    [ObservableProperty]
    private string? _bubbleLevel = "info";

    [ObservableProperty]
    private bool _bubblePersistent;

    public bool HasBubble => !string.IsNullOrEmpty(BubbleTitle);

    public PetViewModel(DatabaseService db, PetSettingsViewModel settings, BubbleSchedulerService bubbleScheduler)
    {
        _db = db;
        _settings = settings;
        _bubbleScheduler = bubbleScheduler;
        LoadState();
    }

    private void LoadState()
    {
        try
        {
            var posRow = _db.GetById("appConfig", KeyPosition);
            if (posRow != null && posRow.TryGetValue("value", out var v) && v != null)
            {
                var parts = v.ToString()!.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out var x) &&
                    double.TryParse(parts[1], out var y))
                {
                    PositionX = x;
                    PositionY = y;
                }
            }

            var moodRow = _db.GetById("appConfig", KeyMood);
            if (moodRow != null && moodRow.TryGetValue("value", out var mv) && mv != null)
            {
                if (Enum.TryParse<PetMoodType>(mv.ToString(), true, out var mood))
                    Mood = mood;
            }

            var topRow = _db.GetById("appConfig", KeyOnTop);
            if (topRow != null && topRow.TryGetValue("value", out var tv) && tv != null)
            {
                IsOnTop = tv.ToString() == "true" || tv.ToString() == "True";
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetViewModel] 加载状态失败");
        }
    }

    private void SavePosition()
    {
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = KeyPosition,
                ["value"] = $"{PositionX},{PositionY}"
            });
        }
        catch (Exception e) { Log.Warning(e, "[PetViewModel] 保存位置失败"); }
    }

    private void SaveMood()
    {
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = KeyMood,
                ["value"] = Mood.ToString()
            });
        }
        catch (Exception e) { Log.Warning(e, "[PetViewModel] 保存情绪失败"); }
    }

    private void SaveOnTop()
    {
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = KeyOnTop,
                ["value"] = IsOnTop.ToString().ToLower()
            });
        }
        catch (Exception e) { Log.Warning(e, "[PetViewModel] 保存置顶失败"); }
    }

    // ============ Actions ============

    public void MoveTo(double x, double y)
    {
        PositionX = x;
        PositionY = y;
        SavePosition();
    }

    [RelayCommand]
    public void SetMood(PetMoodType newMood)
    {
        Mood = newMood;
        SaveMood();
        Log.Debug("[PetViewModel] 情绪切换: {Mood}", newMood);
    }

    /// <summary>
    /// 设置主动反馈态情绪（nervous/excited 等），不受 passive 覆盖影响
    /// </summary>
    public void SetFeedbackMood(PetMoodType mood, int durationMs = 0)
    {
        SetMood(mood);
        if (durationMs > 0)
        {
            // 定时后恢复为 Idle
            Task.Delay(durationMs).ContinueWith(_ =>
            {
                if (Mood == mood) SetMood(PetMoodType.Idle);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>
    /// 自动切换工作/休息/睡眠（仅覆盖 passive moods）
    /// </summary>
    public void AutoSwitchMood(PetMoodType newMood)
    {
        if (PetMoodConfig.IsPassive(Mood))
        {
            SetMood(newMood);
        }
    }

    [RelayCommand]
    public void ToggleOnTop()
    {
        IsOnTop = !IsOnTop;
        SaveOnTop();
    }

    public void ShowBubble(string title, string? content = null, string? level = "info", bool persistent = false)
    {
        BubbleTitle = title;
        BubbleContent = content;
        BubbleLevel = level;
        BubblePersistent = persistent;
    }

    [RelayCommand]
    public void HideBubble()
    {
        BubbleTitle = null;
        BubbleContent = null;
    }

    /// <summary>
    /// 获取当前情绪 emoji
    /// </summary>
    public string CurrentEmoji => PetMoodConfig.GetEmoji(Mood);
    public string CurrentMoodLabel => PetMoodConfig.GetLabel(Mood);
    public string CurrentAnimation => PetMoodConfig.GetAnimation(Mood);
}

#endregion

// ============================================================================
// PetSettingsViewModel（对应 petSettingsStore.js）
// ============================================================================

/// <summary>
/// 宠物设置 ViewModel — 对应 petSettingsStore.js
/// 管理数据源、监控阈值、卖点识别、提醒强度、外观等配置
/// </summary>
public partial class PetSettingsViewModel : ObservableObject
{
    private const string SettingsKey = "pet_settings";
    private readonly DatabaseService _db;

    // PropertyNameCaseInsensitive：兼容 Electron 备份的 camelCase 字段（WPF 自身写入为 PascalCase）
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };

    // ============ 数据源 ============
    [ObservableProperty] private string _primarySource = "eastmoney";
    [ObservableProperty] private int _refreshInterval = 10000;

    // 富途牛牛
    [ObservableProperty] private bool _futuEnabled;
    [ObservableProperty] private string _futuHost = "127.0.0.1";
    [ObservableProperty] private int _futuPort = 11111;
    [ObservableProperty] private string _futuPythonPath = "python";
    [ObservableProperty] private string _futuOpenDPath = "";
    [ObservableProperty] private bool _opendAlertEnabled = true;

    // ============ 监控阈值 ============
    [ObservableProperty] private double _priceChangeThreshold = 2;
    [ObservableProperty] private double _priceNearThreshold = 1;

    // ============ 卖点识别 ============
    [ObservableProperty] private bool _sellPointDetection = true;
    [ObservableProperty] private double _surgePullbackThreshold = 3;
    [ObservableProperty] private double _volumeAmplifyMultiple = 2;
    [ObservableProperty] private double _stagnantThreshold = 0.5;

    // ============ 关键位置 ============
    [ObservableProperty] private bool _keyLevelDetection = true;
    [ObservableProperty] private double _supportBreakdownTolerance = 1;

    // ============ 收盘前 MA5 检查 ============
    [ObservableProperty] private bool _preCloseMA5Check = true;

    // ============ 提醒强度 ============
    [ObservableProperty] private bool _reminderEnabled = true;
    [ObservableProperty] private bool _screenFlashEnabled = true;
    [ObservableProperty] private bool _fullscreenOverlayEnabled;
    [ObservableProperty] private int _bubbleDisplayDuration = 30000;
    [ObservableProperty] private int _bubbleDurationInsight = 60000;
    [ObservableProperty] private int _bubbleDurationTrade = -1;
    [ObservableProperty] private int _bubbleDurationSignal = -1;

    // ============ 盘后提醒 ============
    [ObservableProperty] private int _afterMarketReminderInterval = 3;

    // ============ 行为 ============
    [ObservableProperty] private bool _autoStart;

    // ============ 心得分心提醒 ============
    [ObservableProperty] private bool _insightReminderEnabled = true;
    [ObservableProperty] private int _insightReminderInterval = 60;
    [ObservableProperty] private int _insightMinStars = 4;

    // ============ 外观 ============
    [ObservableProperty] private double _petSize = 1.0;
    [ObservableProperty] private double _petOpacity = 1.0;
    [ObservableProperty] private double _bubbleBackgroundOpacity = 1.0;
    [ObservableProperty] private double _animationSpeed = 1.0;

    public PetSettingsViewModel(DatabaseService db)
    {
        _db = db;
        LoadFromStorage();
    }

    private void LoadFromStorage()
    {
        try
        {
            var row = _db.GetById("appConfig", SettingsKey);
            if (row == null || !row.TryGetValue("value", out var v) || v == null) return;
            var s = JsonSerializer.Deserialize<PetSettingsData>(v.ToString()!, JsonOpts);
            if (s == null) return;

            PrimarySource = s.PrimarySource ?? "eastmoney";
            RefreshInterval = s.RefreshInterval ?? 10000;
            FutuEnabled = s.FutuEnabled ?? false;
            FutuHost = s.FutuHost ?? "127.0.0.1";
            FutuPort = s.FutuPort ?? 11111;
            FutuPythonPath = s.FutuPythonPath ?? "python";
            FutuOpenDPath = s.FutuOpenDPath ?? "";
            OpendAlertEnabled = s.OpendAlertEnabled ?? true;
            PriceChangeThreshold = s.PriceChangeThreshold ?? 2;
            PriceNearThreshold = s.PriceNearThreshold ?? 1;
            SellPointDetection = s.SellPointDetection ?? true;
            SurgePullbackThreshold = s.SurgePullbackThreshold ?? 3;
            VolumeAmplifyMultiple = s.VolumeAmplifyMultiple ?? 2;
            StagnantThreshold = s.StagnantThreshold ?? 0.5;
            KeyLevelDetection = s.KeyLevelDetection ?? true;
            SupportBreakdownTolerance = s.SupportBreakdownTolerance ?? 1;
            PreCloseMA5Check = s.PreCloseMA5Check ?? true;
            ReminderEnabled = s.ReminderEnabled ?? true;
            ScreenFlashEnabled = s.ScreenFlashEnabled ?? true;
            FullscreenOverlayEnabled = s.FullscreenOverlayEnabled ?? false;
            BubbleDisplayDuration = s.BubbleDisplayDuration ?? 30000;
            BubbleDurationInsight = s.BubbleDurationInsight ?? 60000;
            BubbleDurationTrade = s.BubbleDurationTrade ?? -1;
            BubbleDurationSignal = s.BubbleDurationSignal ?? -1;
            AfterMarketReminderInterval = s.AfterMarketReminderInterval ?? 3;
            AutoStart = s.AutoStart ?? false;
            InsightReminderEnabled = s.InsightReminderEnabled ?? true;
            InsightReminderInterval = s.InsightReminderInterval ?? 60;
            InsightMinStars = s.InsightMinStars ?? 4;
            PetSize = s.PetSize ?? 1.0;
            PetOpacity = s.PetOpacity ?? 1.0;
            BubbleBackgroundOpacity = s.BubbleBackgroundOpacity ?? 1.0;
            AnimationSpeed = s.AnimationSpeed ?? 1.0;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetSettings] 加载失败");
        }
    }

    [RelayCommand]
    public void Save()
    {
        try
        {
            var data = new PetSettingsData
            {
                PrimarySource = PrimarySource,
                RefreshInterval = RefreshInterval,
                FutuEnabled = FutuEnabled,
                FutuHost = FutuHost,
                FutuPort = FutuPort,
                FutuPythonPath = FutuPythonPath,
                FutuOpenDPath = FutuOpenDPath,
                OpendAlertEnabled = OpendAlertEnabled,
                PriceChangeThreshold = PriceChangeThreshold,
                PriceNearThreshold = PriceNearThreshold,
                SellPointDetection = SellPointDetection,
                SurgePullbackThreshold = SurgePullbackThreshold,
                VolumeAmplifyMultiple = VolumeAmplifyMultiple,
                StagnantThreshold = StagnantThreshold,
                KeyLevelDetection = KeyLevelDetection,
                SupportBreakdownTolerance = SupportBreakdownTolerance,
                PreCloseMA5Check = PreCloseMA5Check,
                ReminderEnabled = ReminderEnabled,
                ScreenFlashEnabled = ScreenFlashEnabled,
                FullscreenOverlayEnabled = FullscreenOverlayEnabled,
                BubbleDisplayDuration = BubbleDisplayDuration,
                BubbleDurationInsight = BubbleDurationInsight,
                BubbleDurationTrade = BubbleDurationTrade,
                BubbleDurationSignal = BubbleDurationSignal,
                AfterMarketReminderInterval = AfterMarketReminderInterval,
                AutoStart = AutoStart,
                InsightReminderEnabled = InsightReminderEnabled,
                InsightReminderInterval = InsightReminderInterval,
                InsightMinStars = InsightMinStars,
                PetSize = PetSize,
                PetOpacity = PetOpacity,
                BubbleBackgroundOpacity = BubbleBackgroundOpacity,
                AnimationSpeed = AnimationSpeed
            };
            var json = JsonSerializer.Serialize(data, JsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = SettingsKey,
                ["value"] = json
            });
            Log.Information("[PetSettings] 设置已保存");
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetSettings] 保存失败");
        }
    }

    /// <summary>
    /// 获取指定提醒类型的气泡显示时长
    /// </summary>
    public int GetBubbleDuration(string reminderType)
    {
        int duration = reminderType switch
        {
            "insight" => BubbleDurationInsight,
            "trade" => BubbleDurationTrade,
            "signal" => BubbleDurationSignal,
            _ => -1
        };
        return duration >= 0 ? duration : BubbleDisplayDuration;
    }

    // 内部序列化模型
    private class PetSettingsData
    {
        public string? PrimarySource { get; set; }
        public int? RefreshInterval { get; set; }
        public bool? FutuEnabled { get; set; }
        public string? FutuHost { get; set; }
        public int? FutuPort { get; set; }
        public string? FutuPythonPath { get; set; }
        public string? FutuOpenDPath { get; set; }
        public bool? OpendAlertEnabled { get; set; }
        public double? PriceChangeThreshold { get; set; }
        public double? PriceNearThreshold { get; set; }
        public bool? SellPointDetection { get; set; }
        public double? SurgePullbackThreshold { get; set; }
        public double? VolumeAmplifyMultiple { get; set; }
        public double? StagnantThreshold { get; set; }
        public bool? KeyLevelDetection { get; set; }
        public double? SupportBreakdownTolerance { get; set; }
        public bool? PreCloseMA5Check { get; set; }
        public bool? ReminderEnabled { get; set; }
        public bool? ScreenFlashEnabled { get; set; }
        public bool? FullscreenOverlayEnabled { get; set; }
        public int? BubbleDisplayDuration { get; set; }
        public int? BubbleDurationInsight { get; set; }
        public int? BubbleDurationTrade { get; set; }
        public int? BubbleDurationSignal { get; set; }
        public int? AfterMarketReminderInterval { get; set; }
        public bool? AutoStart { get; set; }
        public bool? InsightReminderEnabled { get; set; }
        public int? InsightReminderInterval { get; set; }
        public int? InsightMinStars { get; set; }
        public double? PetSize { get; set; }
        public double? PetOpacity { get; set; }
        public double? BubbleBackgroundOpacity { get; set; }
        public double? AnimationSpeed { get; set; }
    }
}

// ============================================================================
// PetAppearanceViewModel（对应 petAppearanceStore.js）
// ============================================================================

/// <summary>
/// 宠物外观信息
/// </summary>
public class PetInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? SpritesheetPath { get; set; }
    public int SpriteVersionNumber { get; set; } = 1;
    public string? Author { get; set; }
    public string? Avatar { get; set; }
}

/// <summary>
/// 宠物外观包 ViewModel — 对应 petAppearanceStore.js
/// 管理从 awesome-codex-pet 安装的精灵图宠物外观
/// </summary>
public partial class PetAppearanceViewModel : ObservableObject
{
    private const string ActiveKey = "pet_active_pet_id";
    private const string DefaultPetId = "firefly--lingxiaotian";

    private readonly DatabaseService _db;

    // 已安装的宠物列表
    public ObservableCollection<PetInfo> InstalledPets { get; } = new();

    // 在线目录
    public ObservableCollection<PetInfo> Catalog { get; } = new();

    [ObservableProperty]
    private string? _activePetId;

    [ObservableProperty]
    private PetInfo? _activePetMeta;

    [ObservableProperty]
    private bool _isLoadingInstalled;

    [ObservableProperty]
    private bool _isLoadingCatalog;

    // 正在安装的宠物 ID 集合
    private readonly HashSet<string> _installingPetIds = new();
    public bool IsInstalling(string petId) => _installingPetIds.Contains(petId);

    public PetAppearanceViewModel(DatabaseService db)
    {
        _db = db;
        LoadActivePetId();
    }

    private void LoadActivePetId()
    {
        try
        {
            var row = _db.GetById("appConfig", ActiveKey);
            if (row != null && row.TryGetValue("value", out var v) && v != null)
            {
                ActivePetId = v.ToString();
            }
            else
            {
                ActivePetId = DefaultPetId;
            }
        }
        catch
        {
            ActivePetId = DefaultPetId;
        }
    }

    /// <summary>
    /// 设置激活宠物
    /// </summary>
    [RelayCommand]
    public void SetActivePet(string petId)
    {
        ActivePetId = petId;
        try
        {
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = ActiveKey,
                ["value"] = petId
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetAppearance] 保存激活宠物失败");
        }

        // 更新元数据
        ActivePetMeta = InstalledPets.FirstOrDefault(p => p.Id == petId)
                     ?? Catalog.FirstOrDefault(p => p.Id == petId);

        Log.Information("[PetAppearance] 激活宠物: {PetId}", petId);
    }

    /// <summary>
    /// 加载已安装宠物列表（WPF 版从本地目录扫描）
    /// </summary>
    [RelayCommand]
    public async Task LoadInstalledPetsAsync()
    {
        IsLoadingInstalled = true;
        try
        {
            // WPF 版：从本地宠物目录扫描
            // 统一使用 App.DataDir\pets（与渲染侧 PetSpriteControl 一致，且遵循 data-dir.json 自定义目录）
            var petsDir = System.IO.Path.Combine(App.DataDir, "pets");

            InstalledPets.Clear();
            if (!System.IO.Directory.Exists(petsDir))
            {
                Log.Information("[PetAppearance] 宠物目录不存在: {Dir}", petsDir);
                return;
            }

            foreach (var dir in System.IO.Directory.GetDirectories(petsDir))
            {
                var petJsonPath = System.IO.Path.Combine(dir, "pet.json");
                if (!System.IO.File.Exists(petJsonPath)) continue;

                try
                {
                    var json = await System.IO.File.ReadAllTextAsync(petJsonPath);
                    var info = JsonSerializer.Deserialize<PetInfo>(json);
                    if (info != null && !string.IsNullOrEmpty(info.Id))
                    {
                        info.SpritesheetPath = System.IO.Path.Combine(dir, "spritesheet.webp");
                        InstalledPets.Add(info);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(e, "[PetAppearance] 解析 pet.json 失败: {Path}", petJsonPath);
                }
            }

            Log.Information("[PetAppearance] 已加载 {Count} 个已安装宠物", InstalledPets.Count);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetAppearance] 加载已安装宠物失败");
        }
        finally
        {
            IsLoadingInstalled = false;
        }
    }

    /// <summary>
    /// 加载在线目录（从 GitHub API 获取 awesome-codex-pet 目录）
    /// </summary>
    [RelayCommand]
    public async Task LoadCatalogAsync()
    {
        IsLoadingCatalog = true;
        try
        {
            // WPF 版：从 GitHub API 获取目录
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = "https://raw.githubusercontent.com/legeling/awesome-codex-pet/main/pets/catalog.json";
            var resp = await http.GetStringAsync(url);
            var items = JsonSerializer.Deserialize<List<PetInfo>>(resp);
            Catalog.Clear();
            if (items != null)
            {
                foreach (var item in items)
                    Catalog.Add(item);
            }
            Log.Information("[PetAppearance] 在线目录加载: {Count} 个宠物", Catalog.Count);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetAppearance] 加载在线目录失败");
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    /// <summary>
    /// 安装宠物外观包
    /// </summary>
    [RelayCommand]
    public async Task InstallPetAsync(string petId)
    {
        if (string.IsNullOrEmpty(petId) || _installingPetIds.Contains(petId)) return;

        _installingPetIds.Add(petId);
        try
        {
            var petInfo = Catalog.FirstOrDefault(p => p.Id == petId);
            if (petInfo == null)
            {
                Log.Warning("[PetAppearance] 目录中找不到宠物: {Id}", petId);
                return;
            }

            var petsDir = System.IO.Path.Combine(App.DataDir, "pets", petId);
            System.IO.Directory.CreateDirectory(petsDir);

            // 下载精灵图
            if (!string.IsNullOrEmpty(petInfo.SpritesheetPath))
            {
                using var http = new System.Net.Http.HttpClient();
                var spriteData = await http.GetByteArrayAsync(petInfo.SpritesheetPath);
                var localPath = System.IO.Path.Combine(petsDir, "spritesheet.webp");
                await System.IO.File.WriteAllBytesAsync(localPath, spriteData);
            }

            // 写入 pet.json
            var json = JsonSerializer.Serialize(petInfo);
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(petsDir, "pet.json"), json);

            // 刷新已安装列表
            await LoadInstalledPetsAsync();
            Log.Information("[PetAppearance] 安装完成: {Id}", petId);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetAppearance] 安装宠物失败: {Id}", petId);
        }
        finally
        {
            _installingPetIds.Remove(petId);
        }
    }

    /// <summary>
    /// 卸载宠物外观包
    /// </summary>
    [RelayCommand]
    public Task UninstallPetAsync(string petId)
    {
        if (string.IsNullOrEmpty(petId)) return Task.CompletedTask;

        try
        {
            var petDir = System.IO.Path.Combine(App.DataDir, "pets", petId);

            if (System.IO.Directory.Exists(petDir))
            {
                System.IO.Directory.Delete(petDir, true);
            }

            // 如果卸载的是当前激活宠物 → 回退到默认
            if (ActivePetId == petId)
            {
                SetActivePet(DefaultPetId);
            }

            Log.Information("[PetAppearance] 卸载完成: {Id}", petId);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[PetAppearance] 卸载宠物失败: {Id}", petId);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取当前激活宠物的精灵图路径
    /// </summary>
    public string? GetActiveSpritesheetPath()
    {
        if (string.IsNullOrEmpty(ActivePetId)) return null;
        var petDir = System.IO.Path.Combine(App.DataDir, "pets", ActivePetId);
        // webp 优先（在线包），png 兜底（本地/种子包）
        var webp = System.IO.Path.Combine(petDir, "spritesheet.webp");
        if (System.IO.File.Exists(webp)) return webp;
        var png = System.IO.Path.Combine(petDir, "spritesheet.png");
        return System.IO.File.Exists(png) ? png : null;
    }
}
