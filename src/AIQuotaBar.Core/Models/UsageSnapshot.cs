namespace AIQuotaBar.Core.Models;

public sealed class UsageSnapshot
{
    public UsageSnapshot(
        string providerId,
        ProviderKind providerKind,
        string displayName,
        string? accountName,
        string? planType,
        IReadOnlyList<QuotaWindow> windows,
        DateTimeOffset fetchedAt,
        UsageErrorKind error = UsageErrorKind.None,
        string? errorMessage = null,
        TimeSpan? retryAfter = null,
        bool isMock = false)
    {
        ProviderId = providerId;
        ProviderKind = providerKind;
        DisplayName = displayName;
        AccountName = accountName;
        PlanType = planType;
        Windows = windows;
        FetchedAt = fetchedAt;
        Error = error;
        ErrorMessage = errorMessage;
        RetryAfter = retryAfter;
        IsMock = isMock;
    }

    public string ProviderId { get; }
    public ProviderKind ProviderKind { get; }
    public string DisplayName { get; }
    public string? AccountName { get; }
    public string? PlanType { get; }
    public IReadOnlyList<QuotaWindow> Windows { get; }
    public DateTimeOffset FetchedAt { get; }
    public UsageErrorKind Error { get; }
    public string? ErrorMessage { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsMock { get; }

    public bool IsOk => Error == UsageErrorKind.None;

    public QuotaWindow? PrimaryWindow => Windows.Count > 0 ? Windows[0] : null;
    public QuotaWindow? SecondaryWindow => Windows.Count > 1 ? Windows[1] : null;

    public double MinRemainingPercent
    {
        get
        {
            if (Windows.Count == 0) return 100.0;
            double min = 100.0;
            foreach (var w in Windows)
            {
                if (w.RemainingPercent < min)
                {
                    min = w.RemainingPercent;
                }
            }
            return min;
        }
    }

    public static UsageSnapshot Failure(
        string providerId,
        ProviderKind kind,
        string displayName,
        UsageErrorKind error,
        string? errorMessage = null,
        TimeSpan? retryAfter = null,
        IReadOnlyList<QuotaWindow>? fallbackWindows = null,
        string? accountName = null,
        string? planType = null)
    {
        return new UsageSnapshot(
            providerId,
            kind,
            displayName,
            accountName,
            planType,
            fallbackWindows ?? Array.Empty<QuotaWindow>(),
            DateTimeOffset.Now,
            error,
            errorMessage,
            retryAfter);
    }
}
