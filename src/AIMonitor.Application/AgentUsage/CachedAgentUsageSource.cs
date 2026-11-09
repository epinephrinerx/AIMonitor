using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// Decorator that caches the last successful <see cref="AgentUsageReport"/> for a minimum interval.
/// Concurrent callers share one in-flight read.
/// </summary>
public sealed class CachedAgentUsageSource : IAgentUsageSource
{
    private readonly IAgentUsageSource _inner;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cache state is one immutable snapshot read as a single reference (R6).
    private CacheSnapshot? _snapshot;

    private sealed record CacheSnapshot(AgentUsageReport Report, DateOnly? Since, DateTimeOffset SuccessAt);

    /// <summary>
    /// Minimum age of the cached report before a new read is attempted.
    /// </summary>
    public TimeSpan MinInterval { get; }

    /// <summary>
    /// The last successful report, if any.
    /// </summary>
    public AgentUsageReport? LastGood => _snapshot?.Report;

    /// <summary>
    /// The last failure, if any.
    /// </summary>
    public AgentUsageUnavailableException? LastError { get; private set; }

    public CachedAgentUsageSource(IAgentUsageSource inner, IClock clock, TimeSpan? minInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        var interval = minInterval ?? TimeSpan.FromMinutes(15);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minInterval));
        MinInterval = interval;
    }

    public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        => ReadAsync(since, force: false, ct);

    public async Task<AgentUsageReport> ReadAsync(DateOnly? since, bool force, CancellationToken ct)
    {
        if (!force && TryUseCached(since, out var cached)) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && TryUseCached(since, out cached)) return cached;

            var report = await _inner.ReadAsync(since, ct).ConfigureAwait(false);
            _snapshot = new CacheSnapshot(report, since, _clock.UtcNow);
            LastError = null;
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is not stored as a LastError.
            throw;
        }
        catch (AgentUsageUnavailableException ex)
        {
            LastError = ex;
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = new AgentUsageUnavailableException(
                AgentUsageErrorKind.Failed,
                "Could not read agent usage. Check ccusage, then refresh.",
                ex);
            LastError = wrapped;
            throw wrapped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryUseCached(DateOnly? since, out AgentUsageReport report)
    {
        report = null!;
        var snapshot = _snapshot;
        if (snapshot is null) return false;
        if (!SameSince(snapshot.Since, since)) return false;

        var age = _clock.UtcNow - snapshot.SuccessAt;
        if (age >= MinInterval) return false;

        report = snapshot.Report;
        return true;
    }

    private static bool SameSince(DateOnly? a, DateOnly? b)
        => a.HasValue == b.HasValue && (!a.HasValue || a.Value == b.GetValueOrDefault());
}
