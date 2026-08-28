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

    // access_token 缓存：百度 token 有效期 30 天，每次识别都重新获取会白增数百毫秒
    // 且高频调用易触发 token 接口限流（曾导致百度通道间歇性失败降级本地）。
    // 按密钥对缓存，过期前 1 天刷新；线程安全（多区域并发识别共享同一实例）。
    private static readonly System.Threading.SemaphoreSlim _tokenLock = new(1, 1);
    private static string _tokenCacheKey = "";
    private static string _tokenCacheValue = "";
    private static DateTime _tokenExpireAt = DateTime.MinValue;

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

            // 1. 获取 access_token（带缓存）
            var cacheKey = apiKey + "|" + secretKey;
            string accessToken;
            await _tokenLock.WaitAsync();
            try
            {
                if (_tokenCacheKey == cacheKey && DateTime.UtcNow < _tokenExpireAt)
                {
                    accessToken = _tokenCacheValue;
                }
                else
                {
                    var tokenUrl = $"https://aip.baidubce.com/oauth/2.0/token?grant_type=client_credentials&client_id={apiKey}&client_secret={secretKey}";
                    var tokenResp = await _httpClient.GetStringAsync(tokenUrl);
                    var tokenJson = JsonSerializer.Deserialize<JsonElement>(tokenResp);
                    if (!tokenJson.TryGetProperty("access_token", out var tokenEl))
                    {
                        // 百度失败时返回 {"error":"invalid_client","error_description":"..."}，带出具体原因便于定位密钥错误
                        var desc = tokenJson.TryGetProperty("error_description", out var ed) ? ed.GetString() : null;
                        var err = tokenJson.TryGetProperty("error", out var e) ? e.GetString() : null;
                        _tokenCacheKey = "";
                        return (false, null, $"获取 access_token 失败：{err ?? "未知错误"} {desc ?? ""}".Trim());
                    }
                    accessToken = tokenEl.GetString()!;
                    _tokenCacheKey = cacheKey;
                    _tokenCacheValue = accessToken;
                    _tokenExpireAt = DateTime.UtcNow.AddDays(29); // 官方有效期 30 天，提前 1 天刷新
                }
            }
            finally
            {
                _tokenLock.Release();
            }

            // 2. 调用 OCR API
            var ocrUrl = $"https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token={accessToken}";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("image", imageBase64)
            });

            var ocrResp = await _httpClient.PostAsync(ocrUrl, content);
            var ocrText = await ocrResp.Content.ReadAsStringAsync();
            var ocrJson = JsonSerializer.Deserialize<JsonElement>(ocrText);

            if (ocrJson.TryGetProperty("error_code", out var ec))
            {
                // 带出百度错误码+描述（如 17=每日额度用尽、110=token 过期），不再笼统报「OCR 识别失败」
                var em = ocrJson.TryGetProperty("error_msg", out var emEl) ? emEl.GetString() : null;
                // token 失效（110/111）时清除缓存，下次调用自动重新获取
                if (ec.GetInt32() is 110 or 111)
                {
                    _tokenCacheKey = "";
                }
                return (false, null, $"百度 OCR 错误 {ec.GetInt32()}: {em ?? "未知"}");
            }

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
