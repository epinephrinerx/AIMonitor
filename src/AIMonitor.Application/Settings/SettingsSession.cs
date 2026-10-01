namespace AIMonitor.Application.Settings;

/// <summary>
/// Thread-safe in-memory single source of truth for application settings.
/// Serializes updates via <see cref="SemaphoreSlim"/>, persists atomically to <see cref="ISettingsStore"/>,
/// and notifies subscribers of changes.
/// </summary>
public sealed class SettingsSession : IDisposable
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;
    private volatile bool _disposed;

    /// <summary>
    /// Gets the current in-memory normalized application settings.
    /// </summary>
    public AppSettings Current => _current;

    /// <summary>
    /// Event raised after an update is successfully persisted to the store and applied to <see cref="Current"/>.
    /// Raised while the update gate is held, so handlers must not synchronously wait on the session
    /// (<see cref="UpdateAsync"/>/<see cref="FlushAsync"/>) and must marshal UI work asynchronously.
    /// </summary>
    public event Action<AppSettings>? Changed;

    /// <summary>
    /// Initializes a new instance of <see cref="SettingsSession"/> with the specified store and initial settings.
    /// </summary>
    public SettingsSession(ISettingsStore store, AppSettings initialSettings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _current = (initialSettings ?? throw new ArgumentNullException(nameof(initialSettings))).Normalize();
    }

    /// <summary>
    /// Asynchronously creates a <see cref="SettingsSession"/> by loading the initial settings from the given store once.
    /// </summary>
    public static async Task<SettingsSession> CreateAsync(ISettingsStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var initial = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new SettingsSession(store, initial);
    }

    /// <summary>
    /// Serializes and executes a settings modification. Applies the change to the latest <see cref="Current"/>,
    /// normalizes the result, persists to the underlying store, updates <see cref="Current"/>, and raises <see cref="Changed"/>.
    /// If the store fails to save, <see cref="Current"/> is not updated and the exception is thrown to the caller.
    /// </summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = change(_current);
            if (updated is null)
            {
                throw new InvalidOperationException("Settings change function must return a non-null AppSettings instance.");
            }

            var normalized = updated.Normalize();
            await _store.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            _current = normalized;
            Changed?.Invoke(normalized);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Safe against Dispose() occurring while an update was in flight.
            }
        }
    }

    /// <summary>
    /// Waits until all currently executing and queued updates have finished.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Safe against Dispose() occurring while flush was in flight.
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
