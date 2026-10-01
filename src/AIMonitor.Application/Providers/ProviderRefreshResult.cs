using System.Collections.ObjectModel;
using AIMonitor.Domain;

namespace AIMonitor.Application.Providers;

public sealed record ProviderRefreshResult
{
    public long RequestId { get; }
    public ProviderSnapshotRequest Request { get; }
    public IReadOnlyDictionary<string, ProviderSnapshot> Snapshots { get; }

    public ProviderRefreshResult(
        long requestId,
        ProviderSnapshotRequest request,
        IReadOnlyDictionary<string, ProviderSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshots);
        RequestId = requestId;
        Request = request;
        Snapshots = new ReadOnlyDictionary<string, ProviderSnapshot>(
            new Dictionary<string, ProviderSnapshot>(snapshots, StringComparer.Ordinal));
    }
}
