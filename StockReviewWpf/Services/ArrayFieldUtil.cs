using System.Collections;
using System.Text.Json;

namespace StockReviewWpf.Services;

/// <summary>
/// JSON 数组字段（problemTags / followUp 等）统一解析。
/// 数据层 DeserializeRecord 会把 JSON 数组列还原成 List&lt;object&gt;，
/// 历史数据还可能是 JSON 字符串或逗号分隔字符串——三种形态统一转 List&lt;string&gt;。
/// </summary>
public static class ArrayFieldUtil
{
    public static List<string> ToStringList(object? value)
    {
        if (value == null) return new List<string>();
        if (value is not string && value is IEnumerable list)
        {
            var result = new List<string>();
            foreach (var item in list)
            {
                var t = item?.ToString();
                if (!string.IsNullOrEmpty(t)) result.Add(t);
            }
            return result;
        }
        return ParseString(value.ToString() ?? "");
    }

    private static List<string> ParseString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        try
        {
            if (s.TrimStart().StartsWith("["))
            {
                var arr = JsonSerializer.Deserialize<List<string>>(s);
                if (arr != null) return arr;
            }
        }
        catch { /* 非 JSON，按逗号分隔处理 */ }
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
