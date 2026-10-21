using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AIQuotaBar.Core.Providers;

public static class NetworkProxyHelper
{
    private static readonly int[] CommonProxyPorts = { 7897, 7890, 7891, 10808, 10809, 2080, 8080 };
    private static (IWebProxy? Proxy, DateTime CheckedAt)? _cachedProxy;
    private static readonly object LockObj = new();

    public static IWebProxy? ResolveProxy(string? customProxyUrl = null)
    {
        // 1. 如果用户显式指定了自定义代理
        if (!string.IsNullOrWhiteSpace(customProxyUrl))
        {
            try
            {
                return new WebProxy(customProxyUrl.Trim());
            }
            catch
            {
                // 无效代理格式则继续向下回退
            }
        }

        // 2. 检查环境变量
        var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                  ?? Environment.GetEnvironmentVariable("ALL_PROXY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                return new WebProxy(env.Trim());
            }
            catch
            {
            }
        }

        lock (LockObj)
        {
            // 3. 缓存检测结果 30 秒，避免高频连接探测
            if (_cachedProxy.HasValue && (DateTime.Now - _cachedProxy.Value.CheckedAt).TotalSeconds < 30)
            {
                return _cachedProxy.Value.Proxy;
            }

            // 4. 优先嗅探本机运行中的流行代理客户端核心端口 (如 Clash Verge / Mihomo 7897, Clash 7890, v2rayN 10809/10808 等)
            foreach (var port in CommonProxyPorts)
            {
                if (IsPortListening("127.0.0.1", port, 40))
                {
                    var proxy = new WebProxy($"http://127.0.0.1:{port}");
                    _cachedProxy = (proxy, DateTime.Now);
                    return proxy;
                }
            }

            // 5. 回退系统默认代理设置 (WinINet)
            var sysProxy = HttpClient.DefaultProxy;
            _cachedProxy = (sysProxy, DateTime.Now);
            return sysProxy;
        }
    }

    public static HttpClient CreateHttpClient(string? customProxyUrl = null, TimeSpan? timeout = null)
    {
        var proxy = ResolveProxy(customProxyUrl);
        var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        return client;
    }

    public static bool IsPortListening(string host, int port, int timeoutMs = 40)
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(timeoutMs) && client.Connected)
            {
                client.EndConnect(ar);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
