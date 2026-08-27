using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 云端同步服务 - 对应 main.cjs 的 registerCloudSyncHandlers
/// 组合 WebDAV + BackupService 实现云端备份/恢复
/// </summary>
public class CloudSyncService
{
    private readonly WebDavSyncService _webdav;
    private readonly BackupService _backupService;
    private readonly string _backupsDir;

    // 进度回调
    public Action<string, int, string>? OnProgress { get; set; }

    public CloudSyncService(WebDavSyncService webdav, BackupService backupService, string dataDir)
    {
        _webdav = webdav;
        _backupService = backupService;
        _backupsDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(_backupsDir);
    }

    private void SendProgress(string step, int progress, string message)
    {
        OnProgress?.Invoke(step, progress, message);
    }

    private static string GenerateBackupFileName()
    {
        var now = DateTime.Now;
        return $"backup-{now:yyyy-MM-dd_HHmmss}.zip";
    }

    // ============ 测试连接 ============
    public async Task<(bool success, string message)> TestConnectionAsync(string serverUrl, string username, string password)
    {
        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return (false, "请填写完整的 WebDAV 配置（服务器地址、用户名、密码）");
        return await _webdav.TestConnectionAsync(serverUrl, username, password);
    }

    // ============ 上传备份 ============
    public async Task<(bool success, string message, string? fileName, int images)> UploadAsync(
        string serverUrl, string username, string password, string remotePath,
        string? localStorageJson = null)
    {
        try
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return (false, "请填写完整的 WebDAV 配置", null, 0);

            _webdav.Configure(serverUrl, username, password);

            var backupFileName = GenerateBackupFileName();
            var tempZipPath = Path.Combine(_backupsDir, backupFileName);

            var exportResult = await _backupService.ExportZipAsync(tempZipPath, localStorageJson);
            if (!exportResult.Success)
                return (false, exportResult.Message, null, 0);

            await _webdav.EnsureDirAsync(remotePath);
            var remoteFilePath = remotePath.TrimEnd('/') + "/" + backupFileName;
            var uploadResult = await _webdav.UploadFileAsync(tempZipPath, remoteFilePath);

            // 清理本地临时文件
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { /* 忽略 */ }

            if (uploadResult.success)
                return (true, $"备份上传成功\n文件: {backupFileName}\n截图: {exportResult.Images} 张", backupFileName, exportResult.Images);
            return (false, uploadResult.message, null, 0);
        }
        catch (Exception ex)
        {
            return (false, $"上传失败: {ex.Message}", null, 0);
        }
    }

    // ============ 下载并恢复 ============
    public async Task<(bool success, string message, int added, int updated, int images, string? localStorageJson)> DownloadAsync(
        string serverUrl, string username, string password, string remotePath, string fileName)
    {
        try
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return (false, "请填写完整的 WebDAV 配置", 0, 0, 0, null);

            if (string.IsNullOrEmpty(fileName))
                return (false, "请指定要下载的备份文件名", 0, 0, 0, null);

            _webdav.Configure(serverUrl, username, password);
            SendProgress("download", 10, "正在连接云端服务器...");

            SendProgress("download", 20, "正在下载备份文件...");
            var remoteFilePath = remotePath.TrimEnd('/') + "/" + fileName;
            var tempDir = Path.Combine(_backupsDir, "temp");
            Directory.CreateDirectory(tempDir);
            var tempZipPath = Path.Combine(tempDir, fileName);

            var downloadResult = await _webdav.DownloadFileAsync(remoteFilePath, tempZipPath);
            if (!downloadResult.success)
                return (false, downloadResult.message, 0, 0, 0, null);

            SendProgress("extract", 40, "下载完成，正在解压备份文件...");

            SendProgress("parse", 50, "正在解析数据文件...");
            var importResult = await _backupService.ImportZipAsync(tempZipPath);
            if (!importResult.Success)
                return (false, importResult.Message, 0, 0, 0, null);

            SendProgress("cleanup", 95, "正在清理临时文件...");
            try
            {
                if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                if (Directory.Exists(tempDir) && !Directory.EnumerateFiles(tempDir).Any()) Directory.Delete(tempDir);
            }
            catch { /* 忽略 */ }

            SendProgress("complete", 100, "恢复完成！");
            return (true, importResult.Message, importResult.Added, importResult.Updated, importResult.Images, importResult.LocalStorageJson);
        }
        catch (Exception ex)
        {
            SendProgress("error", 0, "恢复失败: " + ex.Message);
            return (false, $"下载/导入失败: {ex.Message}", 0, 0, 0, null);
        }
    }

    // ============ 列出云端备份 ============
    public async Task<(bool success, List<WebDavFileInfo>? files, string? message)> ListAsync(
        string serverUrl, string username, string password, string remotePath)
    {
        try
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return (false, null, "请填写完整的 WebDAV 配置");

            _webdav.Configure(serverUrl, username, password);
            var result = await _webdav.ListFilesAsync(remotePath);
            if (result.success && result.files != null)
            {
                var zipFiles = result.files.Where(f => f.Name.EndsWith(".zip")).ToList();
                return (true, zipFiles, $"找到 {zipFiles.Count} 个备份文件");
            }
            return (false, null, result.message);
        }
        catch (Exception ex)
        {
            return (false, null, $"列出文件失败: {ex.Message}");
        }
    }

    // ============ 删除云端备份 ============
    public async Task<(bool success, string message)> DeleteAsync(
        string serverUrl, string username, string password, string remotePath, string fileName)
    {
        try
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return (false, "请填写完整的 WebDAV 配置");
            if (string.IsNullOrEmpty(fileName))
                return (false, "请指定要删除的备份文件名");

            _webdav.Configure(serverUrl, username, password);
            var remoteFilePath = remotePath.TrimEnd('/') + "/" + fileName;
            return await _webdav.DeleteFileAsync(remoteFilePath);
        }
        catch (Exception ex)
        {
            return (false, $"删除失败: {ex.Message}");
        }
    }

    // ============ 自动同步（退出前触发） ============
    public async Task<(bool success, string? fileName, int images, string? error)> AutoSyncAsync(
        string serverUrl, string username, string password, string remotePath)
    {
        try
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return (false, null, 0, "WebDAV 配置不完整");

            _webdav.Configure(serverUrl, username, password);

            var backupFileName = GenerateBackupFileName();
            var tempZipPath = Path.Combine(_backupsDir, backupFileName);

            var exportResult = await _backupService.ExportZipAsync(tempZipPath);
            if (!exportResult.Success)
                return (false, null, 0, exportResult.Message);

            await _webdav.EnsureDirAsync(remotePath);
            var remoteFilePath = remotePath.TrimEnd('/') + "/" + backupFileName;
            var uploadRes = await _webdav.UploadFileAsync(tempZipPath, remoteFilePath);

            // 清理本地临时文件
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { /* 忽略 */ }

            if (uploadRes.success)
                return (true, backupFileName, exportResult.Images, null);
            return (false, null, 0, uploadRes.message);
        }
        catch (Exception ex)
        {
            return (false, null, 0, $"自动同步失败: {ex.Message}");
        }
    }
}
