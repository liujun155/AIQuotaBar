using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Providers;

public interface IUsageProvider
{
    string Id { get; }
    ProviderKind Kind { get; }
    string DisplayName { get; }
    bool HasCredentials();
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default);
}
