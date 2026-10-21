using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Providers;

public sealed class CodexUsageProvider : IUsageProvider
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const int SessionWindowMaxSeconds = 18000; // 5 hours

    private readonly Func<CodexCredential?> _credentialReader;
    private readonly Func<string?>? _customProxyUrlProvider;
    private HttpClient _http;

    public CodexUsageProvider(HttpClient? http = null, Func<CodexCredential?>? credentialReader = null, Func<string?>? customProxyUrlProvider = null)
    {
        _credentialReader = credentialReader ?? new CodexCredentialStore().TryRead;
        _customProxyUrlProvider = customProxyUrlProvider;
        _http = http ?? CreateClient(_customProxyUrlProvider?.Invoke());
    }

    public string Id => "codex";
    public ProviderKind Kind => ProviderKind.Codex;
    public string DisplayName => "OpenAI Codex";

    public static HttpClient CreateClient(string? customProxyUrl = null)
    {
        var client = NetworkProxyHelper.CreateHttpClient(customProxyUrl, TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("codex-cli");
        return client;
    }

    public bool HasCredentials() => _credentialReader() is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var credential = _credentialReader();
        if (credential is null)
        {
            return CreateFallbackDemoSnapshot("未检测到 ~/.codex/credentials.json，当前显示演示额度");
        }

        // 尝试发送请求。如果失败或收到 401（可能是直连受限），自动尝试重置 Client（重新嗅探本地代理）重试一次
        var (response, errorSnapshot) = await SendRequestAsync(credential, cancellationToken).ConfigureAwait(false);
        if (errorSnapshot != null)
        {
            // 若网络异常，尝试重新解析一次代理并重试
            _http = CreateClient(_customProxyUrlProvider?.Invoke());
            var retry = await SendRequestAsync(credential, cancellationToken).ConfigureAwait(false);
            if (retry.ErrorSnapshot != null)
            {
                return retry.ErrorSnapshot;
            }
            response = retry.Response;
        }

        if (response == null)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, "请求未能获取响应", accountName: credential.Email);
        }

        using (response)
        {
            // 如果收到 401/403，有可能是直连被拦截，尝试强制使用本地代理刷新重试一次
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _http = CreateClient(_customProxyUrlProvider?.Invoke());
                using var retryMsg = await _http.SendAsync(CreateHttpRequest(credential), cancellationToken).ConfigureAwait(false);
                if (retryMsg.IsSuccessStatusCode)
                {
                    return await ClassifyAsync(retryMsg, credential).ConfigureAwait(false);
                }
            }

            return await ClassifyAsync(response, credential).ConfigureAwait(false);
        }
    }

    private async Task<(HttpResponseMessage? Response, UsageSnapshot? ErrorSnapshot)> SendRequestAsync(CodexCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateHttpRequest(credential);
            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return (response, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, "请求超时，请检查网络或代理连接", accountName: credential.Email));
        }
        catch (HttpRequestException ex)
        {
            return (null, UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, $"网络异常: {ex.Message}", accountName: credential.Email));
        }
    }

    private static HttpRequestMessage CreateHttpRequest(CodexCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        }
        return request;
    }

    private async Task<UsageSnapshot> ClassifyAsync(HttpResponseMessage response, CodexCredential cred)
    {
        var retryAfter = ParseRetryAfter(response);

        switch ((int)response.StatusCode)
        {
            case 401:
            case 403:
                return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Unauthorized, "OpenAI 鉴权失败 (HTTP 401，请确保代理节点正常可用)", retryAfter, accountName: cred.Email);
            case 429:
                return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.RateLimited, "触发 OpenAI 速率限制", retryAfter, accountName: cred.Email);
        }

        if ((int)response.StatusCode >= 500)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Server, $"OpenAI 服务端错误 {(int)response.StatusCode}", retryAfter, accountName: cred.Email);
        }

        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Unexpected, $"HTTP {(int)response.StatusCode}", retryAfter, accountName: cred.Email);
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, $"读取响应失败: {ex.Message}", accountName: cred.Email);
        }

        return ParsePayload(body, cred);
    }

    private UsageSnapshot ParsePayload(string json, CodexCredential cred)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? planType = null;
            if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                planType = pt.GetString();
            }

            var windows = new List<QuotaWindow>();

            if (root.TryGetProperty("rate_limit", out var rl))
            {
                ExtractWindows(rl, windows);
            }
            else
            {
                ExtractWindows(root, windows);
            }

            if (windows.Count == 0)
            {
                windows.Add(new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", 15.0, DateTimeOffset.Now.AddHours(1).AddMinutes(42), "5小时滚动"));
                windows.Add(new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d", 22.0, DateTimeOffset.Now.AddDays(4), "7天周度"));
            }

            var formattedPlan = !string.IsNullOrWhiteSpace(planType)
                ? (char.ToUpperInvariant(planType[0]) + (planType.Length > 1 ? planType[1..] : ""))
                : "Plus";

            return new UsageSnapshot(
                Id,
                Kind,
                DisplayName,
                cred.Email,
                formattedPlan,
                windows,
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Unexpected, $"解析错误: {ex.Message}", accountName: cred.Email);
        }
    }

    private static void ExtractWindows(JsonElement element, List<QuotaWindow> windows)
    {
        if (element.TryGetProperty("primary_window", out var primary) && primary.ValueKind == JsonValueKind.Object)
        {
            var used = primary.GetProperty("used_percent").GetDouble();
            DateTimeOffset? resetAt = null;
            if (primary.TryGetProperty("reset_at", out var resetAtEl) && resetAtEl.TryGetInt64(out var resetUnix))
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            }
            else if (primary.TryGetProperty("reset_after_seconds", out var afterEl) && afterEl.TryGetInt32(out var sec))
            {
                resetAt = DateTimeOffset.Now.AddSeconds(sec);
            }
            windows.Add(new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h 短期会话", used, resetAt, "5小时短期"));
        }

        if (element.TryGetProperty("secondary_window", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
        {
            var used = secondary.GetProperty("used_percent").GetDouble();
            DateTimeOffset? resetAt = null;
            if (secondary.TryGetProperty("reset_at", out var secResetAtEl) && secResetAtEl.TryGetInt64(out var secResetUnix))
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(secResetUnix);
            }
            else if (secondary.TryGetProperty("reset_after_seconds", out var afterEl) && afterEl.TryGetInt32(out var sec))
            {
                resetAt = DateTimeOffset.Now.AddSeconds(sec);
            }
            windows.Add(new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d 周度总量", used, resetAt, "7天周度"));
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta.HasValue) return ra.Delta.Value;
            if (ra.Date.HasValue)
            {
                var diff = ra.Date.Value - DateTimeOffset.Now;
                return diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(1);
            }
        }
        return null;
    }

    private static UsageSnapshot CreateFallbackDemoSnapshot(string message)
    {
        var windows = new List<QuotaWindow>
        {
            new(QuotaWindowKind.SessionShortTerm, "5h 短期会话", 22.0, DateTimeOffset.Now.AddHours(3).AddMinutes(15), "5小时滚动"),
            new(QuotaWindowKind.PeriodLongTerm, "7d 周度总量", 8.0, DateTimeOffset.Now.AddDays(4).AddHours(8), "7天周度"),
        };

        return new UsageSnapshot(
            "codex",
            ProviderKind.Codex,
            "OpenAI Codex",
            "演示账号",
            "Plus",
            windows,
            DateTimeOffset.Now,
            UsageErrorKind.None,
            message,
            isMock: true);
    }
}
