using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public class PresentationAssemblySmokeTests
{
    [Fact]
    public void PresentationAssembly_Loads()
    {
        var assembly = typeof(App).Assembly;

        Assert.Equal("AIUsageMonitor", assembly.GetName().Name);
    }

    [Fact]
    public void PresentationAssembly_ReferencesApplicationAndInfrastructure()
    {
        var assembly = typeof(App).Assembly;

        Assert.True(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Application"));
        Assert.True(AssemblyReferenceAssertions.References(assembly, "AIMonitor.Infrastructure"));
    }
}
