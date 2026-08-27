using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Serilog;
using StockReview.Core.Data;

namespace StockReviewWpf.Services;

/// <summary>
/// 心得定时提醒（对应 InsightReminderEnabled / InsightReminderInterval / InsightMinStars 设置）。
/// 按设定间隔轮询 insights 表，随机抽取一条达到最低星级的记录，通过宠物气泡展示。
/// 随 Host 启动，每分钟检查一次定时是否到点；开关/间隔在运行时从设置实时读取。
/// </summary>
public sealed class InsightReminderService : BackgroundService
{
    private readonly DatabaseService _db;
    // 走 IPetStore.AddReminder 统一管线（对应 Electron usePetStore().addReminder）：
    // 记录提醒历史 + 气泡优先级调度。直接调 PetService.ShowReminder 会绕过历史记录，
    // 导致"弹出的气泡没进提醒历史"。
    private readonly StockReview.Core.Services.IPetStore _petStore;

    public InsightReminderService(DatabaseService db, StockReview.Core.Services.IPetStore petStore)
    {
        _db = db;
        _petStore = petStore;
    }

    // 注意：不能用位置参数 record——SQLite 整数列是 Int64，record 构造器参数为 int 时
    // Dapper 按签名精确匹配失败（"A parameterless default constructor ... is required"），
    // 每次查询抛异常导致提醒永远不触发。改用无参构造 + 可写属性，Dapper 按列名映射。
    private sealed class InsightRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public long Importance { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastShown = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = PetSettingsStore.Load();
                if (settings.InsightReminderEnabled)
                {
                    var interval = TimeSpan.FromMinutes(Math.Max(1, settings.InsightReminderInterval));
                    if (lastShown == null ||
                        DateTime.Now - lastShown.Value >= interval)
                    {
                        if (TryShowRandomInsight(settings.InsightMinStars))
                        {
                            lastShown = DateTime.Now;
                        }
                    }
                }
                else
                {
                    lastShown = null;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[心得提醒] 定时提醒循环异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private bool TryShowRandomInsight(int minStars)
    {
        try
        {
            var rows = _db.Query<InsightRow>(
                "SELECT Id, Title, Content, Importance FROM insights WHERE importance >= @min ORDER BY createdAt DESC",
                new { min = minStars });
            var candidates = rows.Where(r => !string.IsNullOrWhiteSpace(r.Content)).ToList();
            if (candidates.Count == 0) return false;

            var pick = candidates[Random.Shared.Next(candidates.Count)];
            // 标题就是心得标题（气泡标题行），内容单独作为正文，不再拼接
            var title = pick.Title?.Trim();
            var body = ToReminderText(pick.Content);
            // 气泡 TextBlock 无 MaxHeight，长心得会把气泡撑到满屏——截断（对应 Electron 版 88 字摘要）
            if (body.Length > 200) body = body[..200] + "…";
            _petStore.AddReminder(new StockReview.Core.Services.ReminderRequest
            {
                Type = "insight",
                Level = StockReview.Core.Services.ReminderLevel.Info,
                Importance = (int)Math.Min(pick.Importance, 5),
                Title = title ?? "心得提醒",
                Content = body
            });
            Log.Information("[心得提醒] 已推送心得 #Id={Id}（★{Importance}）", pick.Id, pick.Importance);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[心得提醒] 查询/推送失败");
            return false;
        }
    }

    /// <summary>
    /// 心得正文 → 气泡纯文本：编辑器存的 HTML（&lt;p&gt;/&lt;br&gt; 等）不能直接展示，
    /// 块级标签与换行符转为换行、其余标签剥除、HTML 实体解码。
    /// </summary>
    private static string ToReminderText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
            return RichTextUtil.ToPlain(content);

        var text = Regex.Replace(content, "(?i)<br\\s*/?>|</(p|div|li|h[1-6]|blockquote)>", "\n");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = text.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">")
                   .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&");
        // 压缩 3 个以上连续换行为 1 个空行，去掉每行首尾空白
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return string.Join("\n", text.Split('\n').Select(l => l.Trim())).Trim();
    }
}