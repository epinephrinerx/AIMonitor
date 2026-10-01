using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests;

public class InfrastructureAssemblySmokeTests
{
    [Fact]
    public void InfrastructureAssembly_Loads()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.Equal("AIMonitor.Infrastructure", assembly.GetName().Name);
    }

    [Fact]
    public void InfrastructureAssembly_ReferencesApplicationAndDomain()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.True(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Application"));
        Assert.True(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Domain"));
    }
}
