namespace AIMonitor.Application;

/// <summary>Marker type used by tests to obtain this assembly via reflection.</summary>
public sealed class AssemblyMarker
{
    /// <summary>Forces a real compile-time reference to AIMonitor.Domain so dependency-direction smoke tests can observe it.</summary>
    internal static readonly Type DomainReference = typeof(Domain.AssemblyMarker);
}
