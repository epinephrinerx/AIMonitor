using AIMonitor.Domain;

namespace AIMonitor.Application.Providers;

/// <summary>Refreshes every registered provider in stable order. Provider failures are isolated;
/// caller cancellation remains exceptional so shutdown can stop before the next provider.</summary>
public sealed class RefreshProvidersUseCase
{
    private const string UnexpectedFailureMessage = "Unexpected provider error. Try again.";
    private readonly IReadOnlyList<ProviderClientRegistration> _providers;

    public RefreshProvidersUseCase(IEnumerable<ProviderClientRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var copy = providers.ToArray();
        var duplicate = copy.GroupBy(item => item.ProviderId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate provider id '{duplicate.Key}'.", nameof(providers));
        }

        _providers = copy;
    }

    public async Task<ProviderRefreshResult> ExecuteAsync(
        long requestId,
        ProviderSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshots = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await provider.Client.GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
                snapshots[provider.ProviderId] = snapshot.ProviderId == provider.ProviderId
                    ? snapshot
                    : new ProviderSnapshot(provider.ProviderId, error: UnexpectedFailureMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                snapshots[provider.ProviderId] = new ProviderSnapshot(provider.ProviderId, error: UnexpectedFailureMessage);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ProviderRefreshResult(requestId, request, snapshots);
    }
}
