namespace AIMonitor.Application.Settings;

public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string name, CancellationToken cancellationToken = default);
}
