using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.Tests;

/// <summary>Settings that live only for the test, so nothing on disk is touched.</summary>
internal sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    private AppSettings _settings = initial ?? new AppSettings();

    public bool Exists => true;

    public int SaveCalls { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        _settings = settings;
        return Task.CompletedTask;
    }
}
