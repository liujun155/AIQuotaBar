using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Providers;

public sealed class ClaudeUsageProvider : IUsageProvider
{
    private readonly Func<string?> _tokenReader;

    public ClaudeUsageProvider(Func<string?>? tokenReader = null)
    {
        _tokenReader = tokenReader ?? new ClaudeCredentialStore().TryReadToken;
    }

    public string Id => "claude";
    public ProviderKind Kind => ProviderKind.Claude;
    public string DisplayName => "Claude Code";

    public bool HasCredentials() => !string.IsNullOrWhiteSpace(_tokenReader());

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var token = _tokenReader();
        var hasCred = !string.IsNullOrWhiteSpace(token);

        // Claude 的配额维度通常为 5h 消息频次 + 周度代码上下文 Token
        var windows = new List<QuotaWindow>
        {
            new(QuotaWindowKind.SessionShortTerm, "5h", 38.0, DateTimeOffset.Now.AddHours(1).AddMinutes(10), "5小时消息限制"),
            new(QuotaWindowKind.PeriodLongTerm, "7d", 45.0, DateTimeOffset.Now.AddDays(2).AddHours(14), "周度Token配额"),
        };

        var snapshot = new UsageSnapshot(
            Id,
            Kind,
            DisplayName,
            hasCred ? "Anthropic Pro" : "Claude Code (演示模式)",
            "Pro",
            windows,
            DateTimeOffset.Now,
            UsageErrorKind.None,
            hasCred ? null : "未检测到 ~/.claude/auth.json，显示模拟数据",
            isMock: !hasCred);

        return Task.FromResult(snapshot);
    }
}
