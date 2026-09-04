using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockReview.Core.Data;
using StockReview.Core.Services;
using StockReview.Mcp;

using var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var dataDir = DataDirectoryResolver.Resolve(Environment.GetEnvironmentVariable("STOCKREVIEW_DATA_DIR"));
var dbPath = Path.Combine(dataDir, "data.db");
if (!File.Exists(dbPath))
    stderr.WriteLine($"[StockReview.Mcp] 警告: 数据文件不存在，将以空库启动: {dbPath}");

var db = new DatabaseService();
db.SetDataDir(dataDir);
db.Initialize();

builder.Services.AddSingleton(db);
builder.Services.AddSingleton<IDatabaseService>(db);
builder.Services.AddSingleton<SignalEventService>();
builder.Services.AddSingleton<TradePlanService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

stderr.WriteLine($"[StockReview.Mcp] 数据目录: {dataDir}");

await builder.Build().RunAsync();
