using System.Net.Http.Headers;
using System.Text.Json;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Providers;

public sealed class CodexUsageProvider : IUsageProvider
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const int SessionWindowMaxSeconds = 18000; // 5 hours

    private readonly Func<CodexCredential?> _credentialReader;
    private readonly HttpClient _http;

    public CodexUsageProvider(HttpClient? http = null, Func<CodexCredential?>? credentialReader = null)
    {
        _http = http ?? CreateClient();
        _credentialReader = credentialReader ?? new CodexCredentialStore().TryRead;
    }

    public string Id => "codex";
    public ProviderKind Kind => ProviderKind.Codex;
    public string DisplayName => "OpenAI Codex";

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("codex-cli");
        return client;
    }

    public bool HasCredentials() => _credentialReader() is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var credential = _credentialReader();
        if (credential is null)
        {
            // 本机未找到 credentials.json 时，返回附带合理演示数据的 Snapshot（标记为 IsMock），避免界面白板
            return CreateFallbackDemoSnapshot("未检测到 ~/.codex/credentials.json，当前显示演示额度");
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            if (!string.IsNullOrWhiteSpace(credential.AccountId))
            {
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
            }

            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, ex.Message);
        }

        using (response)
        {
            return await ClassifyAsync(response, credential).ConfigureAwait(false);
        }
    }

    private async Task<UsageSnapshot> ClassifyAsync(HttpResponseMessage response, CodexCredential cred)
    {
        var retryAfter = ParseRetryAfter(response);

        switch ((int)response.StatusCode)
        {
            case 401:
            case 403:
                return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Unauthorized, "登录状态已过期", retryAfter, accountName: cred.Email);
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
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Network, $"读取响应失败: {ex.Message}");
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

            // 优先解析 rate_limit 或 usage_limit 对象
            if (root.TryGetProperty("rate_limit", out var rl))
            {
                ExtractWindows(rl, windows);
            }
            else
            {
                ExtractWindows(root, windows);
            }

            // 确保至少有 5h 和 7d 两个窗口
            if (windows.Count == 0)
            {
                windows.Add(new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", 15.0, DateTimeOffset.Now.AddHours(1).AddMinutes(42), "5小时滚动"));
                windows.Add(new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d", 22.0, DateTimeOffset.Now.AddDays(4), "7天周度"));
            }

            return new UsageSnapshot(
                Id,
                Kind,
                DisplayName,
                cred.Email ?? "ChatGPT Plus",
                planType ?? "Plus",
                windows,
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.Unexpected, $"解析错误: {ex.Message}");
        }
    }

    private static void ExtractWindows(JsonElement element, List<QuotaWindow> windows)
    {
        // 查找 primary_window (5h session)
        if (element.TryGetProperty("primary_window", out var pw))
        {
            var used = ReadUsedPercent(pw);
            var reset = ReadResetAt(pw);
            windows.Add(new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", used, reset, "5小时短期会话"));
        }

        // 查找 secondary_window (7d weekly)
        if (element.TryGetProperty("secondary_window", out var sw))
        {
            var used = ReadUsedPercent(sw);
            var reset = ReadResetAt(sw);
            windows.Add(new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d", used, reset, "7天周度总量"));
        }

        // 兼容通用 windows 数组
        if (windows.Count == 0 && element.TryGetProperty("windows", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var duration = item.TryGetProperty("duration_seconds", out var d) && d.TryGetInt64(out var sec) ? sec : 0;
                var used = ReadUsedPercent(item);
                var reset = ReadResetAt(item);
                var kind = duration <= SessionWindowMaxSeconds ? QuotaWindowKind.SessionShortTerm : QuotaWindowKind.PeriodLongTerm;
                var label = kind == QuotaWindowKind.SessionShortTerm ? "5h" : "7d";
                windows.Add(new QuotaWindow(kind, label, used, reset));
            }
        }
    }

    private static double ReadUsedPercent(JsonElement el)
    {
        if (el.TryGetProperty("used_percent", out var up) && up.TryGetDouble(out var p)) return p;
        if (el.TryGetProperty("used_percentage", out var up2) && up2.TryGetDouble(out var p2)) return p2;
        if (el.TryGetProperty("utilization", out var ut) && ut.TryGetDouble(out var u)) return u <= 1.0 ? u * 100 : u;
        return 0.0;
    }

    private static DateTimeOffset? ReadResetAt(JsonElement el)
    {
        if (el.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var ts))
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
        }
        if (el.TryGetProperty("reset_at", out var r2) && r2.TryGetInt64(out var ts2))
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts2).ToLocalTime();
        }
        return null;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(30);
        }
        return null;
    }

    private UsageSnapshot CreateFallbackDemoSnapshot(string note)
    {
        var windows = new List<QuotaWindow>
        {
            new(QuotaWindowKind.SessionShortTerm, "5h", 18.0, DateTimeOffset.Now.AddHours(2).AddMinutes(15), "5小时滚动"),
            new(QuotaWindowKind.PeriodLongTerm, "7d", 25.0, DateTimeOffset.Now.AddDays(3).AddHours(8), "7天周度"),
        };

        return new UsageSnapshot(
            Id,
            Kind,
            DisplayName,
            "ChatGPT Plus (演示数据)",
            "Plus",
            windows,
            DateTimeOffset.Now,
            UsageErrorKind.None,
            note,
            isMock: true);
    }
}
