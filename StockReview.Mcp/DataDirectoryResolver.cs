using System.Text.Json;

namespace StockReview.Mcp;

internal static class DataDirectoryResolver
{
    public static string Resolve(string? envOverride)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            if (!Directory.Exists(envOverride))
                throw new DirectoryNotFoundException(
                    $"环境变量 STOCKREVIEW_DATA_DIR 指向的目录不存在: {envOverride}");
            return envOverride;
        }

        var appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
        var isVelopackInstalled = File.Exists(Path.Combine(appBaseDir, "Update.exe"))
            || string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(appBaseDir)),
                "current", StringComparison.OrdinalIgnoreCase);
        var dataRoot = isVelopackInstalled
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TradingAssistantWpf")
            : appBaseDir;

        var fromConfig = TryResolveFromConfig(Path.Combine(dataRoot, "data-dir.json"));
        if (fromConfig != null) return fromConfig;

        return Path.Combine(dataRoot, "data");
    }

    private static string? TryResolveFromConfig(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "DataDir", "dataDir", "datadir" })
            {
                if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var dir = el.GetString();
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
