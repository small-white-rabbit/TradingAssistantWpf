using StockReview.Core.Services;

namespace StockReviewWpf.Services;

/// <summary>
/// 调度器设置读取桥（IPetSettingsStore 真实实现，对应 petSettingsStore）。
/// 按版本号缓存：调度器每秒多次读取不再反复读盘（读文件风暴），
/// 设置保存后版本号变化立即失效 → 盘中开关依然实时生效。
/// </summary>
public class SchedulerPetSettingsStore : IPetSettingsStore
{
    private StockReview.Core.Services.PetSettings? _cached;
    private long _cachedVersion = -1;

    public StockReview.Core.Services.PetSettings Settings
    {
        get
        {
            if (_cached != null && _cachedVersion == PetSettingsStore.Version)
            {
                return _cached;
            }

            var s = PetSettingsStore.Load();
            _cached = new StockReview.Core.Services.PetSettings
            {
                SellPointDetection = s.SellPointDetection,
                KeyLevelDetection = s.KeyLevelDetection,
                SurgePullbackThreshold = (decimal)s.SurgePullbackThreshold,
                VolumeAmplifyMultiple = (decimal)s.VolumeAmplifyMultiple,
                SupportBreakdownTolerance = (decimal)s.SupportBreakdownTolerance,
                PriceNearThreshold = (decimal)s.PriceNearThreshold,
                AfterMarketReminderInterval = s.AfterMarketReminderInterval,
                RefreshIntervalMs = s.RefreshInterval,
                CustomRemindersEnabled = true, // WPF 无私自定义提醒开关，默认开启
                PreCloseMA5Check = s.PreCloseMA5Check // 尾盘 MA5 检查：设置面板开关实时生效
            };
            _cachedVersion = PetSettingsStore.Version;
            return _cached;
        }
    }
}