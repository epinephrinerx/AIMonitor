namespace AIMonitor.Application.Providers;

using AIMonitor.Domain;

/// <summary>
/// A provider quota client decorator that monitors connection changes in <see cref="ProviderConnectionStore"/>
/// and dynamically reconfigures the underlying inner <see cref="IProviderQuotaClient"/> when credentials
/// or extra settings change.
/// </summary>
public sealed class ReconfigurableQuotaClient : IProviderQuotaClient, IDisposable
{
    private readonly ProviderConnectionStore _store;
    private readonly string _providerId;
    private readonly Func<ProviderConnection, IProviderQuotaClient> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IProviderQuotaClient? _currentClient;
    private ProviderConnection? _lastConnection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="ReconfigurableQuotaClient"/>.
    /// </summary>
    public ReconfigurableQuotaClient(
        ProviderConnectionStore store,
        string providerId,
        Func<ProviderConnection, IProviderQuotaClient> factory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _providerId = providerId.Trim();
    }

    /// <inheritdoc/>
    public async Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        IProviderQuotaClient client;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var connection = await _store.GetAsync(_providerId, cancellationToken).ConfigureAwait(false);

            if (_currentClient is null || !Equals(_lastConnection, connection))
            {
                var newClient = _factory(connection)
                    ?? throw new InvalidOperationException($"Factory for provider '{_providerId}' returned null.");
                var oldClient = _currentClient;
                _currentClient = newClient;
                _lastConnection = connection;

                if (oldClient is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            client = _currentClient;
        }
        finally
        {
            _gate.Release();
        }

        return await client.GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;

            if (_currentClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _currentClient = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
