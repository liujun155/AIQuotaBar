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

public sealed class ClaudeUsageProvider : IUsageProvider
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string AnthropicBetaHeader = "oauth-2025-04-20";
    private const string DefaultUserAgent = "claude-code/2.1.159";

    private readonly Func<ClaudeCredential?> _credentialReader;
    private readonly Func<string?>? _customProxyUrlProvider;
    private readonly HttpClient _http;

    public ClaudeUsageProvider(HttpClient? http = null, Func<ClaudeCredential?>? credentialReader = null, Func<string?>? customProxyUrlProvider = null)
    {
        _credentialReader = credentialReader ?? new ClaudeCredentialStore().TryRead;
        _customProxyUrlProvider = customProxyUrlProvider;
        _http = http ?? CreateDefaultClient(_customProxyUrlProvider?.Invoke());
    }

    public string Id => "claude";
    public ProviderKind Kind => ProviderKind.Claude;
    public string DisplayName => "Claude Code";

    public bool HasCredentials() => _credentialReader() is not null;

    public static HttpClient CreateDefaultClient(string? customProxyUrl = null)
    {
        var client = NetworkProxyHelper.CreateHttpClient(customProxyUrl, TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var credential = _credentialReader();
        if (credential is null || string.IsNullOrWhiteSpace(credential.Token))
        {
            return UsageSnapshot.Failure(
                Id,
                Kind,
                DisplayName,
                UsageErrorKind.NoCredentials,
                "未检测到 Claude Code 登录凭证。请在终端执行 'claude auth login' 登录，或设置 CLAUDE_CODE_OAUTH_TOKEN 环境变量。");
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            if (!request.Headers.Contains("anthropic-beta"))
            {
                request.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicBetaHeader);
            }

            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(
                Id,
                Kind,
                DisplayName,
                UsageErrorKind.Network,
                $"连接 Anthropic 失败: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return UsageSnapshot.Failure(
                    Id,
                    Kind,
                    DisplayName,
                    UsageErrorKind.Unauthorized,
                    "Claude 凭据已失效或未授权。请在终端执行 'claude auth login' 重新登录。");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                return UsageSnapshot.Failure(
                    Id,
                    Kind,
                    DisplayName,
                    UsageErrorKind.RateLimited,
                    "Claude 额度查询已达到限流上限，正在等待自动冷却恢复",
                    retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorKind = (int)response.StatusCode >= 500 ? UsageErrorKind.Server : UsageErrorKind.Unexpected;
                return UsageSnapshot.Failure(
                    Id,
                    Kind,
                    DisplayName,
                    errorKind,
                    $"Anthropic 接口响应异常 (HTTP {(int)response.StatusCode})");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseUsageResponse(content, credential);
        }
    }

    public UsageSnapshot ParseUsageResponse(string json, ClaudeCredential credential)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var windows = new List<QuotaWindow>();

            // 1. 解析 5h 滚动周期 (five_hour)
            if (root.TryGetProperty("five_hour", out var fiveHourProp) && fiveHourProp.ValueKind == JsonValueKind.Object)
            {
                var w = ParseWindowObject(fiveHourProp, QuotaWindowKind.SessionShortTerm, "5h 滚动周期", "5小时短期会话额度");
                if (w != null) windows.Add(w);
            }

            // 2. 解析 7d 周度周期 (seven_day 或 细分模型项)
            QuotaWindow? sevenDayWindow = null;
            if (root.TryGetProperty("seven_day", out var sevenDayProp) && sevenDayProp.ValueKind == JsonValueKind.Object)
            {
                sevenDayWindow = ParseWindowObject(sevenDayProp, QuotaWindowKind.PeriodLongTerm, "7d 周度周期", "7天周度总配额");
            }
            else
            {
                // 回退查找 细分模型 (seven_day_sonnet / seven_day_opus)
                QuotaWindow? highestUsedSubWindow = null;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.StartsWith("seven_day_", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var modelName = prop.Name["seven_day_".Length..];
                        var subWin = ParseWindowObject(prop.Value, QuotaWindowKind.PeriodLongTerm, $"7d {modelName}", $"7天 {modelName} 配额");
                        if (subWin != null)
                        {
                            if (highestUsedSubWindow == null || subWin.UsedPercent > highestUsedSubWindow.UsedPercent)
                            {
                                highestUsedSubWindow = subWin;
                            }
                        }
                    }
                }
                sevenDayWindow = highestUsedSubWindow;
            }

            if (sevenDayWindow != null)
            {
                windows.Add(sevenDayWindow);
            }

            // 如果没有匹配到任何标准窗口，尝试读取通用 windows 或 usage 结构
            if (windows.Count == 0)
            {
                return UsageSnapshot.Failure(
                    Id,
                    Kind,
                    DisplayName,
                    UsageErrorKind.Unexpected,
                    "未能识别到 Claude 配额窗口信息");
            }

            var accountName = !string.IsNullOrWhiteSpace(credential.AccountName)
                ? credential.AccountName
                : (credential.IsOAuth ? "Anthropic Claude Pro/Max" : "Anthropic API Key");

            var planType = credential.IsOAuth ? "Claude Code" : "API";

            return new UsageSnapshot(
                Id,
                Kind,
                DisplayName,
                accountName,
                planType,
                windows,
                DateTimeOffset.Now,
                UsageErrorKind.None,
                errorMessage: null,
                isMock: false);
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(
                Id,
                Kind,
                DisplayName,
                UsageErrorKind.Unexpected,
                $"解析 Claude 配额数据失败: {ex.Message}");
        }
    }

    private static QuotaWindow? ParseWindowObject(JsonElement element, QuotaWindowKind kind, string label, string description)
    {
        // 提取已用百分比 (utilization 或 used_percentage 或 percent)
        double? usedPercent = null;
        if (element.TryGetProperty("utilization", out var utilProp) && utilProp.TryGetDouble(out var uVal))
        {
            usedPercent = uVal;
        }
        else if (element.TryGetProperty("used_percentage", out var upProp) && upProp.TryGetDouble(out var upVal))
        {
            usedPercent = upVal;
        }
        else if (element.TryGetProperty("percent", out var pProp) && pProp.TryGetDouble(out var pVal))
        {
            usedPercent = pVal;
        }

        if (!usedPercent.HasValue)
        {
            return null;
        }

        // 提取重置时间 resets_at
        DateTimeOffset? resetAt = null;
        if (element.TryGetProperty("resets_at", out var resetProp) && resetProp.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(resetProp.GetString(), out var parsedDto))
            {
                resetAt = parsedDto;
            }
        }
        else if (element.TryGetProperty("reset_at", out var resetProp2) && resetProp2.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(resetProp2.GetString(), out var parsedDto2))
            {
                resetAt = parsedDto2;
            }
        }

        return new QuotaWindow(kind, label, usedPercent.Value, resetAt, description);
    }
}
