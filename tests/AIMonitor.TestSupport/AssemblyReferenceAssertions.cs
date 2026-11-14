using System.Reflection;

namespace AIMonitor.TestSupport;

/// <summary>
/// Shared helper for architecture-smoke tests that assert dependency direction between
/// assemblies (see Docs/ARCHITECTURE.md §5) without depending on implementation details.
/// </summary>
public static class AssemblyReferenceAssertions
{
    public static bool References(Assembly assembly, string referencedAssemblySimpleName) =>
        assembly.GetReferencedAssemblies()
            .Any(name => string.Equals(name.Name, referencedAssemblySimpleName, StringComparison.Ordinal));
}
