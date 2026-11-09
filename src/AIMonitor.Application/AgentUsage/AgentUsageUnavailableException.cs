namespace AIMonitor.Application.AgentUsage;

/// <summary>
/// Exception representing a known failure when reading agent usage.
/// Messages are fixed and safe; raw process output or paths are never leaked.
/// </summary>
public class AgentUsageUnavailableException : Exception
{
    public AgentUsageErrorKind Kind { get; }

    public AgentUsageUnavailableException(AgentUsageErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public AgentUsageUnavailableException(AgentUsageErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
