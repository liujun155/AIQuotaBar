using System.Collections.Concurrent;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.Core.Scheduling;

public sealed class RefreshCoordinator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<ProviderKind, IUsageProvider> _providers = new();
    private readonly ConcurrentDictionary<ProviderKind, UsageSnapshot> _latestSnapshots = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Action _saveSettings;

    private ProviderKind _activeProviderKind;
    private Timer? _timer;
    private int _consecutiveErrors;
    private bool _disposed;

    public RefreshCoordinator(AppSettings settings, IEnumerable<IUsageProvider> providers, Action? saveSettings = null)
    {
        _settings = settings;
        _saveSettings = saveSettings ?? settings.Save;
        _activeProviderKind = settings.ActiveProvider;

        foreach (var p in providers)
        {
            _providers[p.Kind] = p;
        }
    }

    public ProviderKind ActiveProviderKind
    {
        get => _activeProviderKind;
        set
        {
            if (_activeProviderKind != value)
            {
                _activeProviderKind = value;
                _settings.ActiveProvider = value;
                _saveSettings();
                ActiveProviderChanged?.Invoke(value);
                _ = RefreshNowAsync();
            }
        }
    }

    public UsageSnapshot? CurrentSnapshot => _latestSnapshots.TryGetValue(_activeProviderKind, out var s) ? s : null;

    public event Action<ProviderKind>? ActiveProviderChanged;
    public event Action<ProviderKind, bool>? RefreshStateChanged;
    public event Action<UsageSnapshot>? SnapshotUpdated;
    public event Action<TimeSpan, bool>? NextRefreshPlanned; // (interval, isAccelerated)

    public void Start()
    {
        ScheduleNext(TimeSpan.FromMilliseconds(500), false);
    }

    public async Task RefreshNowAsync()
    {
        if (_disposed) return;

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        var requestedKind = _activeProviderKind;
        try
        {
            try { RefreshStateChanged?.Invoke(requestedKind, true); } catch { }

            if (!_providers.TryGetValue(requestedKind, out var provider))
            {
                return;
            }

            var snapshot = await provider.FetchAsync(_cts.Token).ConfigureAwait(false);
            _latestSnapshots[requestedKind] = snapshot;
            if (_activeProviderKind == requestedKind)
            {
                SnapshotUpdated?.Invoke(snapshot);
                PlanNextRefresh(snapshot);
            }
        }
        catch (Exception ex)
        {
            var fallback = UsageSnapshot.Failure(
                requestedKind.ToString().ToLowerInvariant(),
                requestedKind,
                requestedKind.ToString(),
                UsageErrorKind.Unexpected,
                ex.Message);
            _latestSnapshots[requestedKind] = fallback;
            if (_activeProviderKind == requestedKind)
            {
                SnapshotUpdated?.Invoke(fallback);
                PlanNextRefresh(fallback);
            }
        }
        finally
        {
            try { RefreshStateChanged?.Invoke(requestedKind, false); } catch { }
            _refreshLock.Release();
        }
    }

    public UsageSnapshot? GetSnapshot(ProviderKind kind)
    {
        return _latestSnapshots.TryGetValue(kind, out var s) ? s : null;
    }

    public IUsageProvider? GetProvider(ProviderKind kind)
    {
        return _providers.TryGetValue(kind, out var p) ? p : null;
    }

    private void PlanNextRefresh(UsageSnapshot snapshot)
    {
        // 1. 若被限流且要求遵循 Retry-After
        if (snapshot.Error == UsageErrorKind.RateLimited && snapshot.RetryAfter.HasValue && _settings.HonorRetryAfter)
        {
            var wait = snapshot.RetryAfter.Value;
            if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
            ScheduleNext(wait, true);
            return;
        }

        // 2. 网络异常或临时服务器异常：启动自适应快速故障重试 (5s -> 10s -> 15s -> 最多 30s)
        if (snapshot.Error is UsageErrorKind.Network or UsageErrorKind.Unexpected or UsageErrorKind.Server)
        {
            _consecutiveErrors++;
            var retrySeconds = Math.Min(30, Math.Max(5, 5 * _consecutiveErrors));
            ScheduleNext(TimeSpan.FromSeconds(retrySeconds), true);
            return;
        }

        // 成功获取或非瞬态配置错误，重置异常计数器
        _consecutiveErrors = 0;

        // 3. 检查是否触发低额度智能加速刷新
        var minPercent = snapshot.MinRemainingPercent;
        if (_settings.LowQuotaAccelerateEnabled && minPercent < _settings.LowQuotaThresholdPercent)
        {
            var accelerated = TimeSpan.FromSeconds(Math.Max(5, _settings.AcceleratedIntervalSeconds));
            ScheduleNext(accelerated, true);
            return;
        }

        // 4. 正常轮询
        var normal = TimeSpan.FromSeconds(Math.Max(10, _settings.NormalIntervalSeconds));
        ScheduleNext(normal, false);
    }

    private void ScheduleNext(TimeSpan delay, bool isAccelerated)
    {
        if (_disposed) return;

        NextRefreshPlanned?.Invoke(delay, isAccelerated);
        _timer?.Dispose();
        _timer = new Timer(async _ =>
        {
            try
            {
                await RefreshNowAsync().ConfigureAwait(false);
            }
            catch
            {
                if (!_disposed)
                {
                    ScheduleNext(TimeSpan.FromSeconds(10), true);
                }
            }
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}
