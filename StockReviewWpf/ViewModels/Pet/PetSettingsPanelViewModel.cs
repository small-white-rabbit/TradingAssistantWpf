using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReviewWpf.Models;
using StockReviewWpf.Services;

namespace StockReviewWpf.ViewModels.Pet;

/// <summary>
/// 宠物设置面板 ViewModel - 对应 PetSettingsPanel.vue（600px 弹窗 + 4 tab）
/// </summary>
public partial class PetSettingsPanelViewModel : ObservableObject
{
    // 设置被保存后触发，供宠物窗口按新设置生效（点穿透/拖拽等）
    public event Action? SettingsSaved;

    [ObservableProperty]
    private string _activeTab = "source";

    [ObservableProperty]
    private PetSettings _settings = new();

    // 数据源选项
    [ObservableProperty]
    private ObservableCollection<SourceOption> _sourceOptions = new();

    // 阈值滑块辅助
    [ObservableProperty]
    private double _priceChangeSlider = 3.0;

    [ObservableProperty]
    private double _priceNearSlider = 1.0;

    [ObservableProperty]
    private double _surgePullbackSlider = 2.0;

    [ObservableProperty]
    private double _volumeAmplifySlider = 2.0;

    [ObservableProperty]
    private double _supportBreakdownSlider = 1.0;

    [ObservableProperty]
    private bool _isTestingLatency;

    [ObservableProperty]
    private string _saveStatus = "";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

    private readonly AutoStartService _autoStartService = new();

    public PetSettingsPanelViewModel()
    {
        // 载入已持久化的设置（若文件不存在则用默认值）
        Settings = PetSettingsStore.Load();
        ApplySettingsToSliders();
        LoadSourceOptions();
        // 同步注册表中真实的开机自启状态（避免与持久化文件不一致）
        Settings.AutoStart = _autoStartService.GetAutoStart().openAtLogin;
    }

    private void ApplySettingsToSliders()
    {
        PriceChangeSlider = Settings.PriceChangeThreshold;
        PriceNearSlider = Settings.PriceNearThreshold;
        SurgePullbackSlider = Settings.SurgePullbackThreshold;
        VolumeAmplifySlider = Settings.VolumeAmplifyMultiple;
        SupportBreakdownSlider = Settings.SupportBreakdownTolerance;
    }

    private void LoadSourceOptions()
    {
        SourceOptions.Clear();
        SourceOptions.Add(new SourceOption { Key = "futu", Label = "富途 OpenD", Tag = "订阅制" });
        SourceOptions.Add(new SourceOption { Key = "eastmoney", Label = "东方财富", Tag = "推荐" });
        SourceOptions.Add(new SourceOption { Key = "tencent", Label = "腾讯财经", Tag = "轮询" });
        SyncSourceSelection();
        foreach (var s in SourceOptions)
            s.IsUsing = s.Key == Settings.PrimarySource;
    }

    private void SyncSourceSelection()
    {
        foreach (var s in SourceOptions)
            s.IsSelected = s.Key == Settings.PrimarySource;
    }

    [RelayCommand]
    private void SwitchTab(string tab)
    {
        ActiveTab = tab;
    }

    [RelayCommand]
    private void SelectSource(string key)
    {
        Settings.PrimarySource = key;
        SyncSourceSelection();
    }

    /// <summary>一键测试各数据源延迟（对齐原版：并发探测，结果回显到卡片）。</summary>
    [RelayCommand]
    private async Task TestLatencyAsync()
    {
        if (IsTestingLatency) return;
        IsTestingLatency = true;
        foreach (var s in SourceOptions)
        {
            s.LatencyText = "测试中…";
            s.LatencyState = "muted";
        }
        try
        {
            await Task.WhenAll(SourceOptions.Select(async s =>
            {
                var sw = Stopwatch.StartNew();
                var ok = s.Key switch
                {
                    "futu" => await TcpProbeAsync(Settings.FutuHost, Settings.FutuPort),
                    "tencent" => await HttpProbeAsync("https://qt.gtimg.cn/q=sh000001"),
                    _ => await HttpProbeAsync("https://push2.eastmoney.com/api/qt/ulist.np/get?secids=1.000001&fields=f2")
                };
                sw.Stop();
                s.LatencyText = ok ? $"{sw.ElapsedMilliseconds}ms" : "连接失败";
                // 分级：futu 探端口（回环应 <50ms）；http 源 <300 good / <1500 mid
                s.LatencyState = !ok ? "bad"
                    : s.Key == "futu"
                        ? (sw.ElapsedMilliseconds < 50 ? "good" : sw.ElapsedMilliseconds < 200 ? "mid" : "bad")
                        : (sw.ElapsedMilliseconds < 300 ? "good" : sw.ElapsedMilliseconds < 1500 ? "mid" : "bad");
            }));
        }
        finally
        {
            IsTestingLatency = false;
        }
    }

    private static async Task<bool> TcpProbeAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HttpProbeAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // sina 接口要求带 Referer，否则 451
            req.Headers.Referrer = new Uri("https://finance.eastmoney.com");
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void BrowseOpenDPath()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择 OpenD 可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            Settings.FutuOpenDPath = dialog.FileName;
            // PetSettings 是 POCO（无属性变更通知），手动触发 Settings 变更让路径输入框立即回显
            OnPropertyChanged(nameof(Settings));
        }
    }

    /// <summary>取消：丢弃未保存的修改，重新载入已持久化设置。</summary>
    [RelayCommand]
    private void Cancel()
    {
        Settings = PetSettingsStore.Load();
        ApplySettingsToSliders();
        SyncSourceSelection();
        SaveStatus = "";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SaveStatus = "保存中...";
        // 点选富途时自动启用富途数据源（对齐原版 handleSave 联动）
        if (Settings.PrimarySource == "futu") Settings.FutuEnabled = true;
        PetSettingsStore.Save(Settings);
        // 开机自启同步到注册表
        _autoStartService.SetAutoStart(Settings.AutoStart);
        SettingsSaved?.Invoke();
        // 保存后"使用中"标记跟随新选择
        foreach (var s in SourceOptions)
            s.IsUsing = s.Key == Settings.PrimarySource;
        SaveStatus = "设置已保存";
    }

    [RelayCommand]
    private void ResetSettings()
    {
        Settings = new PetSettings();
        ApplySettingsToSliders();
        SyncSourceSelection();
        PetSettingsStore.Save(Settings);
        SettingsSaved?.Invoke();
        SaveStatus = "已恢复默认";
    }

    partial void OnPriceChangeSliderChanged(double value)
    {
        Settings.PriceChangeThreshold = value;
    }

    partial void OnPriceNearSliderChanged(double value)
    {
        Settings.PriceNearThreshold = value;
    }

    partial void OnSurgePullbackSliderChanged(double value)
    {
        Settings.SurgePullbackThreshold = value;
    }

    partial void OnVolumeAmplifySliderChanged(double value)
    {
        Settings.VolumeAmplifyMultiple = value;
    }

    partial void OnSupportBreakdownSliderChanged(double value)
    {
        Settings.SupportBreakdownTolerance = value;
    }
}

/// <summary>
/// 数据源选项
/// </summary>
public partial class SourceOption : ObservableObject
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Tag { get; set; } = "";

    /// <summary>是否为当前点选的主数据源（卡片高亮）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否为当前实际使用的数据源（"使用中"标记）。</summary>
    [ObservableProperty]
    private bool _isUsing;

    /// <summary>延迟测试结果文案。</summary>
    [ObservableProperty]
    private string _latencyText = "未测试";

    /// <summary>延迟分级（good/mid/bad/muted，驱动颜色）。</summary>
    [ObservableProperty]
    private string _latencyState = "muted";
}
