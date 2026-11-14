namespace AIMonitor.TestSupport;

using AIMonitor.Application.Settings;

/// <summary>
/// Controllable in-memory implementation of <see cref="ISecretStore"/> for tests.
/// Maintains an actual dictionary of stored secrets, but allows tests to inject faults
/// on Get, Set, or Remove operations on demand.
/// </summary>
public sealed class TestSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets;

    /// <summary>
    /// If non-null, <see cref="GetAsync"/> will throw this exception.
    /// </summary>
    public Exception? FailOnGet { get; set; }

    /// <summary>
    /// If non-null, <see cref="SetAsync"/> will throw this exception.
    /// </summary>
    public Exception? FailOnSet { get; set; }

    /// <summary>
    /// If non-null, <see cref="RemoveAsync"/> will throw this exception.
    /// </summary>
    public Exception? FailOnRemove { get; set; }

    /// <summary>
    /// Optional hook invoked whenever <see cref="SetAsync"/> is called.
    /// </summary>
    public Action<string, string>? OnSet { get; set; }

    /// <summary>
    /// Optional hook invoked whenever <see cref="RemoveAsync"/> is called.
    /// </summary>
    public Action<string>? OnRemove { get; set; }

    /// <summary>
    /// Number of times <see cref="GetAsync"/> was called.
    /// </summary>
    public int GetCalls { get; private set; }

    /// <summary>
    /// Number of times <see cref="SetAsync"/> was called.
    /// </summary>
    public int SetCalls { get; private set; }

    /// <summary>
    /// Number of times <see cref="RemoveAsync"/> was called.
    /// </summary>
    public int RemoveCalls { get; private set; }

    /// <summary>
    /// Read-only snapshot of all currently stored secrets.
    /// </summary>
    public IReadOnlyDictionary<string, string> Secrets => _secrets;

    /// <summary>
    /// Initializes a new instance of <see cref="TestSecretStore"/>.
    /// </summary>
    public TestSecretStore(IDictionary<string, string>? initial = null)
    {
        _secrets = initial is not null
            ? new Dictionary<string, string>(initial, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCalls++;

        if (FailOnGet is not null)
        {
            return Task.FromException<string?>(FailOnGet);
        }

        return Task.FromResult(_secrets.TryGetValue(name, out var val) ? val : null);
    }

    /// <inheritdoc/>
    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCalls++;

        if (FailOnSet is not null)
        {
            return Task.FromException(FailOnSet);
        }

        _secrets[name] = value;
        OnSet?.Invoke(name, value);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveCalls++;

        if (FailOnRemove is not null)
        {
            return Task.FromException(FailOnRemove);
        }

        _secrets.Remove(name);
        OnRemove?.Invoke(name);
        return Task.CompletedTask;
    }
}
