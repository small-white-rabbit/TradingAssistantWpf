using System.Collections.Generic;
using System.Text.Json;
using StockReview.Core.Data;

namespace StockReviewWpf.Services;

/// <summary>
/// 写日记弹窗草稿存取（appConfig KV 表，key = diaryDraft）。
/// 日记需要边思考边写：编辑过程中防抖自动落草稿，关闭弹窗（取消/Esc/点遮罩）即存，
/// 重新打开（含应用重启后）恢复未保存内容；显式保存成功后清除。
/// 交易记录页与心得页两个写日记弹窗共用同一草稿槽。
/// </summary>
public static class DiaryDraftStore
{
    private const string Key = "diaryDraft";

    private sealed record Draft(string Type, string Date, string Title, string Content);

    public static void Save(IDatabaseService db, string type, string date, string title, string content)
    {
        db.Add("appConfig", new Dictionary<string, object?>
        {
            ["key"] = Key,
            ["value"] = JsonSerializer.Serialize(new Draft(type, date, title, content))
        });
    }

    /// <summary>读取草稿；无草稿或解析失败返回 false。</summary>
    public static bool TryLoad(IDatabaseService db, out string type, out string date, out string title, out string content)
    {
        type = "daily";
        date = "";
        title = "";
        content = "";
        try
        {
            var row = db.GetById("appConfig", Key);
            var val = row != null && row.TryGetValue("value", out var v) ? v?.ToString() : null;
            if (string.IsNullOrEmpty(val)) return false;
            var d = JsonSerializer.Deserialize<Draft>(val);
            if (d == null) return false;
            type = d.Type;
            date = d.Date;
            title = d.Title;
            content = d.Content;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Clear(IDatabaseService db)
    {
        db.Delete("appConfig", Key);
    }
}
