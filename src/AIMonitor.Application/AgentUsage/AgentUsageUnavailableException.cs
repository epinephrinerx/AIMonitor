namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// Exception representing a known failure when reading agent usage.
/// Messages are fixed and safe; raw process output or paths are never leaked.
/// </summary>
public class AgentUsageUnavailableException : Exception
{
    public AgentUsageErrorKind Kind { get; }

    public AgentUsageUnavailableException(string message, AgentUsageErrorKind kind = AgentUsageErrorKind.Failed)
        : base(message)
    {
        Kind = kind;
    }

    public AgentUsageUnavailableException(string message, Exception innerException, AgentUsageErrorKind kind = AgentUsageErrorKind.Failed)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
