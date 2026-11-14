namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// Categories of failure when reading agent usage.
/// </summary>
public enum AgentUsageErrorKind
{
    NotInstalled,
    Timeout,
    OutputTooLarge,
    Failed,
    InvalidResponse,
}
