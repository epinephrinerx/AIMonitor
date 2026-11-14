using AIMonitor.TestSupport;

namespace AIMonitor.Domain.Tests;

public class DomainAssemblySmokeTests
{
    [Fact]
    public void DomainAssembly_Loads()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.Equal("AIMonitor.Domain", assembly.GetName().Name);
    }

    [Fact]
    public void DomainAssembly_DoesNotReferenceUiOrInfrastructure()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.False(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Application"));
        Assert.False(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Infrastructure"));
        Assert.False(AssemblyReferenceAssertions.References(assembly, "PresentationFramework"));
        Assert.False(AssemblyReferenceAssertions.References(assembly, "System.Net.Http"));
    }
}
