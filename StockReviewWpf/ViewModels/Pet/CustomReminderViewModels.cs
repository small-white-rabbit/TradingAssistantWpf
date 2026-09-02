using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.MarketData;
using StockReviewWpf.Models;
using StockReviewWpf.Services;

namespace StockReviewWpf.ViewModels.Pet;

/// <summary>
/// 自定义提醒面板 ViewModel - 对应 CustomReminderPanel.vue + CustomReminderList.vue
/// </summary>
public partial class CustomReminderPanelViewModel : ObservableObject
{
    private readonly StockReview.Core.Services.CustomRemindersService? _reminderService;

    [ObservableProperty]
    private ObservableCollection<CustomReminder> _reminders = new();

    [ObservableProperty]
    private CustomReminder? _selectedReminder;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isDialogVisible;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private int _totalCount;

    /// <summary>15 行空白占位符</summary>
    public ObservableCollection<PlaceholderRow> PlaceholderItems { get; } = new();

    /// <summary>是否有数据</summary>
    public bool HasData => Reminders.Count > 0;

    public CustomReminderPanelViewModel() : this(null) { }

    public CustomReminderPanelViewModel(StockReview.Core.Services.CustomRemindersService? reminderService)
    {
        _reminderService = reminderService;
        for (int i = 0; i < 15; i++)
            PlaceholderItems.Add(new PlaceholderRow());
        LoadFromService();
        UpdateCounts();
    }

    /// <summary>从持久化服务加载提醒列表（对齐原版：按 createdAt 降序排列）</summary>
    public void LoadFromService()
    {
        Reminders.Clear();
        if (_reminderService != null)
        {
            foreach (var r in _reminderService.Reminders.OrderByDescending(r => DateTime.TryParse(r.CreatedAt, out var ct) ? ct : DateTime.MinValue))
            {
                Reminders.Add(new CustomReminder
                {
                    Id = r.Id,
                    Title = r.Title,
                    Type = r.Type,
                    Time = r.Time,
                    Date = r.Date,
                    Weekdays = r.Weekdays != null
                        ? new ObservableCollection<int>(r.Weekdays)
                        : new ObservableCollection<int> { 1, 2, 3, 4, 5 },
                    StockCode = r.StockCode,
                    StockName = r.StockName,
                    Content = r.Content,
                    Enabled = r.Enabled,
                    RepeatBurstCount = r.RepeatBurstCount ?? 1,
                    Status = r.Status,
                    CreatedAt = DateTime.TryParse(r.CreatedAt, out var ct) ? ct : DateTime.Now,
                    Actions = r.Actions
                });
            }
        }
        UpdateCounts();
    }

    /// <summary>将 WPF 模型转为 Core 模型并持久化</summary>
    public void SaveToService()
    {
        if (_reminderService == null) return;
        var coreList = new List<StockReview.Core.Services.CustomReminder>();
        foreach (var r in Reminders)
        {
            coreList.Add(new StockReview.Core.Services.CustomReminder
            {
                Id = r.Id,
                Title = r.Title,
                Type = r.Type,
                Time = r.Time,
                Date = r.Date,
                Weekdays = r.Weekdays?.ToList(),
                StockCode = r.StockCode,
                StockName = r.StockName,
                Content = r.Content,
                Enabled = r.Enabled,
                RepeatBurstCount = r.RepeatBurstCount,
                Status = r.Status,
                Actions = r.Actions,
                CreatedAt = r.CreatedAt.ToString("o"),
                UpdatedAt = DateTime.Now.ToString("o")
            });
        }
        _reminderService.ReplaceAll(coreList);
    }

    [RelayCommand]
    private void ShowAddDialog()
    {
        SelectedReminder = null;
        IsDialogVisible = true;
    }

    [RelayCommand]
    private void ShowEditDialog(CustomReminder reminder)
    {
        SelectedReminder = reminder;
        IsDialogVisible = true;
    }

    [RelayCommand]
    private void CloseDialog()
    {
        IsDialogVisible = false;
        SelectedReminder = null;
    }

    [RelayCommand]
    private void ToggleEnabled(CustomReminder reminder)
    {
        reminder.Enabled = !reminder.Enabled;
        UpdateCounts();
        SaveToService();
    }

    [RelayCommand]
    private void DeleteReminder(CustomReminder reminder)
    {
        Reminders.Remove(reminder);
        UpdateCounts();
        SaveToService();
    }

    [RelayCommand]
    private void SaveReminder(CustomReminder reminder)
    {
        if (!Reminders.Contains(reminder))
            Reminders.Add(reminder);
        IsDialogVisible = false;
        SelectedReminder = null;
        UpdateCounts();
        SaveToService();
    }

    public void UpdateCounts()
    {
        TotalCount = Reminders.Count;
        EnabledCount = 0;
        foreach (var r in Reminders)
            if (r.Enabled) EnabledCount++;
        OnPropertyChanged(nameof(HasData));
        UpdatePlaceholderCount(Reminders.Count);
    }

    /// <summary>数据行不足 15 行时用空白占位补齐（el-table 固定高度观感）</summary>
    public void UpdatePlaceholderCount(int shownRows)
    {
        var need = Math.Max(0, 15 - shownRows);
        while (PlaceholderItems.Count > need) PlaceholderItems.RemoveAt(PlaceholderItems.Count - 1);
        while (PlaceholderItems.Count < need) PlaceholderItems.Add(new PlaceholderRow());
    }
}

/// <summary>
/// 自定义提醒对话框 ViewModel - 对应 CustomReminderDialog.vue
/// </summary>
public partial class CustomReminderDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _type = "once";

    [ObservableProperty]
    private string _time = "09:00";

    [ObservableProperty]
    private string? _date;

    [ObservableProperty]
    private ObservableCollection<int> _weekdays = new() { 1, 2, 3, 4, 5 };

    [ObservableProperty]
    private string? _stockCode;

    [ObservableProperty]
    private string? _stockName;

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private int _repeatBurstCount = 1;

    // ===== 动作按钮勾选（原版 selectedActionTypes，默认全选 DEFAULT_ACTIONS） =====
    [ObservableProperty]
    private bool _hasDoneAction = true;

    [ObservableProperty]
    private bool _hasSnoozeAction = true;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private ObservableCollection<WeekdayOption> _weekdayOptions = new()
    {
        new() { Value = 1, Label = "周一" },
        new() { Value = 2, Label = "周二" },
        new() { Value = 3, Label = "周三" },
        new() { Value = 4, Label = "周四" },
        new() { Value = 5, Label = "周五" },
        new() { Value = 6, Label = "周六" },
        new() { Value = 0, Label = "周日" },
    };

    [ObservableProperty]
    private ObservableCollection<TypeOption> _typeOptions = new()
    {
        new() { Value = "once", Label = "单次" },
        new() { Value = "daily", Label = "每日" },
        new() { Value = "weekly", Label = "每周" },
    };

    public CustomReminderDialogViewModel()
    {
        Date = DateTime.Now.ToString("yyyy-MM-dd");
    }

    [RelayCommand]
    private void ToggleWeekday(int day)
    {
        if (Weekdays.Contains(day))
            Weekdays.Remove(day);
        else
            Weekdays.Add(day);
    }

    [RelayCommand]
    private void FetchStockInfo()
    {
        // 根据股票代码获取名称
        if (!string.IsNullOrEmpty(StockCode) && StockCode.Length == 6)
        {
            StockName = "示例股票";
        }
    }

    public CustomReminder BuildReminder()
    {
        // 原版：actions = DEFAULT_ACTIONS.filter(a => selectedActionTypes.includes(a.type))
        var actions = new List<StockReview.Core.Services.ReminderAction>();
        if (HasDoneAction)
            actions.Add(new StockReview.Core.Services.ReminderAction { Type = "custom_done", Label = "完成" });
        if (HasSnoozeAction)
            actions.Add(new StockReview.Core.Services.ReminderAction { Type = "custom_snooze", Label = "稍后提醒" });

        return new CustomReminder
        {
            Title = Title,
            Type = Type,
            Time = Time,
            Date = Date,
            Weekdays = new ObservableCollection<int>(Weekdays),
            StockCode = StockCode,
            StockName = StockName,
            Content = Content,
            RepeatBurstCount = RepeatBurstCount,
            Enabled = true,
            Actions = actions.Count > 0 ? actions : null
        };
    }

    public void LoadFromReminder(CustomReminder reminder)
    {
        IsEditing = true;
        Title = reminder.Title;
        Type = reminder.Type;
        Time = reminder.Time;
        Date = reminder.Date;
        Weekdays = new ObservableCollection<int>(reminder.Weekdays);
        StockCode = reminder.StockCode;
        StockName = reminder.StockName;
        Content = reminder.Content;
        RepeatBurstCount = reminder.RepeatBurstCount;
        // 原版：(reminder.actions || DEFAULT_ACTIONS).map(a => a.type) → 默认全选
        var types = (reminder.Actions ?? StockReview.Core.Services.CustomRemindersService.DefaultActions)
            .Select(a => a.Type).ToList();
        HasDoneAction = types.Contains("custom_done");
        HasSnoozeAction = types.Contains("custom_snooze");
    }
}

/// <summary>
/// 星期选项
/// </summary>
public class WeekdayOption
{
    public int Value { get; set; }
    public string Label { get; set; } = "";
    public bool IsSelected { get; set; }
}

/// <summary>
/// 类型选项
/// </summary>
public class TypeOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>
/// 添加计划对话框 ViewModel - 对应 AddPlanDialog.vue
/// （真实进场类型分组 + 行情自动回填 + 原版 tradePlanStore.validatePlan 校验规则）
/// </summary>
public partial class AddPlanDialogViewModel : ObservableObject
{
    private readonly StockReview.Core.Data.IDatabaseService? _db;
    private readonly StockOcrService? _ocr;
    private readonly MarketDataAggregator? _market;
    private readonly System.Windows.Threading.DispatcherTimer _autoFetchTimer;

    [ObservableProperty] private string _stockCode = "";
    [ObservableProperty] private string _stockName = "";
    [ObservableProperty] private string _planType = "sell";
    [ObservableProperty] private DateTime? _planDateValue = DateTime.Today;
    [ObservableProperty] private string _entryReason = "";
    [ObservableProperty] private decimal? _entryPrice;
    [ObservableProperty] private decimal? _targetPrice;
    [ObservableProperty] private decimal? _stopLoss;

    // 价格输入以字符串承载（对齐原版 input v-model）：
    // decimal 直接绑定 + UpdateSourceTrigger=PropertyChanged 时，输入 "12." 等
    // 中间态转换失败，绑定把旧 VM 值推回文本框，小数点被立即"吃掉"。
    // 解析成功才推进 decimal 值；中间态保留旧值等待补全。
    [ObservableProperty] private string? _entryPriceText;
    [ObservableProperty] private string? _targetPriceText;
    [ObservableProperty] private string? _stopLossText;
    [ObservableProperty] private int _maxHoldDays = 3;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _priceLoading;
    [ObservableProperty] private ObservableCollection<EntryTypeItem> _entryTypeGroups = new();
    [ObservableProperty] private string? _validationError;

    public string PlanDate => PlanDateValue?.ToString("yyyy-MM-dd") ?? "";

    /// <summary>保存计划按钮可用（原版 :disabled="!validation.valid"）</summary>
    public bool IsValid => ValidationError == null;

    /// <summary>目标空间/止损空间文本（原版 targetPct/stopPct，红涨绿跌）</summary>
    public string? TargetPctText => PctText(TargetPrice);
    public string? StopPctText => PctText(StopLoss);
    public string TargetPctColor => PctColor(TargetPrice);
    public string StopPctColor => PctColor(StopLoss);

    public AddPlanDialogViewModel()
    {
        var sp = App.Host?.Services;
        _db = sp?.GetService<StockReview.Core.Data.DatabaseService>();
        _ocr = sp?.GetService<StockOcrService>();
        _market = sp?.GetService<MarketDataAggregator>();

        _autoFetchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _autoFetchTimer.Tick += (_, _) => { _autoFetchTimer.Stop(); _ = FetchStockInfoAsync(); };

        _ = LoadEntryTypesAsync();
        RecomputeValidation();
    }

    // === 原版 watch(form.stockCode)：6位代码 300ms 防抖自动获取名称+现价 ===

    partial void OnStockCodeChanged(string value)
    {
        if (value is { Length: 6 } && value.All(char.IsDigit))
        {
            _autoFetchTimer.Stop();
            _autoFetchTimer.Start();
        }
        else
        {
            _autoFetchTimer.Stop();
        }
        RecomputeValidation();
    }

    private async Task FetchStockInfoAsync()
    {
        var code = StockCode.Trim();
        if (code.Length != 6 || _ocr == null) return;
        PriceLoading = true;
        try
        {
            if (_market != null)
            {
                var data = await StockMarketService.Fetch(_ocr, _market, code, DateTime.Today.ToString("yyyy-MM-dd"));
                if (data != null)
                {
                    if (!string.IsNullOrEmpty(data.Name)) StockName = data.Name;
                    if (!string.IsNullOrEmpty(data.Close) && decimal.TryParse(data.Close, out var close) && EntryPrice is null or 0)
                    {
                        EntryPrice = close;
                        EntryPriceText = close.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            if (string.IsNullOrEmpty(StockName))
            {
                var name = await Task.Run(() => _ocr.GetNameByCode(code));
                if (!string.IsNullOrEmpty(name)) StockName = name;
            }
        }
        catch { }
        finally { PriceLoading = false; }
    }

    // === 原版 loadEntryTypes：entryTypes 表 active 项按父子分组，组内按 sortOrder ===
    // 树构建统一走 Services.EntryTypeTree（与交易录入表单/设置管理共用同一形态）

    private async Task LoadEntryTypesAsync()
    {
        if (_db == null) return;
        try
        {
            var rows = await Task.Run(() => _db.GetAll("entryTypes"));
            var nodes = rows
                .Select(r => new EntryTypeItem
                {
                    Id = ToInt(r, "id"),
                    SortOrder = ToInt(r, "sortOrder"),
                    Name = ToStr(r, "typeName"),
                    IsActive = ToInt(r, "isActive") != 0,
                    ParentId = ToIntOrNull(r, "parentId"),
                })
                .Where(n => n.Id > 0 && n.IsActive)
                .ToList();

            var roots = Services.EntryTypeTree.Build(nodes);
            EntryTypeGroups = new ObservableCollection<EntryTypeItem>(roots);
            Serilog.Log.Information("[AddPlan] 进场理由分组加载完成: {Groups} 组", roots.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AddPlan] 进场理由加载失败");
        }
    }

    /// <summary>进场理由分组单选（模板内 RadioButton 的 Command）</summary>
    [RelayCommand]
    private void SelectEntryReason(string reason) => EntryReason = reason;

    // === 原版 validatePlan + el-form 规则（逐条对齐文案） ===

    partial void OnStockNameChanged(string value) => RecomputeValidation();
    partial void OnPlanTypeChanged(string value) => RecomputeValidation();
    partial void OnEntryReasonChanged(string value) => RecomputeValidation();
    partial void OnPlanDateValueChanged(DateTime? value) => OnPropertyChanged(nameof(PlanDate));

    partial void OnEntryPriceChanged(decimal? value)
    {
        RecomputeValidation();
        RaisePctChanged();
    }

    partial void OnTargetPriceChanged(decimal? value)
    {
        RecomputeValidation();
        RaisePctChanged();
    }

    partial void OnStopLossChanged(decimal? value)
    {
        RecomputeValidation();
        RaisePctChanged();
    }

    // === 文本 → decimal：解析成功推进值；中间态（"12."）保留旧值等待补全 ===

    partial void OnEntryPriceTextChanged(string? value)
    {
        if (ParsePrice(value, out var v)) EntryPrice = v;
        else if (string.IsNullOrWhiteSpace(value)) EntryPrice = null;
        RecomputeValidation();
    }

    partial void OnTargetPriceTextChanged(string? value)
    {
        if (ParsePrice(value, out var v)) TargetPrice = v;
        else if (string.IsNullOrWhiteSpace(value)) TargetPrice = null;
        RecomputeValidation();
    }

    partial void OnStopLossTextChanged(string? value)
    {
        if (ParsePrice(value, out var v)) StopLoss = v;
        else if (string.IsNullOrWhiteSpace(value)) StopLoss = null;
        RecomputeValidation();
    }

    private static bool ParsePrice(string? text, out decimal value)
        => decimal.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Number,
             System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0;

    private void RecomputeValidation()
    {
        string? err = null;
        if (string.IsNullOrWhiteSpace(StockCode)) err = "股票代码必填";
        else if (string.IsNullOrWhiteSpace(StockName)) err = "请输入股票名称";
        else if (string.IsNullOrWhiteSpace(EntryReason)) err = "进场理由必填";
        else if (EntryPrice is null || EntryPrice <= 0) err = "进场价位必填且必须大于 0";
        else if (TargetPrice is null or 0) err = "目标价位必填（必须先制定离场计划）";
        else if (StopLoss is null or 0) err = "止损价位必填（必须先制定离场计划）";
        else if (PlanType is "buy" or "sell")
        {
            var label = PlanType == "buy" ? "买入" : "卖出";
            if (TargetPrice.Value <= EntryPrice.Value) err = $"{label}计划的目标价应高于进场价";
            else if (StopLoss.Value >= EntryPrice.Value) err = $"{label}计划的止损价应低于进场价";
        }
        ValidationError = err;
        OnPropertyChanged(nameof(IsValid));
    }

    private void RaisePctChanged()
    {
        OnPropertyChanged(nameof(TargetPctText));
        OnPropertyChanged(nameof(TargetPctColor));
        OnPropertyChanged(nameof(StopPctText));
        OnPropertyChanged(nameof(StopPctColor));
    }

    private string? PctText(decimal? price)
    {
        if (EntryPrice is not > 0 || price is null) return null;
        var pct = (double)((price.Value - EntryPrice.Value) / EntryPrice.Value * 100);
        return (pct >= 0 ? "+" : "") + pct.ToString("F2") + "%";
    }

    private string PctColor(decimal? price) => EntryPrice is > 0 && price >= EntryPrice ? "#F56C6C" : "#67C23A";

    public TradePlan BuildPlan() => new()
    {
        StockCode = StockCode.Trim(),
        StockName = StockName,
        PlanType = PlanType,
        PlanDate = PlanDate,
        EntryReason = EntryReason,
        EntryPrice = EntryPrice,
        TargetPrice = TargetPrice,
        StopLoss = StopLoss,
        MaxHoldDays = MaxHoldDays,
        Note = Note,
        Status = "pending",
    };

    // === 行字典取值（entryTypes 行 → 强类型字段） ===

    // 注意：DeserializeRecord 会把 is* 字段的 0/1 转成 bool，必须先处理 bool 分支
    private static int ToInt(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) switch
        {
            true when v is bool b => b ? 1 : 0,
            true when v != null && int.TryParse(v.ToString(), out var i) => i,
            _ => 0
        };

    private static int? ToIntOrNull(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : null;

    private static string ToStr(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";
}
