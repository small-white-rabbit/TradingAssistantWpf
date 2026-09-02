using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 自动更新服务 - Velopack 集成（对应原版自动更新语义）
/// 更新源解析优先级：环境变量 STOCKREVIEW_UPDATE_URL > 安装目录 update-source.json 的 url 字段。
/// 流程：启动延迟 15s 后台检查 → 发现新版本静默下载并应用到磁盘 → 宠物气泡提示"下次启动生效"。
/// 未配置更新源、开发环境（非 Velopack 安装运行）或网络失败时静默跳过，仅记日志。
/// </summary>
public class UpdateService
{
    private readonly PetService _petService;

    public UpdateService(PetService petService) => _petService = petService;

    /// <summary>启动后台更新检查（fire-and-forget，异常全部吞掉仅记日志，不影响主流程）</summary>
    public void StartBackgroundCheck()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 延迟检查：避开启动期网络/IO 高峰（富途连接、云同步、WebView2 预热并发）
                await Task.Delay(TimeSpan.FromSeconds(15));

                var sourceUrl = ResolveSourceUrl();
                if (string.IsNullOrEmpty(sourceUrl))
                {
                    Log.Information("[更新] 未配置更新源，跳过检查（配置环境变量 STOCKREVIEW_UPDATE_URL 或安装目录 update-source.json）");
                    return;
                }

                var mgr = new Velopack.UpdateManager(sourceUrl);
                var update = await mgr.CheckForUpdatesAsync();
                if (update == null)
                {
                    Log.Information("[更新] 已是最新版本 v{Version}", App.AppVersion);
                    return;
                }

                var newVer = update.TargetFullRelease.Version.ToString();
                Log.Information("[更新] 发现新版本 v{New}（当前 v{Current}），开始后台下载", newVer, App.AppVersion);
                await mgr.DownloadUpdatesAsync(update);

                // 登记更新包，Update.exe 等待本进程优雅退出后静默安装（不自动重启），下次启动生效
                mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
                Log.Information("[更新] v{New} 已就绪，下次启动生效", newVer);
                _petService.ShowBubble($"新版本 v{newVer} 已就绪，下次启动自动生效", "hint", 10000, "软件更新");
            }
            catch (Exception ex)
            {
                // 开发环境（非 Velopack 安装运行）UpdateManager 操作会抛异常，属预期，降级为 Debug
                Log.Debug(ex, "[更新] 检查/应用更新失败（开发环境或更新源不可达时属预期）");
            }
        });
    }

    /// <summary>更新源解析：环境变量优先，其次安装目录 update-source.json</summary>
    private static string? ResolveSourceUrl()
    {
        var env = Environment.GetEnvironmentVariable("STOCKREVIEW_UPDATE_URL");
        if (!string.IsNullOrEmpty(env)) return env;

        var configPath = Path.Combine(App.AppBaseDir, "update-source.json");
        if (!File.Exists(configPath)) return null;
        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<UpdateSourceConfig>(json);
            return string.IsNullOrEmpty(config?.Url) ? null : config.Url;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[更新] 读取更新源配置失败: {Path}", configPath);
            return null;
        }
    }

    private class UpdateSourceConfig
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
