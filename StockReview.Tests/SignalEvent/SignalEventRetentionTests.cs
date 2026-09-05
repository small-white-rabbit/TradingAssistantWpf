// SignalEventService 滚动窗口保留测试（2026-09-06）
// pet_signal_events 此前单条 JSON 只增不裁（生产实测 27MB/17 交易日），
// 启动全量反序列化 + 盘中全量重写是 LOH 碎片与托管堆大头。
// 约定：内存与持久化均只保留最近 N 个交易日期键（自进化窗口 5 日 + 余量）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockReview.Core.Data;
using StockReview.Core.Services;
using Xunit;

// 目录位于 SignalEvent/ 下但命名空间刻意用 Retention 后缀：
// 避免 StockReview.Tests.SignalEvent 与全局类型 SignalEvent 同名冲突
namespace StockReview.Tests.SignalEventRetention;

public class SignalEventRetentionTests : IDisposable
{
    private const string EventsKey = "pet_signal_events";
    private readonly string _dir;
    private readonly DatabaseService _db;

    public SignalEventRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sreview-signal-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService();
        _db.SetDataDir(_dir);
        _db.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响测试 */ }
    }

    /// <summary>构造 N 个连续交易日期键、每天 1 个事件的 JSON 并写入 appConfig</summary>
    private void SeedEvents(int days, DateTime? start = null)
    {
        var dict = new Dictionary<string, List<global::SignalEvent>>();
        var day = start ?? new DateTime(2026, 8, 1);
        for (var i = 0; i < days; i++)
        {
            var date = day.AddDays(i).ToString("yyyy-MM-dd");
            dict[date] = new List<global::SignalEvent>
            {
                new()
                {
                    Id = $"{date}_double_top",
                    StockCode = "600000",
                    StockName = "测试股",
                    SignalType = "double_top",
                    SignalLabel = "双顶",
                    Timestamp = 1
                }
            };
        }
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = EventsKey,
            ["value"] = JsonSerializer.Serialize(dict)
        });
    }

    private static string TodayKey()
    {
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CnTimeZone.Get);
        return shanghai.ToString("yyyy-MM-dd");
    }

    [Fact]
    public void Load_PrunesDaysBeyondRetentionWindow_AndPersistsToStorage()
    {
        // 10 个历史日期键：保留最近 7 个，最旧 3 个应被裁剪
        SeedEvents(10);

        var svc = new SignalEventService(_db);

        // 内存：最旧 3 天消失，第 4 天起保留
        Assert.Empty(svc.GetEventsByDate("2026-08-01"));
        Assert.Empty(svc.GetEventsByDate("2026-08-02"));
        Assert.Empty(svc.GetEventsByDate("2026-08-03"));
        Assert.Single(svc.GetEventsByDate("2026-08-04"));
        Assert.Single(svc.GetEventsByDate("2026-08-10"));

        // 持久化：重新加载（模拟重启）后裁剪结果仍在，证明已回写
        var reloaded = new SignalEventService(_db);
        Assert.Empty(reloaded.GetEventsByDate("2026-08-01"));
        Assert.Single(reloaded.GetEventsByDate("2026-08-10"));
    }

    [Fact]
    public void RecordEvent_EvictsOldestDayWhenWindowExceeded()
    {
        // 预置恰好 7 个历史日期键（加载后全部保留），今日新事件使键数超窗 → 最旧一天被挤出
        SeedEvents(7, new DateTime(2026, 8, 4));
        var svc = new SignalEventService(_db);
        Assert.Single(svc.GetEventsByDate("2026-08-04"));

        svc.RecordEvent(new SignalEventInput
        {
            StockCode = "600000",
            StockName = "测试股",
            SignalType = "double_top",
            SignalLabel = "双顶"
        });

        // 今日事件已记录，最旧一天被淘汰，窗口内其余日期不受影响
        Assert.Single(svc.GetTodayEvents());
        Assert.Empty(svc.GetEventsByDate("2026-08-04"));
        Assert.Single(svc.GetEventsByDate("2026-08-05"));
        Assert.Single(svc.GetEventsByDate("2026-08-10"));
        Assert.Single(svc.GetEventsByDate(TodayKey()));
    }
}
