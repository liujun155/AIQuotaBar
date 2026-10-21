using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;
using Xunit;

namespace AIQuotaBar.Tests;

public class SchedulerTests
{
    private sealed class ControlledProvider(ProviderKind kind, Func<Task<UsageSnapshot>> fetch) : IUsageProvider
    {
        public string Id => kind.ToString();
        public ProviderKind Kind => kind;
        public string DisplayName => Id;
        public bool HasCredentials() => true;
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default) => fetch();
    }

    [Fact]
    public async Task SwitchingProviderWhileRefreshingDoesNotPutCodexResultInGeminiCache()
    {
        var pendingCodex = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codexSnapshot = new UsageSnapshot("codex", ProviderKind.Codex, "Codex", null, null, [], DateTimeOffset.Now);
        var geminiSnapshot = new UsageSnapshot("gemini", ProviderKind.Gemini, "Gemini", null, null, [], DateTimeOffset.Now);
        using var coordinator = new RefreshCoordinator(new AppSettings(),
            [new ControlledProvider(ProviderKind.Codex, () => pendingCodex.Task),
             new ControlledProvider(ProviderKind.Gemini, () => Task.FromResult(geminiSnapshot))], () => { });
        var shown = new List<ProviderKind>();
        coordinator.SnapshotUpdated += snapshot => shown.Add(snapshot.ProviderKind);
        var firstRefresh = coordinator.RefreshNowAsync();
        coordinator.ActiveProviderKind = ProviderKind.Gemini;
        pendingCodex.SetResult(codexSnapshot);
        await firstRefresh;
        await coordinator.RefreshNowAsync();
        Assert.Same(codexSnapshot, coordinator.GetSnapshot(ProviderKind.Codex));
        Assert.Same(geminiSnapshot, coordinator.CurrentSnapshot);
        Assert.DoesNotContain(ProviderKind.Codex, shown);
    }
    private class MockProvider : IUsageProvider
    {
        public string Id => "mock";
        public ProviderKind Kind => ProviderKind.Codex;
        public string DisplayName => "Mock Codex";
        public bool HasCredentials() => true;

        public UsageSnapshot SnapshotToReturn { get; set; } = new(
            "mock",
            ProviderKind.Codex,
            "Mock Codex",
            "mock@user.com",
            "Plus",
            new[]
            {
                new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", 15.0, DateTimeOffset.Now.AddHours(2)),
                new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d", 22.0, DateTimeOffset.Now.AddDays(4)),
            },
            DateTimeOffset.Now);

        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SnapshotToReturn);
        }
    }

    [Fact]
    public async Task RefreshCoordinator_PlansNormalInterval_WhenQuotaIsHigh()
    {
        var settings = new AppSettings
        {
            NormalIntervalSeconds = 40,
            LowQuotaAccelerateEnabled = true,
            LowQuotaThresholdPercent = 20.0,
            AcceleratedIntervalSeconds = 15,
        };

        var provider = new MockProvider();
        // 剩余 85% (高配额)
        provider.SnapshotToReturn = new UsageSnapshot(
            "mock", ProviderKind.Codex, "Mock", null, "Plus",
            new[] { new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", 15.0, DateTimeOffset.Now.AddHours(2)) },
            DateTimeOffset.Now);

        using var coordinator = new RefreshCoordinator(settings, new[] { provider });

        TimeSpan? plannedDelay = null;
        bool? isAccelerated = null;

        coordinator.NextRefreshPlanned += (delay, acc) =>
        {
            plannedDelay = delay;
            isAccelerated = acc;
        };

        await coordinator.RefreshNowAsync();

        Assert.NotNull(plannedDelay);
        Assert.Equal(TimeSpan.FromSeconds(40), plannedDelay.Value);
        Assert.True(isAccelerated.HasValue && !isAccelerated.Value);
    }

    [Fact]
    public async Task RefreshCoordinator_SwitchesToAccelerated_WhenQuotaIsLow()
    {
        var settings = new AppSettings
        {
            NormalIntervalSeconds = 40,
            LowQuotaAccelerateEnabled = true,
            LowQuotaThresholdPercent = 20.0,
            AcceleratedIntervalSeconds = 12,
        };

        var provider = new MockProvider();
        // 剩余 12% (低于 20% 阈值)
        provider.SnapshotToReturn = new UsageSnapshot(
            "mock", ProviderKind.Codex, "Mock", null, "Plus",
            new[] { new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h", 88.0, DateTimeOffset.Now.AddMinutes(20)) },
            DateTimeOffset.Now);

        using var coordinator = new RefreshCoordinator(settings, new[] { provider });

        TimeSpan? plannedDelay = null;
        bool? isAccelerated = null;

        coordinator.NextRefreshPlanned += (delay, acc) =>
        {
            plannedDelay = delay;
            isAccelerated = acc;
        };

        await coordinator.RefreshNowAsync();

        Assert.NotNull(plannedDelay);
        Assert.Equal(TimeSpan.FromSeconds(12), plannedDelay.Value);
        Assert.True(isAccelerated.HasValue && isAccelerated.Value);
    }

    [Fact]
    public async Task RefreshCoordinator_HonorsRetryAfter_WhenRateLimited()
    {
        var settings = new AppSettings
        {
            NormalIntervalSeconds = 30,
            HonorRetryAfter = true,
        };

        var provider = new MockProvider();
        provider.SnapshotToReturn = UsageSnapshot.Failure(
            "mock", ProviderKind.Codex, "Mock",
            UsageErrorKind.RateLimited,
            "429 Too Many Requests",
            TimeSpan.FromSeconds(55));

        using var coordinator = new RefreshCoordinator(settings, new[] { provider });

        TimeSpan? plannedDelay = null;
        bool? isAccelerated = null;

        coordinator.NextRefreshPlanned += (delay, acc) =>
        {
            plannedDelay = delay;
            isAccelerated = acc;
        };

        await coordinator.RefreshNowAsync();

        Assert.NotNull(plannedDelay);
        Assert.Equal(TimeSpan.FromSeconds(55), plannedDelay.Value);
        Assert.True(isAccelerated.HasValue && isAccelerated.Value);
    }
}
