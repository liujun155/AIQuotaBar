namespace AIQuotaBar.Core.Models;

public enum ProviderKind
{
    Codex,
    Claude,
    Gemini,
}

public enum UsageErrorKind
{
    None = 0,
    NoCredentials,
    Unauthorized,
    RateLimited,
    Network,
    Server,
    Unexpected,
}

public enum QuotaWindowKind
{
    SessionShortTerm, // 比如 Codex 5h, Claude 5h
    PeriodLongTerm,    // 比如 Codex 7d, Gemini 1d
    RateLimitMinute,   // 比如 Gemini RPM
    ModelQuota,        // Antigravity 按模型提供的额度，不能假定为 RPM/RPD 或固定时间窗口
}

public sealed class QuotaWindow
{
    public QuotaWindow(QuotaWindowKind kind, string label, double usedPercent, DateTimeOffset? resetAt, string? description = null)
    {
        Kind = kind;
        Label = label;
        UsedPercent = Math.Clamp(usedPercent, 0, 100);
        ResetAt = resetAt;
        Description = description;
    }

    public QuotaWindowKind Kind { get; }
    public string Label { get; }
    public double UsedPercent { get; }
    public double RemainingPercent => Math.Clamp(100.0 - UsedPercent, 0, 100.0);
    public DateTimeOffset? ResetAt { get; }
    public string? Description { get; }

    public TimeSpan? TimeUntilReset => ResetAt.HasValue ? ResetAt.Value - DateTimeOffset.Now : null;

    public string FormatResetCountdown()
    {
        if (!ResetAt.HasValue)
        {
            return "--";
        }

        var remaining = ResetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "即将重置";
        }

        if (remaining.TotalHours >= 24)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours:D2}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours:D2}h {remaining.Minutes:D2}m";
        }

        return $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s";
    }
}
