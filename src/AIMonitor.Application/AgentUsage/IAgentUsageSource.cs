namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// Port for reading a ccusage-style agent usage report.
/// </summary>
public interface IAgentUsageSource
{
    /// <summary>
    /// Reads the report. Implementations may perform I/O or subprocess calls.
    /// </summary>
    /// <param name="since">Optional earliest day to include.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct);
}
