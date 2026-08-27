using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// HTTP 代理服务 - 对应 main.cjs 的 http:fetch / http:nodeFetch / http:browserFetch
/// 使用 HttpClient 带浏览器 UA 和东财专用请求头
/// </summary>
public class HttpProxyService
{
    private readonly HttpClient _httpClient;

    // 模拟 Chrome 浏览器 UA（规避东财等数据源的 UA 检测）
    private const string BrowserUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public HttpProxyService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUA);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// 通用 HTTP GET 请求（带浏览器 UA）
    /// 对应 http:fetch / http:nodeFetch
    /// </summary>
    public async Task<(bool success, string? data, int statusCode, string? error)> FetchAsync(string url, Dictionary<string, string>? headers = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            // 默认请求头
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            req.Headers.TryAddWithoutValidation("Connection", "keep-alive");

            // 东财专用：补全所有浏览器安全上下文头
            if (url.Contains("eastmoney.com"))
            {
                req.Headers.TryAddWithoutValidation("Referer", "https://quote.eastmoney.com/");
                req.Headers.TryAddWithoutValidation("Origin", "https://quote.eastmoney.com");
                req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
                req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
                req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
                req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not/A)Brand\";v=\"8\", \"Chromium\";v=\"126\", \"Google Chrome\";v=\"126\"");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            }

            // 调用方 headers 覆盖默认值
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }

            var resp = await _httpClient.SendAsync(req);
            var data = await resp.Content.ReadAsStringAsync();
            return (true, data, (int)resp.StatusCode, null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, 0, "HTTP 请求超时");
        }
        catch (Exception ex)
        {
            return (false, null, 0, ex.Message);
        }
    }

    /// <summary>
    /// 获取原始字节数据（用于 GBK 编码响应）
    /// </summary>
    public async Task<(bool success, byte[]? data, int statusCode, string? error)> FetchBytesAsync(string url, Dictionary<string, string>? headers = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Connection", "keep-alive");

            if (url.Contains("eastmoney.com"))
            {
                req.Headers.TryAddWithoutValidation("Referer", "https://quote.eastmoney.com/");
                req.Headers.TryAddWithoutValidation("Origin", "https://quote.eastmoney.com");
                req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
                req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
                req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
                req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not/A)Brand\";v=\"8\", \"Chromium\";v=\"126\", \"Google Chrome\";v=\"126\"");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            }

            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }

            var resp = await _httpClient.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (true, bytes, (int)resp.StatusCode, null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, 0, "HTTP 请求超时");
        }
        catch (Exception ex)
        {
            return (false, null, 0, ex.Message);
        }
    }

    /// <summary>
    /// GBK 编解码的文本请求（腾讯行情等）
    /// </summary>
    public async Task<(bool success, string? data, int statusCode, string? error)> FetchGbkAsync(string url, Dictionary<string, string>? headers = null)
    {
        var (success, bytes, statusCode, error) = await FetchBytesAsync(url, headers);
        if (!success || bytes == null) return (false, null, statusCode, error);
        try
        {
            // 尝试 GBK 解码
            var gbk = Encoding.GetEncoding("GBK");
            return (true, gbk.GetString(bytes), statusCode, null);
        }
        catch
        {
            // 回退 UTF-8
            return (true, Encoding.UTF8.GetString(bytes), statusCode, null);
        }
    }
}
