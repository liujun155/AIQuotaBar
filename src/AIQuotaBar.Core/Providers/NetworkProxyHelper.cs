using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.Core.Providers;

public static class NetworkProxyHelper
{
    // 默认优先检测 7890，其次 7897 及常见端口
    public const string DefaultProxyUrl = "http://127.0.0.1:7890";
    private static readonly int[] CommonProxyPorts = { 7890, 7897, 7891, 10808, 10809, 2080, 8080 };
    private static (IWebProxy? Proxy, DateTime CheckedAt)? _cachedProxy;
    private static readonly object LockObj = new();

    public static void InvalidateCache()
    {
        lock (LockObj)
        {
            _cachedProxy = null;
        }
    }

    public static IWebProxy? ResolveProxy(AppProxyMode mode = AppProxyMode.Auto, string? customProxyUrl = null)
    {
        switch (mode)
        {
            case AppProxyMode.Direct:
                return null;

            case AppProxyMode.System:
                return HttpClient.DefaultProxy;

            case AppProxyMode.Custom:
                var targetUrl = !string.IsNullOrWhiteSpace(customProxyUrl) ? customProxyUrl.Trim() : DefaultProxyUrl;
                try
                {
                    return new WebProxy(targetUrl);
                }
                catch
                {
                    return new WebProxy(DefaultProxyUrl);
                }

            case AppProxyMode.Auto:
            default:
                // 1. 如果用户提供了显式 URL 且处于自定义模式，使用自定义；否则进入智能自适应
                if (!string.IsNullOrWhiteSpace(customProxyUrl) && mode == AppProxyMode.Custom)
                {
                    try { return new WebProxy(customProxyUrl.Trim()); } catch {}
                }

                // 2. 检查环境变量
                var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                          ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                          ?? Environment.GetEnvironmentVariable("ALL_PROXY");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    try { return new WebProxy(env.Trim()); } catch {}
                }

                lock (LockObj)
                {
                    // 3. 缓存检测结果 30 秒，避免高频连接探测
                    if (_cachedProxy.HasValue && (DateTime.Now - _cachedProxy.Value.CheckedAt).TotalSeconds < 30)
                    {
                        return _cachedProxy.Value.Proxy;
                    }

                    // 4. 优先嗅探本机运行中的流行代理核心端口 (首选 7890, 其次 7897 等)
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
    }

    public static IWebProxy? ResolveProxy(string? customProxyUrl) =>
        ResolveProxy(!string.IsNullOrWhiteSpace(customProxyUrl) ? AppProxyMode.Custom : AppProxyMode.Auto, customProxyUrl);

    public static HttpClient CreateHttpClient(AppProxyMode mode = AppProxyMode.Auto, string? customProxyUrl = null, TimeSpan? timeout = null)
    {
        var proxy = ResolveProxy(mode, customProxyUrl);
        var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        return client;
    }

    public static HttpClient CreateHttpClient(string? customProxyUrl, TimeSpan? timeout = null)
    {
        var mode = !string.IsNullOrWhiteSpace(customProxyUrl) ? AppProxyMode.Custom : AppProxyMode.Auto;
        return CreateHttpClient(mode, customProxyUrl, timeout);
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

    /// <summary>
    /// 测试指定代理配置的连通性
    /// </summary>
    public static async Task<(bool Success, string Message, long ElapsedMs)> TestConnectivityAsync(
        AppProxyMode mode,
        string? customProxyUrl,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateHttpClient(mode, customProxyUrl, TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://chatgpt.com");
            req.Headers.UserAgent.ParseAdd("codex-cli");

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            // 无论返回 200 还是 401（未带token的握手响应），只要能收到 HTTP 响应头，即代表代理网络通路已完全打通！
            var statusCode = (int)resp.StatusCode;
            if (statusCode is >= 200 and < 500)
            {
                return (true, $"连接通畅 (HTTP {statusCode})", sw.ElapsedMilliseconds);
            }

            return (false, $"服务器返回异常: HTTP {statusCode}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"连接失败: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
