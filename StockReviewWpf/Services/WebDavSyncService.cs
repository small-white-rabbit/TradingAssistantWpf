using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// WebDAV 同步服务 - 完整翻译 webdav-sync.cjs
/// 支持 PROPFIND / PUT / GET / MKCOL / DELETE
/// </summary>
public class WebDavSyncService
{
    private string? _serverUrl;
    private string? _username;
    private string? _password;
    private readonly HttpClient _httpClient;

    private static bool _selfChecked;

    public WebDavSyncService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StockReviewSystem/1.5");
#if DEBUG
        DebugSelfCheck();
#endif
    }

    /// <summary>Debug 自检：PROPFIND multistatus 解析（带 d: 前缀 / 默认命名空间 / 目录跳过 / 排序）最小断言</summary>
    private static void DebugSelfCheck()
    {
        if (_selfChecked) return;
        _selfChecked = true;
        // 坚果云等服务器返回带 d: 前缀的响应
        var prefixed = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/backup-2026-08-24_235959.zip</d:href>
                <d:propstat><d:prop>
                  <d:displayname>backup-2026-08-24_235959.zip</d:displayname>
                  <d:getcontentlength>1024</d:getcontentlength>
                  <d:getlastmodified>Mon, 24 Aug 2026 23:59:59 GMT</d:getlastmodified>
                </d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/backup-2026-08-25_010203.zip</d:href>
                <d:propstat><d:prop><d:getcontentlength>2048</d:getcontentlength></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/readme.txt</d:href>
                <d:propstat><d:prop><d:getcontentlength>10</d:getcontentlength></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        var files = ParseMultiStatus(prefixed);
        System.Diagnostics.Debug.Assert(files.Count == 2, $"带前缀应解析出 2 个备份，实际 {files.Count}");
        System.Diagnostics.Debug.Assert(files[0].Name == "backup-2026-08-25_010203.zip", "应按时间倒序，最新在前");
        System.Diagnostics.Debug.Assert(files[1].Size == 1024, "getcontentlength 应解析出 1024");
        // 无前缀（默认命名空间）响应
        var plain = """
            <?xml version="1.0" encoding="utf-8"?>
            <multistatus xmlns="DAV:">
              <response>
                <href>/dav/backup-2026-08-20_100000.zip</href>
                <propstat><prop><getcontentlength>512</getcontentlength></prop></propstat>
              </response>
            </multistatus>
            """;
        var files2 = ParseMultiStatus(plain);
        System.Diagnostics.Debug.Assert(files2.Count == 1 && files2[0].Size == 512, "无前缀响应也应解析成功");
    }

    public void Configure(string serverUrl, string username, string password)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _username = username;
        _password = password;
        // 鉴权头改为每请求注入（见 SendAsync）：
        // 共享单例的 DefaultRequestHeaders.Authorization 在并发（连接测试/上传/下载）时会互相覆盖
    }

    // ============ URL 构建 ============
    private string BuildUrl(string remotePath = "")
    {
        if (string.IsNullOrEmpty(remotePath)) return _serverUrl!;
        var suffix = remotePath.StartsWith('/') ? remotePath : "/" + remotePath;
        return _serverUrl! + suffix;
    }

    // ============ 通用请求 ============
    private async Task<WebDavResponse> SendAsync(HttpMethod method, string remotePath, HttpContent? content = null, Dictionary<string, string>? extraHeaders = null)
    {
        var url = BuildUrl(remotePath);
        using var req = new HttpRequestMessage(method, url);
        // 每请求注入 Basic 鉴权，保证并发安全（Configure 只存凭据）
        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
        if (content != null) req.Content = content;
        if (extraHeaders != null)
        {
            foreach (var kv in extraHeaders)
            {
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsByteArrayAsync();
        return new WebDavResponse(resp.StatusCode, body, resp.Headers);
    }

    // ============ 状态码消息 ============
    private static string StatusMessage(string action, int statusCode)
    {
        if (statusCode == 401 || statusCode == 403) return $"{action}失败：用户名或密码错误";
        if (statusCode == 404) return $"{action}失败：远程路径不存在";
        if (statusCode == 405) return $"{action}失败：服务器不支持当前 WebDAV 请求";
        return $"{action}失败：服务器返回状态码 {statusCode}";
    }

    // ============ 测试连接 ============
    public async Task<(bool success, string message)> TestConnectionAsync(string? serverUrl = null, string? username = null, string? password = null)
    {
        try
        {
            if (serverUrl != null) Configure(serverUrl, username!, password!);
            var resp = await SendAsync(new HttpMethod("PROPFIND"), "", null, new() { ["Depth"] = "0" });
            if ((int)resp.StatusCode == 207 || (int)resp.StatusCode == 200)
                return (true, "连接成功");
            return (false, StatusMessage("连接", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    // ============ 上传文件 ============
    public async Task<(bool success, string message)> UploadFileAsync(string localFilePath, string remotePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(localFilePath);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var resp = await SendAsync(HttpMethod.Put, remotePath, content);
            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                return (true, "上传成功");
            return (false, StatusMessage("上传", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, $"上传失败：{ex.Message}");
        }
    }

    // ============ 下载文件 ============
    public async Task<(bool success, string message, string? filePath)> DownloadFileAsync(string remotePath, string localFilePath)
    {
        try
        {
            var resp = await SendAsync(HttpMethod.Get, remotePath);
            if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
                await File.WriteAllBytesAsync(localFilePath, resp.Body);
                return (true, "下载成功", localFilePath);
            }
            return (false, StatusMessage("下载", (int)resp.StatusCode), null);
        }
        catch (Exception ex)
        {
            return (false, $"下载失败：{ex.Message}", null);
        }
    }

    // ============ 列出文件 ============
    public async Task<(bool success, List<WebDavFileInfo>? files, string? message)> ListFilesAsync(string remotePath)
    {
        try
        {
            var resp = await SendAsync(new HttpMethod("PROPFIND"), remotePath, null, new() { ["Depth"] = "1" });
            if ((int)resp.StatusCode == 207)
            {
                var xml = Encoding.UTF8.GetString(resp.Body);
                var files = ParseMultiStatus(xml);
                return (true, files, null);
            }
            return (false, null, StatusMessage("列出文件", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, null, $"列出文件失败：{ex.Message}");
        }
    }

    // ============ 创建目录 ============
    public async Task<(bool success, string message)> EnsureDirAsync(string remotePath)
    {
        try
        {
            var resp = await SendAsync(new HttpMethod("MKCOL"), remotePath);
            if ((int)resp.StatusCode == 201 || (int)resp.StatusCode == 301 || (int)resp.StatusCode == 405)
                return (true, "目录可用");
            return (false, StatusMessage("创建目录", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, $"创建目录失败：{ex.Message}");
        }
    }

    // ============ 删除文件 ============
    public async Task<(bool success, string message)> DeleteFileAsync(string remotePath)
    {
        try
        {
            var resp = await SendAsync(HttpMethod.Delete, remotePath);
            if ((int)resp.StatusCode == 200 || (int)resp.StatusCode == 204)
                return (true, "删除成功");
            return (false, StatusMessage("删除", (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, $"删除失败：{ex.Message}");
        }
    }

    // ============ XML 解析 PROPFIND multistatus ============
    private static string DecodeXmlText(string text)
    {
        return text
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    private static string GetTagText(XmlElement parent, string tagName)
    {
        // WebDAV 响应的元素带命名空间前缀（如 <d:href>）：
        // 单参数 GetElementsByTagName 按完整限定名匹配，对带前缀元素 0 命中，
        // 必须先按 "DAV:" 命名空间查（回退到无前缀匹配）
        var nodes = parent.GetElementsByTagName(tagName, "DAV:");
        if (nodes.Count == 0) nodes = parent.GetElementsByTagName(tagName);
        if (nodes.Count == 0) return "";
        return DecodeXmlText(nodes[0]!.InnerText.Trim());
    }

    private static string GetBackupSortKey(WebDavFileInfo file)
    {
        var match = Regex.Match(file.Name, @"^backup-(\d{4})-(\d{2})-(\d{2})_(\d{6})\.zip$");
        if (match.Success) return $"{match.Groups[1]}{match.Groups[2]}{match.Groups[3]}{match.Groups[4]}";
        if (DateTime.TryParse(file.LastModified, out var dt)) return dt.Ticks.ToString("D19");
        return "0";
    }

    private static List<WebDavFileInfo> ParseMultiStatus(string xml)
    {
        var files = new List<WebDavFileInfo>();
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            // WebDAV 默认命名空间 "DAV:"
            nsmgr.AddNamespace("D", "DAV:");

            var responses = doc.GetElementsByTagName("response", "DAV:");
            if (responses.Count == 0) responses = doc.GetElementsByTagName("response");

            foreach (XmlElement resp in responses)
            {
                var hrefText = GetTagText(resp, "href");
                if (string.IsNullOrEmpty(hrefText)) continue;

                // 跳过目录（collection）
                var collectionNodes = resp.GetElementsByTagName("collection", "DAV:");
                if (collectionNodes.Count == 0) collectionNodes = resp.GetElementsByTagName("collection");
                if (collectionNodes.Count > 0) continue;

                var href = Uri.UnescapeDataString(hrefText);
                var name = href.TrimEnd('/').Split('/').Last();
                if (string.IsNullOrEmpty(name)) continue;

                // 仅匹配 backup-YYYY-MM-DD_HHMMSS.zip
                if (!Regex.IsMatch(name, @"^backup-\d{4}-\d{2}-\d{2}_\d{6}\.zip$", RegexOptions.IgnoreCase)) continue;

                var sizeText = GetTagText(resp, "getcontentlength");
                long.TryParse(sizeText, out var size);
                var lastModified = GetTagText(resp, "getlastmodified");

                files.Add(new WebDavFileInfo
                {
                    Name = name,
                    Path = href,
                    Size = size,
                    LastModified = lastModified
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ParseMultiStatus XML 解析失败");
        }

        files.Sort((a, b) => string.Compare(GetBackupSortKey(b), GetBackupSortKey(a), StringComparison.Ordinal));
        return files;
    }
}

// ============ 辅助类型 ============
public record WebDavResponse(System.Net.HttpStatusCode StatusCode, byte[] Body, System.Net.Http.Headers.HttpResponseHeaders Headers);

public class WebDavFileInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string LastModified { get; set; } = "";
    public string SizeText => Size >= 1048576 ? $"{Size / 1048576.0:F1} MB" : $"{Size / 1024.0:F1} KB";
}
