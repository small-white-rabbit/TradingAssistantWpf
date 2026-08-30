using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReviewWpf.Models;
using StockReviewWpf.Services;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 设置视图 ViewModel - 对应 SettingsView.vue
/// 进场类型 / 问题标签 来自 SQLite（entryTypes / problemTags 表），
/// 归类规则为阈值配置（与 Electron 一致，默认 5% / -3%）。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private bool _loadingWebDav;

    [ObservableProperty]
    private string _activeTab = "entryTypes";

    [ObservableProperty]
    private ObservableCollection<EntryTypeItem> _entryTypes = new();

    [ObservableProperty]
    private ObservableCollection<ProblemTagItem> _problemTags = new();

    [ObservableProperty]
    private ObservableCollection<CategoryRule> _categoryRules = new();

    [ObservableProperty]
    private EntryTypeItem? _selectedEntryType;

    [ObservableProperty]
    private ProblemTagItem? _selectedProblemTag;

    [ObservableProperty]
    private string _newEntryTypeName = "";

    [ObservableProperty]
    private string _newEntryTypeDescription = "";

    [ObservableProperty]
    private int _newEntryTypeParentId;

    [ObservableProperty]
    private string _newEntryTypeSortOrder = "";

    [ObservableProperty]
    private ObservableCollection<EntryTypeItem> _entryTypeParents = new();

    [ObservableProperty]
    private string _newProblemTagName = "";

    [ObservableProperty]
    private string _newProblemTagDescription = "";

    [ObservableProperty]
    private string _newProblemTagSortOrder = "";

    // 云端同步 (WebDAV)
    [ObservableProperty]
    private string _webDavServerUrl = "";

    [ObservableProperty]
    private string _webDavUsername = "";

    [ObservableProperty]
    private string _webDavPassword = "";

    [ObservableProperty]
    private string _webDavRemotePath = "/StockReviewSync/";

    [ObservableProperty]
    private bool _autoSyncEnabled;

    [ObservableProperty]
    private bool _cloudBusy;

    /// <summary>上传按钮文案（对齐原版 TextBlock DataTrigger）</summary>
    public string CloudUploadButtonText => CloudBusy ? "上传中..." : "上传备份到云端";

    partial void OnCloudBusyChanged(bool value) => OnPropertyChanged(nameof(CloudUploadButtonText));

    [ObservableProperty]
    private string _cloudMessage = "";

    [ObservableProperty]
    private bool _cloudMessageIsError;

    [ObservableProperty]
    private ObservableCollection<WebDavFileInfo> _cloudFiles = new();

    // 进场类型对话框
    [ObservableProperty]
    private bool _isEntryTypeDialogVisible;

    [ObservableProperty]
    private bool _isProblemTagDialogVisible;

    [ObservableProperty]
    private bool _isEditingEntryType;

    [ObservableProperty]
    private bool _isEditingProblemTag;

    // 归类规则阈值（对应 Electron successThreshold / failThreshold，默认 5 / -3）
    [ObservableProperty]
    private string _successThreshold = "5";

    [ObservableProperty]
    private string _failThreshold = "-3";

    // ============ 百度OCR 配置（appConfig['ocrConfig']，对应原版 OCR Tab） ============
    [ObservableProperty]
    private string _ocrApiKey = "";

    [ObservableProperty]
    private string _ocrSecretKey = "";

    // ============ 显示设置（appConfig['displayConfig']，对应原版 显示设置 Tab） ============
    [ObservableProperty]
    private string _insightListStyle = "card"; // card / grid / timeline / paper / magazine / compact

    [ObservableProperty]
    private string _insightPaperScrollMode = "vertical"; // vertical / horizontal（心得 B5 纸张滚动）

    [ObservableProperty]
    private string _diaryStyle = "card"; // card / split / timeline / dark

    [ObservableProperty]
    private string _diaryListStyle = "card"; // card / timeline / grid / bubble / paper

    [ObservableProperty]
    private string _paperScrollMode = "vertical"; // vertical / horizontal（日记列表 B5 纸张滚动）

    // ============ 关于系统 ============
    [ObservableProperty]
    private string _appVersion = $"v{App.AppVersion}";

    // ============ 数据管理（对应原版 数据管理 Tab） ============
    [ObservableProperty]
    private string _dataDirPath = "";

    [ObservableProperty]
    private string _dbFilePath = "";

    [ObservableProperty]
    private string _imagesDirPath = "";

    [ObservableProperty]
    private string _backupsDirPath = "";

    [ObservableProperty]
    private string _screenshotStatsText = "0 个文件 · 0 B";

    [ObservableProperty]
    private string _dataManageFeedback = "";

    // ============ 桌面宠物（对应原版 桌面宠物 Tab） ============
    [ObservableProperty]
    private bool _petRunning;

    [ObservableProperty]
    private bool _petAutoStart;

    [ObservableProperty]
    private double _petSize = 1.0;

    [ObservableProperty]
    private double _petOpacity = 1.0;

    public SettingsViewModel() : this(
        App.Host?.Services.GetRequiredService<DatabaseService>())
    {
    }

    public SettingsViewModel(DatabaseService? db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        // 异步加载：避免同步 GetAll + 多次 GetById 阻塞 UI 线程
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        // 后台线程执行所有 DB 查询，结果回 UI 线程更新集合
        var (entryRows, tagRows, ruleCfg, ocrCfg, wdCfg, dispCfg) = await Task.Run(() =>
        {
            var er = _db.GetAll("entryTypes");
            var tr = _db.GetAll("problemTags");
            var rc = _db.GetById("appConfig", "caseRules");
            var oc = _db.GetById("appConfig", "ocrConfig");
            var wd = _db.GetById("appConfig", "webdavConfig");
            var dc = _db.GetById("appConfig", "displayConfig");
            return (er, tr, rc, oc, wd, dc);
        });

        // ===== 以下在 UI 线程执行（ObservableCollection 更新必须 UI 线程）=====
        EntryTypes.Clear();
        var allTypes = entryRows.Select(MapEntryType).OrderBy(t => t.SortOrder).ToList();
        // 树形平铺：父级后紧跟其子级（对齐原版 el-table 树形展开）
        foreach (var parent in allTypes.Where(t => !t.ParentId.HasValue))
        {
            EntryTypes.Add(parent);
            foreach (var child in allTypes.Where(t => t.ParentId == parent.Id))
            {
                parent.Children.Add(child);
                EntryTypes.Add(child);
            }
        }
        // 父级已被删的孤立子级兜底展示
        foreach (var orphan in allTypes.Where(t => t.ParentId.HasValue && allTypes.All(p => p.Id != t.ParentId)))
            EntryTypes.Add(orphan);
        // 父级候选：仅无 parentId 的类型（可作为父级）
        EntryTypeParents.Clear();
        foreach (var et in EntryTypes.Where(e => !e.ParentId.HasValue))
            EntryTypeParents.Add(et);

        ProblemTags.Clear();
        foreach (var row in tagRows.OrderBy(r => AsInt(r, "sortOrder")))
        {
            ProblemTags.Add(MapProblemTag(row));
        }

        // 归类规则阈值（从 appConfig 读取 caseRules JSON，缺省 5 / -3，对齐 Electron）
        SuccessThreshold = "5";
        FailThreshold = "-3";
        if (ruleCfg != null && ruleCfg.TryGetValue("value", out var rv) && rv != null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rv.ToString()!);
                var root = doc.RootElement;
                if (root.TryGetProperty("successThreshold", out var st) && st.TryGetDecimal(out var sDec))
                    SuccessThreshold = sDec.ToString();
                if (root.TryGetProperty("failThreshold", out var ft) && ft.TryGetDecimal(out var fDec))
                    FailThreshold = fDec.ToString();
            }
            catch { /* 忽略损坏的归类规则配置 */ }
        }

        // 百度OCR 配置（appConfig['ocrConfig']）
        OcrApiKey = "";
        OcrSecretKey = "";
        // ocrCfg 已在后台预加载
        if (ocrCfg != null && ocrCfg.TryGetValue("value", out var ov) && ov != null)
        {
            try
            {
                using var odoc = System.Text.Json.JsonDocument.Parse(ov.ToString()!);
                if (odoc.RootElement.TryGetProperty("apiKey", out var ak)) OcrApiKey = ak.GetString() ?? "";
                if (odoc.RootElement.TryGetProperty("secretKey", out var sk)) OcrSecretKey = sk.GetString() ?? "";
            }
            catch { }
        }

        // WebDAV 云同步配置（appConfig['webdavConfig']，对齐原版 localStorage + appConfig 双写）
        _loadingWebDav = true;
        WebDavServerUrl = "";
        WebDavUsername = "";
        WebDavPassword = "";
        WebDavRemotePath = "/StockReviewSync/";
        AutoSyncEnabled = false;
        // wdCfg 已在后台预加载
        if (wdCfg != null && wdCfg.TryGetValue("value", out var wv) && wv != null)
        {
            try
            {
                using var wdoc = System.Text.Json.JsonDocument.Parse(wv.ToString()!);
                var wr = wdoc.RootElement;
                if (wr.TryGetProperty("serverUrl", out var su)) WebDavServerUrl = su.GetString() ?? "";
                if (wr.TryGetProperty("username", out var un)) WebDavUsername = un.GetString() ?? "";
                if (wr.TryGetProperty("password", out var pw)) WebDavPassword = CredentialProtector.Unprotect(pw.GetString()) ?? "";
                if (wr.TryGetProperty("remotePath", out var rp) && rp.GetString() is { Length: > 0 } remotePath)
                    WebDavRemotePath = remotePath;
                if (wr.TryGetProperty("autoSync", out var ae)) AutoSyncEnabled = ae.GetBoolean();
            }
            catch { }
        }
        _loadingWebDav = false;

        // 显示设置（appConfig['displayConfig']）
        // 加载期间禁止回写：先重置再读取的每次赋值都会触发 WriteDisplayConfig，
        // 不加守卫会用默认值覆盖刚导入的配置
        _loadingDisplay = true;
        InsightListStyle = "card";
        InsightPaperScrollMode = "vertical";
        DiaryStyle = "card";
        DiaryListStyle = "card";
        PaperScrollMode = "vertical";
        // dispCfg 已在后台预加载
        if (dispCfg != null && dispCfg.TryGetValue("value", out var dv) && dv != null)
        {
            try
            {
                using var ddoc = System.Text.Json.JsonDocument.Parse(dv.ToString()!);
                var r = ddoc.RootElement;
                if (r.TryGetProperty("insightListStyle", out var ils)) InsightListStyle = ils.GetString() ?? "card";
                if (r.TryGetProperty("insightPaperScrollMode", out var ipsm)) InsightPaperScrollMode = ipsm.GetString() ?? "vertical";
                if (r.TryGetProperty("diaryStyle", out var ds)) DiaryStyle = ds.GetString() ?? "card";
                if (r.TryGetProperty("diaryListStyle", out var dls)) DiaryListStyle = dls.GetString() ?? "card";
                if (r.TryGetProperty("paperScrollMode", out var psm)) PaperScrollMode = psm.GetString() ?? "vertical";
            }
            catch { }
        }
        _loadingDisplay = false;

        // 数据管理：目录信息 + 截图统计
        LoadDataDirInfo();
        RefreshScreenshotStats();

        // 桌面宠物设置（_loadingPet 防止加载期间回写注册表/文件）
        _loadingPet = true;
        var pet = PetSettingsStore.Load();
        PetSize = pet.PetSize;
        PetOpacity = pet.PetOpacity;
        PetAutoStart = GetAutoStartRegistry();
        _loadingPet = false;
        PetRunning = App.Host?.Services.GetRequiredService<PetWindowManager>().IsPetVisible ?? false;
    }

    private void LoadDataDirInfo()
    {
        DataDirPath = App.DataDir;
        DbFilePath = System.IO.Path.Combine(App.DataDir, "data.db");
        ImagesDirPath = System.IO.Path.Combine(App.DataDir, "images");
        BackupsDirPath = System.IO.Path.Combine(App.DataDir, "backups");
    }

    private static string FormatFileSize(long bytes)
        => bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):F2} GB"
         : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F2} MB"
         : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KB"
         : $"{bytes} B";

    private void RefreshScreenshotStats()
    {
        try
        {
            var imagesDir = System.IO.Path.Combine(App.DataDir, "images");
            if (!System.IO.Directory.Exists(imagesDir))
            {
                ScreenshotStatsText = "0 个文件 · 0 B";
                return;
            }
            var files = System.IO.Directory.GetFiles(imagesDir, "*", System.IO.SearchOption.AllDirectories)
                .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(System.IO.Path.GetExtension(f).ToLower()))
                .ToList();
            var size = files.Sum(f => new System.IO.FileInfo(f).Length);
            ScreenshotStatsText = $"{files.Count} 个文件 · {FormatFileSize(size)}";
        }
        catch
        {
            ScreenshotStatsText = "统计失败";
        }
    }

    // 显示设置在改动时立即持久化（对齐原版 @change="saveXxx"）；加载期间跳过
    private bool _loadingDisplay;

    partial void OnInsightListStyleChanged(string value) { if (!_loadingDisplay) WriteDisplayConfig(); }
    partial void OnInsightPaperScrollModeChanged(string value) { if (!_loadingDisplay) WriteDisplayConfig(); }
    partial void OnDiaryStyleChanged(string value) { if (!_loadingDisplay) WriteDisplayConfig(); }
    partial void OnDiaryListStyleChanged(string value) { if (!_loadingDisplay) WriteDisplayConfig(); }
    partial void OnPaperScrollModeChanged(string value) { if (!_loadingDisplay) WriteDisplayConfig(); }

    private void WriteDisplayConfig()
    {
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = "displayConfig",
            ["value"] = System.Text.Json.JsonSerializer.Serialize(new
            {
                insightListStyle = InsightListStyle,
                insightPaperScrollMode = InsightPaperScrollMode,
                diaryStyle = DiaryStyle,
                diaryListStyle = DiaryListStyle,
                paperScrollMode = PaperScrollMode
            })
        });
    }

    private static EntryTypeItem MapEntryType(Dictionary<string, object?> row)
    {
        return new EntryTypeItem
        {
            Id = AsInt(row, "id"),
            SortOrder = AsInt(row, "sortOrder"),
            Name = AsString(row, "typeName"),
            Description = AsString(row, "description"),
            Color = AsString(row, "color"),
            IsStrongType = AsBool(row, "isStrongType"),
            IsActive = AsBool(row, "isActive", true),
            ParentId = row.TryGetValue("parentId", out var pid) && pid != null ? AsInt(row, "parentId") : (int?)null
        };
    }

    private static ProblemTagItem MapProblemTag(Dictionary<string, object?> row)
    {
        return new ProblemTagItem
        {
            Id = AsInt(row, "id"),
            SortOrder = AsInt(row, "sortOrder"),
            Name = AsString(row, "tagName"),
            Description = AsString(row, "description"),
            Color = AsString(row, "color"),
            IsActive = AsBool(row, "isActive", true)
        };
    }

    // ============ 百度OCR 配置 ============

    [RelayCommand]
    private void SaveOcrConfig()
    {
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = "ocrConfig",
            ["value"] = System.Text.Json.JsonSerializer.Serialize(new
            {
                apiKey = OcrApiKey.Trim(),
                secretKey = OcrSecretKey.Trim()
            })
        });
        System.Windows.MessageBox.Show("百度OCR配置已保存。WPF 版默认使用内置的离线识别，配置将供云端识别备用。",
            "保存成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ClearOcrConfig()
    {
        OcrApiKey = "";
        OcrSecretKey = "";
        _db.Delete("appConfig", "ocrConfig");
    }

    // ============ 数据管理 ============

    /// <summary>更改数据存储位置：写 data-dir.json，重启后生效（对齐原版 selectDataDir）。</summary>
    [RelayCommand]
    private void SelectDataDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择数据存储位置",
            InitialDirectory = App.DataDir
        };
        if (dlg.ShowDialog() != true) return;

        // 安装版的安装目录会随升级被整体替换，禁止选为数据目录（否则升级即丢数据）
        if (App.IsVelopackInstalled && dlg.FolderName.TrimEnd(System.IO.Path.DirectorySeparatorChar)
            .StartsWith(System.IO.Path.TrimEndingDirectorySeparator(App.AppBaseDir)
                + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                "不能选择应用安装目录内部作为数据存储位置：升级应用时该目录会被整体替换，数据会丢失。\n请选择安装目录以外的位置。",
                "更改存储位置", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 指针文件写到外置数据根（安装版 %LocalAppData%\StockReviewWpf），不再写在会随升级被替换的安装目录里
            var configPath = App.DataDirConfigPath;
            System.IO.File.WriteAllText(configPath,
                System.Text.Json.JsonSerializer.Serialize(new { dataDir = dlg.FolderName }));
            System.Windows.MessageBox.Show(
                $"数据目录已更改为：\n{dlg.FolderName}\n\n重启应用后生效。",
                "更改存储位置", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("更改失败: " + ex.Message, "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>清除全部数据：二次确认后清空核心业务表（对应原版 clearAllData）。</summary>
    [RelayCommand]
    private void ClearAllData()
    {
        var first = System.Windows.MessageBox.Show(
            "确定要清除全部数据吗？\n该操作将删除所有交易记录、强股、擒牛、心得、案例数据，且不可恢复！",
            "危险操作", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (first != System.Windows.MessageBoxResult.OK) return;
        var second = System.Windows.MessageBox.Show(
            "再次确认：真的要清除全部数据吗？建议先导出备份！",
            "最终确认", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Stop);
        if (second != System.Windows.MessageBoxResult.OK) return;

        foreach (var table in new[] { "trades", "strongStocks", "dailyPicks", "insights", "patternCases", "dailySummaries" })
            _db.Execute($"DELETE FROM {table}");
        DataManageFeedback = "全部数据已清除";
    }

    /// <summary>清理未被数据库引用的截图文件（对应原版 cleanupOrphanedScreenshots）。</summary>
    [RelayCommand]
    private void CleanupOrphanedScreenshots()
    {
        try
        {
            var referenced = new HashSet<string>();
            foreach (var (table, column) in new[]
                     {
                         ("trades", "screenshot"), ("strongStocks", "screenshot"),
                         ("dailyPicks", "screenshot"), ("patternCases", "screenshot"), ("insights", "screenshot")
                     })
            {
                foreach (var row in _db.GetAll(table))
                    if (row.TryGetValue(column, out var p) && p != null && !string.IsNullOrEmpty(p.ToString()))
                        referenced.Add(p.ToString()!);
            }
            var img = App.Host?.Services.GetRequiredService<ImageService>();
            var deleted = img?.CleanupOrphaned(referenced) ?? 0;
            RefreshScreenshotStats();
            DataManageFeedback = $"清理完成：删除 {deleted} 个未引用截图";
        }
        catch (Exception ex)
        {
            DataManageFeedback = "清理失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RefreshScreenshotStatsCommand() => RefreshScreenshotStats();

    // ============ 桌面宠物 ============

    [RelayCommand]
    private void TogglePet()
    {
        var manager = App.Host?.Services.GetRequiredService<PetWindowManager>();
        if (manager == null) return;
        manager.TogglePet();
        PetRunning = manager.IsPetVisible;
        // 持久化开关状态：重启后按此决定是否随主程序显示宠物
        var pet = PetSettingsStore.Load();
        pet.Enabled = PetRunning;
        PetSettingsStore.Save(pet);
    }

    partial void OnPetSizeChanged(double value) => SavePetSetting();
    partial void OnPetOpacityChanged(double value) => SavePetSetting();

    private void SavePetSetting()
    {
        if (_loadingPet) return;
        var pet = PetSettingsStore.Load();
        pet.PetSize = PetSize;
        pet.PetOpacity = PetOpacity;
        PetSettingsStore.Save(pet);
        // 滑块改动即时作用于已打开的宠物窗口
        App.Host?.Services.GetRequiredService<PetWindowManager>().ApplySettings();
    }

    private bool _loadingPet;

    private static bool GetAutoStartRegistry()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("StockReviewWpf") != null;
    }

    private static void SetAutoStartRegistry(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key == null) return;
        if (enabled)
            key.SetValue("StockReviewWpf", Environment.ProcessPath ?? "");
        else
            key.DeleteValue("StockReviewWpf", throwOnMissingValue: false);
    }

    partial void OnPetAutoStartChanged(bool value)
    {
        if (_loadingPet) return;
        SetAutoStartRegistry(value);
    }

    [RelayCommand]
    private void ResetPetSettings()
    {
        _loadingPet = true;
        try
        {
            PetSize = 1.0;
            PetOpacity = 1.0;
            var pet = PetSettingsStore.Load();
            pet.PetSize = 1.0;
            pet.PetOpacity = 1.0;
            PetSettingsStore.Save(pet);
            App.Host?.Services.GetRequiredService<PetWindowManager>().ApplySettings();
        }
        finally
        {
            _loadingPet = false;
        }
        System.Windows.MessageBox.Show("宠物设置已恢复默认。", "恢复默认",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ShowPetHelp()
    {
        System.Windows.MessageBox.Show(
            "宠物操作：拖拽移动 / 单击查看提醒 / 双击打开计划列表 / 右键功能菜单\n" +
            "添加计划：右键菜单 → 添加计划；记录执行：计划列表中点击记录\n" +
            "提醒规则：目标价接近 1% 内提醒；涨跌幅超阈值提醒；分时卖点识别；盘后循环提醒\n" +
            "违规记录：未执行计划会被记录；每月累计 3 次宠物生病；连续 7 天无违规恢复",
            "桌面宠物使用帮助", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    // ============ Tab 切换 ============

    [RelayCommand]
    private void SwitchTab(string tab)
    {
        ActiveTab = tab;
    }

    // ============ 进场类型 ============

    [RelayCommand]
    private void ShowAddEntryTypeDialog()
    {
        IsEditingEntryType = false;
        SelectedEntryType = null;
        NewEntryTypeName = "";
        NewEntryTypeDescription = "";
        NewEntryTypeParentId = 0;
        NewEntryTypeSortOrder = "";
        IsEntryTypeDialogVisible = true;
    }

    [RelayCommand]
    private void ShowEditEntryTypeDialog(EntryTypeItem item)
    {
        IsEditingEntryType = true;
        SelectedEntryType = item;
        NewEntryTypeName = item.Name;
        NewEntryTypeDescription = item.Description;
        NewEntryTypeParentId = item.ParentId ?? 0;
        NewEntryTypeSortOrder = item.SortOrder.ToString();
        IsEntryTypeDialogVisible = true;
    }

    [RelayCommand]
    private void SaveEntryType()
    {
        var name = NewEntryTypeName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            IsEntryTypeDialogVisible = false;
            return;
        }

        var now = DateTime.UtcNow.ToString("o");
        var parentId = NewEntryTypeParentId > 0 ? NewEntryTypeParentId : (int?)null;

        // 自动排序：用户手动输入优先，否则按规则
        // 1. 不选父级 → 父级类型，排序 = 现有父级数 + 1
        // 2. 选了父级 → 子级，排序 = 该父级下现有子级数 + 1
        int sortOrder;
        if (int.TryParse(NewEntryTypeSortOrder.Trim(), out var manualSo) && manualSo > 0)
        {
            sortOrder = manualSo;
        }
        else if (parentId.HasValue)
        {
            // 子级：该父级下现有子级数 + 1
            sortOrder = EntryTypes.Count(e => e.ParentId == parentId.Value) + 1;
        }
        else
        {
            // 父级：现有父级数 + 1
            sortOrder = EntryTypes.Count(e => !e.ParentId.HasValue) + 1;
        }

        if (IsEditingEntryType && SelectedEntryType != null && SelectedEntryType.Id > 0)
        {
            var data = new Dictionary<string, object?>
            {
                ["typeName"] = name,
                ["description"] = NewEntryTypeDescription,
                ["sortOrder"] = sortOrder,
                ["parentId"] = parentId,
                ["updatedAt"] = now
            };
            _db.Update("entryTypes", SelectedEntryType.Id, data);
        }
        else
        {
            var data = new Dictionary<string, object?>
            {
                ["typeName"] = name,
                ["description"] = NewEntryTypeDescription,
                ["color"] = "#409EFF",
                ["sortOrder"] = sortOrder,
                ["isActive"] = 1,
                ["parentId"] = parentId,
                ["standardForm"] = "",
                ["notes"] = "",
                ["reflections"] = "",
                ["typeImage"] = "",
                ["standardFormImage"] = ""
            };
            _db.Add("entryTypes", data);
        }

        _ = LoadDataAsync();
        IsEntryTypeDialogVisible = false;
    }

    [RelayCommand]
    private void DeleteEntryType(EntryTypeItem item)
    {
        if (item == null) return;
        var result = System.Windows.MessageBox.Show(
            $"确定要删除进场类型「{item.Name}」吗？\n该操作不可撤销。",
            "确认删除", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.OK) return;
        if (item.Id > 0) _db.Delete("entryTypes", item.Id);
        EntryTypes.Remove(item);
    }

    /// <summary>启用开关切换即保存（对齐原版 el-switch @change="updateType"）</summary>
    [RelayCommand]
    private void ToggleEntryTypeActive(EntryTypeItem item)
    {
        if (item is { Id: > 0 })
            _db.Update("entryTypes", item.Id, new Dictionary<string, object?> { ["isActive"] = item.IsActive ? 1 : 0 });
    }

    /// <summary>问题标签启用开关切换即保存（对齐原版 updateTag）</summary>
    [RelayCommand]
    private void ToggleProblemTagActive(ProblemTagItem item)
    {
        if (item is { Id: > 0 })
            _db.Update("problemTags", item.Id, new Dictionary<string, object?> { ["isActive"] = item.IsActive ? 1 : 0 });
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEntryTypeDialogVisible = false;
        IsProblemTagDialogVisible = false;
    }

    // ============ 问题标签 ============

    [RelayCommand]
    private void ShowAddProblemTagDialog()
    {
        IsEditingProblemTag = false;
        SelectedProblemTag = null;
        NewProblemTagName = "";
        NewProblemTagDescription = "";
        NewProblemTagSortOrder = "";
        IsProblemTagDialogVisible = true;
    }

    [RelayCommand]
    private void ShowEditProblemTagDialog(ProblemTagItem item)
    {
        IsEditingProblemTag = true;
        SelectedProblemTag = item;
        NewProblemTagName = item.Name;
        NewProblemTagDescription = item.Description;
        NewProblemTagSortOrder = item.SortOrder.ToString();
        IsProblemTagDialogVisible = true;
    }

    [RelayCommand]
    private void SaveProblemTag()
    {
        var name = NewProblemTagName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            IsProblemTagDialogVisible = false;
            return;
        }

        var now = DateTime.UtcNow.ToString("o");
        // 自动排序：用户手动输入优先，否则 = 现有标签数 + 1
        var sortOrder = int.TryParse(NewProblemTagSortOrder.Trim(), out var manualSo) && manualSo > 0
            ? manualSo
            : ProblemTags.Count + 1;
        if (IsEditingProblemTag && SelectedProblemTag != null && SelectedProblemTag.Id > 0)
        {
            var data = new Dictionary<string, object?>
            {
                ["tagName"] = name,
                ["description"] = NewProblemTagDescription,
                ["sortOrder"] = sortOrder,
                ["updatedAt"] = now
            };
            _db.Update("problemTags", SelectedProblemTag.Id, data);
        }
        else
        {
            var data = new Dictionary<string, object?>
            {
                ["tagName"] = name,
                ["description"] = NewProblemTagDescription,
                ["color"] = "#F56C6C",
                ["sortOrder"] = sortOrder,
                ["isActive"] = 1
            };
            _db.Add("problemTags", data);
        }

        _ = LoadDataAsync();
        IsProblemTagDialogVisible = false;
    }

    [RelayCommand]
    private void DeleteProblemTag(ProblemTagItem item)
    {
        if (item == null) return;
        var result = System.Windows.MessageBox.Show(
            $"确定要删除问题标签「{item.Name}」吗？\n该操作不可撤销。",
            "确认删除", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.OK) return;
        if (item.Id > 0) _db.Delete("problemTags", item.Id);
        ProblemTags.Remove(item);
    }

    // ============ 归类规则 ============

    [RelayCommand]
    private void SaveRules()
    {
        // 与 Electron 保持一致：整包存为 appConfig['caseRules']
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = "caseRules",
            ["value"] = System.Text.Json.JsonSerializer.Serialize(new
            {
                successThreshold = SuccessThreshold,
                failThreshold = FailThreshold
            })
        });
    }

    [RelayCommand]
    private void ReclassifyAll()
    {
        // 对应 Electron reclassifyAll：按阈值重算 trades 的 caseType
        if (!int.TryParse(SuccessThreshold, out var success)) success = 5;
        if (!int.TryParse(FailThreshold, out var fail) || fail > 0) fail = -3;

        var trades = _db.GetAll("trades");
        foreach (var t in trades)
        {
            if (!t.TryGetValue("positionStatus", out var ps) || ps?.ToString() != "已清仓") continue;
            var ret = AsDouble(t, "totalReturn");
            string caseType;
            if (ret >= success) caseType = "成功案例";
            else if (ret <= fail) caseType = "失败案例";
            else caseType = "未归类";
            _db.Update("trades", AsInt(t, "id"), new Dictionary<string, object?>
            {
                ["caseType"] = caseType,
                ["updatedAt"] = DateTime.UtcNow.ToString("o")
            });
        }
    }

    // ============ 云端同步 (WebDAV) - 对应原版 SettingsView.vue 云端同步区 ============

    private CloudSyncService Cloud =>
        App.Host?.Services.GetRequiredService<CloudSyncService>()
        ?? throw new InvalidOperationException("CloudSyncService 未初始化");

    private (string server, string user, string pass, string path) CurrentWebDavConfig()
    {
        var path = WebDavRemotePath.Trim();
        if (path.Length == 0) path = "/StockReviewSync/";
        return (WebDavServerUrl.Trim().TrimEnd('/'), WebDavUsername.Trim(), WebDavPassword, path);
    }

    private void WriteWebDavConfig()
    {
        var (_, _, _, path) = CurrentWebDavConfig();
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = "webdavConfig",
            ["value"] = System.Text.Json.JsonSerializer.Serialize(new
            {
                serverUrl = WebDavServerUrl.Trim(),
                username = WebDavUsername.Trim(),
                password = CredentialProtector.Protect(WebDavPassword),
                remotePath = path,
                autoSync = AutoSyncEnabled
            })
        });
    }

    [RelayCommand]
    private void SaveWebDavConfig()
    {
        WriteWebDavConfig();
        CloudMessage = "WebDAV 配置已保存";
        CloudMessageIsError = false;
    }

    /// <summary>自动同步开关切换即持久化（对齐原版 toggleAutoSync）</summary>
    partial void OnAutoSyncEnabledChanged(bool value)
    {
        if (_loadingWebDav) return;
        WriteWebDavConfig();
    }

    [RelayCommand]
    private async Task TestWebDavConnection()
    {
        if (CloudBusy) return;
        var (server, user, pass, _) = CurrentWebDavConfig();
        if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
        {
            CloudMessage = "请填写完整的 WebDAV 配置（服务器地址、用户名、密码）";
            CloudMessageIsError = true;
            return;
        }
        CloudBusy = true;
        CloudMessage = "正在测试连接...";
        try
        {
            var result = await Cloud.TestConnectionAsync(server, user, pass);
            CloudMessage = result.success ? "连接成功！服务器可访问" : result.message;
            CloudMessageIsError = !result.success;
        }
        finally
        {
            CloudBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloudUpload()
    {
        if (CloudBusy) return;
        var (server, user, pass, path) = CurrentWebDavConfig();
        if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
        {
            CloudMessage = "请填写完整的 WebDAV 配置";
            CloudMessageIsError = true;
            return;
        }
        CloudBusy = true;
        CloudMessage = "正在打包并上传备份...";
        CloudMessageIsError = false;
        try
        {
            var result = await Cloud.UploadAsync(server, user, pass, path);
            CloudMessage = result.message;
            CloudMessageIsError = !result.success;
            if (result.success) await LoadCloudFiles(server, user, pass, path);
        }
        finally
        {
            CloudBusy = false;
        }
    }

    /// <summary>从云端恢复最新一份备份（对齐原版 cloudDownload）</summary>
    [RelayCommand]
    private Task CloudRestoreLatest()
    {
        if (CloudFiles.Count == 0)
        {
            CloudMessage = "没有可恢复的云端备份，请先刷新云端文件列表";
            CloudMessageIsError = true;
            return Task.CompletedTask;
        }
        return CloudRestoreFile(CloudFiles[0].Name);
    }

    [RelayCommand]
    private async Task CloudRestoreFile(string? fileName)
    {
        if (CloudBusy || string.IsNullOrEmpty(fileName)) return;
        var confirmed = System.Windows.MessageBox.Show(
            $"确定要从云端恢复备份 \"{fileName}\" 吗？\n\n恢复采用智能合并：已有数据按 ID 更新，新数据自动添加。",
            "恢复云端备份", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirmed != System.Windows.MessageBoxResult.OK) return;

        var (server, user, pass, path) = CurrentWebDavConfig();
        CloudBusy = true;
        CloudMessage = "正在下载并恢复备份...";
        CloudMessageIsError = false;
        try
        {
            var result = await Cloud.DownloadAsync(server, user, pass, path, fileName);
            CloudMessage = result.message;
            CloudMessageIsError = !result.success;
            if (result.success)
            {
                _ = LoadDataAsync();
                ReloadRuntimeStores();
            }
        }
        finally
        {
            CloudBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloudDeleteFile(string? fileName)
    {
        if (CloudBusy || string.IsNullOrEmpty(fileName)) return;
        var confirmed = System.Windows.MessageBox.Show(
            $"确定要删除云端备份 \"{fileName}\" 吗？此操作不可恢复。",
            "删除云端备份", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirmed != System.Windows.MessageBoxResult.OK) return;

        var (server, user, pass, path) = CurrentWebDavConfig();
        CloudBusy = true;
        try
        {
            var result = await Cloud.DeleteAsync(server, user, pass, path, fileName);
            CloudMessage = result.message;
            CloudMessageIsError = !result.success;
            if (result.success) await LoadCloudFiles(server, user, pass, path);
        }
        finally
        {
            CloudBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCloudFiles()
    {
        if (CloudBusy) return;
        var (server, user, pass, path) = CurrentWebDavConfig();
        if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
        {
            CloudMessage = "请填写完整的 WebDAV 配置";
            CloudMessageIsError = true;
            return;
        }
        await LoadCloudFiles(server, user, pass, path);
    }

    private async Task LoadCloudFiles(string server, string user, string pass, string path)
    {
        CloudBusy = true;
        CloudMessage = "正在获取云端备份列表...";
        CloudMessageIsError = false;
        try
        {
            var result = await Cloud.ListAsync(server, user, pass, path);
            if (result.success && result.files != null)
            {
                CloudFiles.Clear();
                foreach (var f in result.files) CloudFiles.Add(f);
                CloudMessage = result.message ?? "";
            }
            else
            {
                CloudFiles.Clear();
                CloudMessage = result.message ?? "列出文件失败";
                CloudMessageIsError = true;
            }
        }
        finally
        {
            CloudBusy = false;
        }
    }

    // ============ 数据备份 / 恢复 ============
    // 对齐原版 Electron：ZIP 打包（data.json + images/ 截图），导入支持 ZIP/JSON 智能合并

    private BackupService Backup =>
        App.Host?.Services.GetRequiredService<BackupService>()
        ?? throw new InvalidOperationException("BackupService 未初始化");

    /// <summary>
    /// 导入/恢复后重载持有内存态的单例服务（对齐 Electron 写回 localStorage 后刷新各 store）。
    /// 数据已落库，任一服务重载失败不影响其余（重启后按库内数据加载）。
    /// </summary>
    private static void ReloadRuntimeStores()
    {
        var host = App.Host;
        if (host == null) return;
        foreach (var reload in new Action[]
        {
            () => host.Services.GetRequiredService<StockReview.Core.Services.TradePlanService>().ReloadFromStorage(),
            () => host.Services.GetRequiredService<StockReview.Core.Services.CustomRemindersService>().ReloadFromStorage(),
            () => host.Services.GetRequiredService<StockReview.Core.Services.ReminderHistoryService>().ReloadFromStorage(),
            () => host.Services.GetRequiredService<StockReview.Core.Services.SignalEventService>().ReloadFromStorage(),
            () => host.Services.GetRequiredService<PetWindowManager>().ApplySettings()
        })
        {
            try { reload(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[导入] 服务重载失败: {ex.Message}"); }
        }
    }

    [RelayCommand]
    private async Task ExportData()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ZIP 备份文件|*.zip",
            Title = "导出数据",
            FileName = $"stock-review-backup-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var result = await Backup.ExportZipAsync(dlg.FileName);
            if (result.Success)
            {
                System.Windows.MessageBox.Show(result.Message + $"\n\n保存至：{result.FilePath}", "导出备份",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("导出失败: " + result.Message, "导出备份",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导出失败: " + ex.Message, "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportData()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "备份文件|*.zip;*.json",
            Title = "导入数据备份（智能合并）"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var result = dlg.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? await Backup.ImportZipAsync(dlg.FileName)
                : Backup.ImportJsonFile(dlg.FileName);
            if (result.Success)
            {
                System.Windows.MessageBox.Show(result.Message, "导入备份",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                _ = LoadDataAsync();
                ReloadRuntimeStores();
            }
            else
            {
                System.Windows.MessageBox.Show("导入失败: " + result.Message, "导入备份",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导入失败: " + ex.Message, "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RestoreSnapshot()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "数据库快照|*.db",
            Title = "从本地快照恢复（整库覆盖）"
        };
        var backupsDir = System.IO.Path.Combine(App.DataDir, "backups");
        if (System.IO.Directory.Exists(backupsDir)) dlg.InitialDirectory = backupsDir;
        if (dlg.ShowDialog() != true) return;

        var confirm = System.Windows.MessageBox.Show(
            "将用所选快照完整覆盖当前数据库，与 ZIP 导入的智能合并不同，此操作为整库替换。\n\n" +
            "恢复前会自动把当前数据留存为一份 -pre-restore 快照，是否继续？",
            "从快照恢复", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            var safetyPath = _db.RestoreFromSnapshot(dlg.FileName);
            System.Windows.MessageBox.Show(
                "恢复成功！\n\n恢复前的数据已自动保存至：" + safetyPath, "从快照恢复",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            _ = LoadDataAsync();
            ReloadRuntimeStores();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("恢复失败: " + ex.Message, "从快照恢复",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // ============ 辅助解析 ============

    private static int AsInt(Dictionary<string, object?> row, string key, int def = 0)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is int i) return i;
            if (int.TryParse(v.ToString(), out var r)) return r;
        }
        return def;
    }

    private static double AsDouble(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is double d) return d;
            if (v is int i) return i;
            if (double.TryParse(v.ToString(), out var r)) return r;
        }
        return 0;
    }

    private static string AsString(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var v) && v != null ? v.ToString()! : "";
    }

    private static bool AsBool(Dictionary<string, object?> row, string key, bool def = false)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is bool b) return b;
            if (v is int i) return i == 1;
        }
        return def;
    }
}

/// <summary>
/// 分类规则（UI 占位，阈值存放在 appConfig）
/// </summary>
public class CategoryRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
