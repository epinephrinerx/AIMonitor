namespace AIMonitor.Application.Settings;

public interface ISettingsStore
{
    bool Exists { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
