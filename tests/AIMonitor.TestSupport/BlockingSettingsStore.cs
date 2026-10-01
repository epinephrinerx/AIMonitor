namespace AIMonitor.TestSupport;

using AIMonitor.Application.Settings;

/// <summary>
/// Reusable test store whose <see cref="SaveAsync"/> can be held on a <see cref="TaskCompletionSource"/>
/// and released by the test, and which records every saved <see cref="AppSettings"/> in order.
/// </summary>
public sealed class BlockingSettingsStore : ISettingsStore
{
    private readonly object _sync = new();
    private readonly List<AppSettings> _savedSettings = new();
    private readonly AppSettings _initialSettings;

    public bool Exists => true;

    /// <summary>
    /// Signaled when <see cref="SaveAsync"/> has been entered and is waiting to be released.
    /// </summary>
    public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Signaled to release the held <see cref="SaveAsync"/> call.
    /// </summary>
    public TaskCompletionSource ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// All settings instances saved through <see cref="SaveAsync"/> in order of completion.
    /// </summary>
    public IReadOnlyList<AppSettings> SavedSettings
    {
        get
        {
            lock (_sync)
            {
                return _savedSettings.ToList();
            }
        }
    }

    public BlockingSettingsStore(AppSettings? initialSettings = null)
    {
        _initialSettings = initialSettings ?? new AppSettings();
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_initialSettings);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveStarted.TrySetResult();
        await ReleaseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _savedSettings.Add(settings);
        }
    }

    /// <summary>
    /// Releases the held <see cref="SaveAsync"/> call.
    /// </summary>
    public void Release() => ReleaseSource.TrySetResult();
}
