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

    private TrackedClient? _currentTracked;
    private ProviderConnection? _lastConnection;
    private bool _disposed;
    private int _enteringCalls;

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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ReconfigurableQuotaClient));
        }
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _enteringCalls);
        TrackedClient tracked;
        try
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException(nameof(ReconfigurableQuotaClient));
            }

            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ReconfigurableQuotaClient));
                }

                var connection = await _store.GetAsync(_providerId, cancellationToken).ConfigureAwait(false);

                if (_currentTracked is null || !Equals(_lastConnection, connection))
                {
                    var newClient = _factory(connection)
                        ?? throw new InvalidOperationException($"Factory for provider '{_providerId}' returned null.");
                    var oldTracked = _currentTracked;
                    _currentTracked = new TrackedClient(newClient);
                    _lastConnection = connection;

                    oldTracked?.Retire();
                }

                tracked = _currentTracked;
                tracked.AddLease();
            }
            finally
            {
                try
                {
                    _gate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _enteringCalls);
        }

        try
        {
            return await tracked.Client.GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tracked.ReleaseLease();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TrackedClient? toRetire = null;
        try
        {
            _gate.Wait();
            try
            {
                toRetire = _currentTracked;
                _currentTracked = null;
            }
            finally
            {
                try
                {
                    _gate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }

        toRetire?.Retire();

        if (Volatile.Read(ref _enteringCalls) == 0)
        {
            try
            {
                _gate.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class TrackedClient
    {
        private readonly object _sync = new();
        private int _leaseCount;
        private bool _retired;
        private bool _isDisposed;

        public IProviderQuotaClient Client { get; }

        public TrackedClient(IProviderQuotaClient client)
        {
            Client = client;
        }

        public void AddLease()
        {
            lock (_sync)
            {
                _leaseCount++;
            }
        }

        public void ReleaseLease()
        {
            bool shouldDispose = false;
            lock (_sync)
            {
                _leaseCount--;
                if (_leaseCount == 0 && _retired && !_isDisposed)
                {
                    _isDisposed = true;
                    shouldDispose = true;
                }
            }

            if (shouldDispose && Client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public void Retire()
        {
            bool shouldDispose = false;
            lock (_sync)
            {
                _retired = true;
                if (_leaseCount == 0 && !_isDisposed)
                {
                    _isDisposed = true;
                    shouldDispose = true;
                }
            }

            if (shouldDispose && Client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
