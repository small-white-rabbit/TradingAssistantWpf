using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockReview.Mcp;

internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static Dictionary<string, object?> Project(
        Dictionary<string, object?> row, string[]? fields, int textLimit = 0)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kv in row)
        {
            if (kv.Key.StartsWith('_')) continue;
            if (fields != null && !fields.Contains(kv.Key)) continue;
            result[kv.Key] = textLimit > 0 ? TrimValue(kv.Value, textLimit) : kv.Value;
        }
        return result;
    }

    private static object? TrimValue(object? value, int maxLength)
    {
        if (value is string s && s.Length > maxLength)
            return s[..maxLength] + "…(截断)";
        return value;
    }
}
