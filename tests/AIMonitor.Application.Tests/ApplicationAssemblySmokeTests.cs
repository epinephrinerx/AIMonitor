using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests;

public class ApplicationAssemblySmokeTests
{
    [Fact]
    public void ApplicationAssembly_Loads()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.Equal("AIMonitor.Application", assembly.GetName().Name);
    }

    [Fact]
    public void ApplicationAssembly_ReferencesDomainOnly()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        Assert.True(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Domain"));
        Assert.False(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Infrastructure"));
        Assert.False(AssemblyReferenceAssertions.References(assembly, "PresentationFramework"));
    }
}
