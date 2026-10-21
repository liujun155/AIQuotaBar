using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Providers;

public sealed class GeminiUsageProvider : IUsageProvider
{
    private const string StatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string SummaryPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private static readonly HttpClient LocalHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };
    private readonly HttpClient _http;
    private readonly Func<bool, IReadOnlyList<AntigravityLocalEndpoint>> _discover;
    private int? _lastWorkingPort;

    public GeminiUsageProvider(HttpClient? http = null, Func<bool, IReadOnlyList<AntigravityLocalEndpoint>>? discover = null)
    {
        _http = http ?? LocalHttp;
        var discovery = new AntigravityLocalServerDiscovery();
        _discover = discover ?? discovery.Discover;
    }

    public string Id => "gemini";
    public ProviderKind Kind => ProviderKind.Gemini;
    public string DisplayName => "Gemini · Antigravity";
    public bool HasCredentials() => _discover(false).Count > 0;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await Task.Run(() => _discover(false), cancellationToken).ConfigureAwait(false);
        if (endpoints.Count == 0)
            return Failure(UsageErrorKind.NoCredentials, "未检测到 Antigravity 本地服务，请启动 Antigravity 并登录账号");

        UsageSnapshot? failure = null;
        var attempted = new HashSet<(int Port, string Token)>();
        // Retry discovery once after a restart/port change; successful polling reuses cached discovery.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var endpoint in endpoints.OrderByDescending(e => e.Port == _lastWorkingPort))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!attempted.Add((endpoint.Port, endpoint.CsrfToken))) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{StatusPath}");
                    request.Headers.Add("X-Codeium-Csrf-Token", endpoint.CsrfToken);
                    request.Headers.Add("Connect-Protocol-Version", "1");
                    request.Content = JsonContent.Create(new { metadata = new { ideName = "antigravity", extensionName = "antigravity", locale = "en" } });
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var retry = response.Headers.RetryAfter;
                        var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                        var httpFailure = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(UsageErrorKind.Unauthorized, "Antigravity 登录状态失效，请在 Antigravity 中重新登录"),
                            HttpStatusCode.TooManyRequests => UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.RateLimited, "Antigravity 额度接口暂时限流", delay),
                            _ => Failure(UsageErrorKind.Server, $"Antigravity 本地接口暂不可用（HTTP {(int)response.StatusCode}）"),
                        };
                        if (failure == null || Priority(httpFailure.Error) > Priority(failure.Error)) failure = httpFailure;
                        continue;
                    }
                    var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var snapshot = ParseStatus(payload);
                    snapshot = await ReadSummaryAsync(endpoint, snapshot, cancellationToken).ConfigureAwait(false);
                    if (snapshot.IsOk)
                    {
                        _lastWorkingPort = endpoint.Port;
                        return snapshot;
                    }
                    if (failure == null || Priority(snapshot.Error) >= Priority(failure.Error)) failure = snapshot;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failure ??= Failure(UsageErrorKind.Network, "Antigravity 本地服务响应超时");
                }
                catch (HttpRequestException)
                {
                    failure ??= Failure(UsageErrorKind.Network, "无法连接 Antigravity 本地服务，请确认程序正在运行");
                }
            }
            if (pass == 0) endpoints = await Task.Run(() => _discover(true), cancellationToken).ConfigureAwait(false);
        }
        return failure ?? Failure(UsageErrorKind.Network, "Antigravity 本地额度服务暂不可用");
    }

    private UsageSnapshot ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var status = Get(document.RootElement, "userStatus");
            var configs = Get(Get(status, "cascadeModelConfigData"), "clientModelConfigs");
            var quotas = new List<(string Family, string Name, double Remaining, DateTimeOffset? Reset)>();
            if (configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in configs.EnumerateArray())
                {
                    var label = Text(Get(config, "label"));
                    if (label?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var quota = Get(config, "quotaInfo");
                    var fraction = Get(quota, "remainingFraction");
                    DateTimeOffset? reset = DateTimeOffset.TryParse(Text(Get(quota, "resetTime")), out var time) ? time : null;
                    double remaining;
                    if (fraction.ValueKind == JsonValueKind.Number && fraction.TryGetDouble(out var value) && double.IsFinite(value))
                        remaining = Math.Clamp(value, 0, 1);
                    else if (fraction.ValueKind == JsonValueKind.Undefined && reset.HasValue)
                        remaining = 0; // Protobuf JSON can omit the default numeric zero.
                    else continue; // An empty quotaInfo does not mean 100% or 0% remaining.
                    var name = Regex.Replace(label, @"\s*\([^)]*\)\s*$", "").Trim();
                    var family = name.Contains("Pro", StringComparison.OrdinalIgnoreCase) ? "Pro" :
                        name.Contains("Flash", StringComparison.OrdinalIgnoreCase) ? "Flash" : name;
                    quotas.Add((family, name, remaining, reset));
                }
            }
            // High/Medium/Low often share one quota pool. Use the lowest known remaining value per family.
            var windows = quotas.GroupBy(q => q.Family).Select(group => group.OrderBy(q => q.Remaining).First())
                .OrderBy(q => q.Family == "Pro" ? 0 : q.Family == "Flash" ? 1 : 2)
                .Select(q => new QuotaWindow(QuotaWindowKind.ModelQuota, q.Family == "Pro" || q.Family == "Flash" ? q.Family : "Gem",
                    100 - q.Remaining * 100, q.Reset, $"{q.Name} · 模型额度"))
                .ToArray();
            if (windows.Length == 0)
                return Failure(UsageErrorKind.Server, "Antigravity 尚未返回 Gemini 模型额度，请确认已登录并稍后刷新");
            var plan = Text(Get(Get(Get(status, "planStatus"), "planInfo"), "planName"));
            return new UsageSnapshot(Id, Kind, DisplayName, Text(Get(status, "email")), plan,
                windows, DateTimeOffset.Now);
        }
        catch (JsonException)
        {
            return Failure(UsageErrorKind.Server, "Antigravity 额度响应格式无法识别");
        }
    }

    private async Task<UsageSnapshot> ReadSummaryAsync(AntigravityLocalEndpoint endpoint, UsageSnapshot fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{SummaryPath}");
            request.Headers.Add("X-Codeium-Csrf-Token", endpoint.CsrfToken);
            request.Headers.Add("Connect-Protocol-Version", "1");
            request.Content = JsonContent.Create(new { metadata = new { ideName = "antigravity", extensionName = "antigravity", locale = "en" } });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                return UsageSnapshot.Failure(Id, Kind, DisplayName, UsageErrorKind.RateLimited,
                    "Antigravity 周额度接口暂时限流", delay, fallback.Windows, fallback.AccountName, fallback.PlanType);
            }
            // Older Antigravity builds do not expose the summary endpoint; keep their known model quota.
            if (!response.IsSuccessStatusCode) return fallback;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            var summary = Get(root, "response");
            if (summary.ValueKind != JsonValueKind.Object) summary = root;
            var groups = Get(summary, "groups");
            if (groups.ValueKind != JsonValueKind.Array) return fallback;
            var windows = new List<QuotaWindow>();
            foreach (var group in groups.EnumerateArray())
            {
                if (Text(Get(group, "displayName"))?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) != true) continue;
                var buckets = Get(group, "buckets");
                if (buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (Get(bucket, "disabled").ValueKind == JsonValueKind.True) continue;
                    var cadence = Text(Get(bucket, "window"))?.Trim().ToLowerInvariant();
                    // Use explicit cadence or documented bucket IDs, never infer a week from reset time.
                    if (string.IsNullOrWhiteSpace(cadence))
                    {
                        var id = Text(Get(bucket, "bucketId")) ?? "";
                        cadence = id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" :
                            id.EndsWith("-5h", StringComparison.OrdinalIgnoreCase) ? "5h" : null;
                    }
                    var isSession = cadence is "5h" or "session" or "5-hour" or "five-hour";
                    var isWeekly = cadence is "weekly" or "7d";
                    if (!isSession && !isWeekly) continue;
                    var fraction = Get(bucket, "remainingFraction");
                    DateTimeOffset? reset = DateTimeOffset.TryParse(Text(Get(bucket, "resetTime")), out var time) ? time : null;
                    double remaining;
                    if (fraction.ValueKind == JsonValueKind.Number && fraction.TryGetDouble(out var value) && double.IsFinite(value))
                        remaining = Math.Clamp(value, 0, 1);
                    else if (fraction.ValueKind == JsonValueKind.Undefined && reset.HasValue)
                        remaining = 0;
                    else continue;
                    windows.Add(new QuotaWindow(isSession ? QuotaWindowKind.SessionShortTerm : QuotaWindowKind.PeriodLongTerm,
                        isSession ? "5h" : "7d", 100 - remaining * 100, reset,
                        isSession ? "Gemini · 5 小时额度" : "Gemini · 一周额度"));
                }
            }
            if (windows.Count == 0) return fallback;
            var ordered = windows.GroupBy(w => w.Kind).Select(g => g.OrderBy(w => w.RemainingPercent).First())
                .OrderBy(w => w.Kind == QuotaWindowKind.SessionShortTerm ? 0 : 1).ToArray();
            return new UsageSnapshot(Id, Kind, DisplayName, fallback.AccountName, fallback.PlanType,
                ordered, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return fallback;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return fallback;
        }
    }

    private UsageSnapshot Failure(UsageErrorKind kind, string message) => UsageSnapshot.Failure(Id, Kind, DisplayName, kind, message);
    private static int Priority(UsageErrorKind kind) => kind switch
    {
        UsageErrorKind.RateLimited => 4, UsageErrorKind.Unauthorized => 3, UsageErrorKind.Server => 2, _ => 1,
    };
    private static JsonElement Get(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
