using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 百度 OCR 服务 - 对应 main.cjs 的 ocr:baidu
/// 调用百度 AI 平台通用文字识别 API
/// </summary>
public class OcrService
{
    private readonly HttpClient _httpClient;

    public OcrService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 百度 OCR 识别
    /// </summary>
    /// <param name="imageBase64">图片 base64（不含 data:image/... 前缀）</param>
    /// <param name="apiKey">百度 API Key</param>
    /// <param name="secretKey">百度 Secret Key</param>
    public async Task<(bool success, string? text, string? error)> RecognizeAsync(string imageBase64, string apiKey, string secretKey)
    {
        try
        {
            if (string.IsNullOrEmpty(imageBase64) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(secretKey))
                return (false, null, "缺少必要参数");

            // 去掉 data:image/...;base64, 前缀
            if (imageBase64.Contains(','))
                imageBase64 = imageBase64.Split(',')[1];

            // 1. 获取 access_token
            var tokenUrl = $"https://aip.baidubce.com/oauth/2.0/token?grant_type=client_credentials&client_id={apiKey}&client_secret={secretKey}";
            var tokenResp = await _httpClient.GetStringAsync(tokenUrl);
            var tokenJson = JsonSerializer.Deserialize<JsonElement>(tokenResp);
            if (!tokenJson.TryGetProperty("access_token", out var tokenEl))
                return (false, null, "获取 access_token 失败");
            var accessToken = tokenEl.GetString();

            // 2. 调用 OCR API
            var ocrUrl = $"https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token={accessToken}";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("image", imageBase64)
            });

            var ocrResp = await _httpClient.PostAsync(ocrUrl, content);
            var ocrText = await ocrResp.Content.ReadAsStringAsync();
            var ocrJson = JsonSerializer.Deserialize<JsonElement>(ocrText);

            if (ocrJson.TryGetProperty("error_code", out _))
                return (false, null, "OCR 识别失败");

            // 拼接识别结果
            var sb = new StringBuilder();
            if (ocrJson.TryGetProperty("words_result", out var wordsResult) && wordsResult.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in wordsResult.EnumerateArray())
                {
                    if (word.TryGetProperty("words", out var w))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(w.GetString());
                    }
                }
            }

            return (true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR 识别错误");
            return (false, null, ex.Message);
        }
    }
}
