using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace StockReviewWpf.Services;

/// <summary>
/// OpenD 服务 - 对应原版 OpenD 端口探测
/// </summary>
public class OpenDService
{
    private const int DefaultPort = 11118;
    private const string DefaultHost = "127.0.0.1";

    public bool IsPortListening(int port = DefaultPort, string host = DefaultHost)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            if (success) { client.EndConnect(result); return true; }
            return false;
        }
        catch { return false; }
    }

    public async Task<bool> IsPortListeningAsync(int port = DefaultPort, string host = DefaultHost)
    {
        try { using var client = new TcpClient(); await client.ConnectAsync(host, port); return true; }
        catch { return false; }
    }
}
