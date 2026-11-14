using AIMonitor.Domain;

namespace AIMonitor.Application.Providers;

/// <summary>
/// Application's port for "fetch one provider's current quota". Infrastructure implements this per
/// provider (HTTP client, process client, or whatever that provider needs); nothing above this
/// boundary knows how the snapshot was produced.
/// </summary>
public interface IProviderQuotaClient
{
    /// <summary>
    /// Produces a <see cref="ProviderSnapshot"/> for <paramref name="request"/>. Never throws for an
    /// expected failure (missing/expired credential, network/parse error) — those become an error
    /// snapshot instead. The only exception a caller should expect is
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled.
    /// </summary>
    Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken);
}
