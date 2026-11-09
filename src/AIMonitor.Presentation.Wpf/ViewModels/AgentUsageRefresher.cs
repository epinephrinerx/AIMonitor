using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// Owns the cadence of ccusage reads for the "All agents" tab. Keeps at most one read in flight,
/// raises <see cref="Updated"/> on the caller's synchronization context, and swallows all exceptions.
/// </summary>
public sealed class AgentUsageRefresher
{
    private readonly CachedAgentUsageSource _source;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _zone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _running;
    private bool _pendingForce;

    public AgentUsageRefresher(CachedAgentUsageSource source, Func<DateTimeOffset> utcNow, TimeZoneInfo zone)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    /// <summary>
    /// Raised after each read attempt with the latest good report and/or the error that occurred.
    /// </summary>
    public event Action<AgentUsageReport?, AgentUsageUnavailableException?>? Updated;

    /// <summary>
    /// Reads agent usage, forcing a fresh read when <paramref name="force"/> is <c>true</c>.
    /// A forced call waits for an in-flight read and then runs once more.
    /// </summary>
    public async Task RefreshAsync(bool force)
    {
        var capturedContext = SynchronizationContext.Current;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running)
            {
                if (force)
                {
                    _pendingForce = true;
                }

                return;
            }

            _running = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            do
            {
                await RunOnceAsync(force, capturedContext).ConfigureAwait(false);

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_pendingForce)
                    {
                        _pendingForce = false;
                        force = true;
                        continue;
                    }

                    _running = false;
                    break;
                }
                finally
                {
                    _gate.Release();
                }
            }
            while (true);
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _running = false;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task RunOnceAsync(bool force, SynchronizationContext? context)
    {
        AgentUsageReport? report = null;
        AgentUsageUnavailableException? error = null;
        try
        {
            var since = ComputeSince();
            report = await _source.ReadAsync(since, force, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentUsageUnavailableException ex)
        {
            error = ex;
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation is not treated as an error; the last good report is still shown.
        }
        catch (Exception ex)
        {
            error = new AgentUsageUnavailableException(
                AgentUsageErrorKind.Failed,
                "Could not read agent usage. Check ccusage, then refresh.",
                ex);
        }

        report ??= _source.LastGood;

        try
        {
            RaiseUpdated(report, error, context);
        }
        catch
        {
            // Handler exceptions must not prevent the refresher from recovering.
        }
    }

    private DateOnly ComputeSince()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(_utcNow().UtcDateTime, _zone);
        var today = DateOnly.FromDateTime(local);
        return new DateOnly(today.Year, today.Month, 1).AddMonths(-2);
    }

    private void RaiseUpdated(
        AgentUsageReport? report,
        AgentUsageUnavailableException? error,
        SynchronizationContext? context)
    {
        var handlers = Updated;
        if (handlers is null)
        {
            return;
        }

        if (context is not null)
        {
            context.Post(
                _ =>
                {
                    try
                    {
                        handlers(report, error);
                    }
                    catch
                    {
                        // Handlers are independent; one throwing must not break the refresher.
                    }
                },
                null);
        }
        else
        {
            try
            {
                handlers(report, error);
            }
            catch
            {
                // Handlers are independent; one throwing must not break the refresher.
            }
        }
    }
}
