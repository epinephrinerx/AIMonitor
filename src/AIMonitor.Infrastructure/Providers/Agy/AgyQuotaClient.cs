using AIMonitor.Application.Providers;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Agy;

/// <summary>
/// Quota client adapter for Google Antigravity (AGY) CLI usage and quota reporting.
/// </summary>
public sealed class AgyQuotaClient : IProviderQuotaClient
{
    public const string ProviderId = "agy";
    public const string ValueNote = "AGY shared model-pool quotas; used percentage. Token, cost and history data are not reported.";
    public const string SourceId = "agy_cli";
    public const string SourceLabel = "AGY CLI";
    public const string SetupHint = "Install or sign in to AGY CLI, then refresh.";
    public const string ActionableError = "AGY CLI is not available or not signed in. Install and sign in to AGY, then refresh.";

    private readonly IAgyUsageRunner _runner;
    private readonly IClock _clock;

    public AgyQuotaClient(IAgyUsageRunner runner, IClock clock)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(
        ProviderSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string output = await _runner.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Meter> meters = AgyUsageMapper.MapUsage(output);

            return new ProviderSnapshot(
                providerId: ProviderId,
                configured: true,
                meters: meters,
                valueNote: ValueNote,
                fetchedAt: _clock.UtcNow,
                detection: new DetectionInfo(
                    providerId: ProviderId,
                    state: DetectionState.Connected,
                    sourceId: SourceId,
                    sourceLabel: SourceLabel));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgyUsageException ex)
        {
            var state = ex.Kind is AgyErrorKind.RateLimited or AgyErrorKind.Timeout or AgyErrorKind.InvalidResponse
                ? DetectionState.Limited
                : DetectionState.NotConnected;

            string safeError = ex.Kind switch
            {
                AgyErrorKind.NotInstalled => "AGY CLI was not found. Add agy.exe to PATH and restart AIMonitor.",
                AgyErrorKind.Timeout => "AGY usage timed out or exceeded its output limit. Check AGY, then refresh.",
                AgyErrorKind.InvalidResponse => "AGY returned no usable quota data. Check AGY /usage or update AGY, then refresh.",
                AgyErrorKind.RateLimited => "AGY usage checks are temporarily limited. Try again later.",
                _ => ActionableError
            };

            return CreateFailureSnapshot(safeError, state);
        }
        catch (Exception)
        {
            // Unexpected runner exception message is never echoed.
            return CreateFailureSnapshot(ActionableError, DetectionState.NotConnected);
        }
    }

    private static ProviderSnapshot CreateFailureSnapshot(string errorMessage, DetectionState state)
    {
        return new ProviderSnapshot(
            providerId: ProviderId,
            configured: state == DetectionState.Limited,
            error: errorMessage,
            setupHint: SetupHint,
            detection: new DetectionInfo(
                providerId: ProviderId,
                state: state,
                sourceId: SourceId,
                sourceLabel: SourceLabel,
                hint: SetupHint));
    }
}
