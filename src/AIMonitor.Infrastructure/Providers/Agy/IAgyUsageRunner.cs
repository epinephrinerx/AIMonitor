namespace AIMonitor.Infrastructure.Providers.Agy;

/// <summary>
/// Abstraction for invoking the AGY CLI command that produces raw usage JSON.
/// </summary>
public interface IAgyUsageRunner
{
    Task<string> ReadUsageAsync(CancellationToken cancellationToken);
}
