using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class SupplementaryRegressionTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    public void UnconfiguredSnapshot_HidesSupplementary(string provider)
    {
        var vm = new ProviderTabViewModel(provider, provider);
        vm.UpdateFromSnapshot(new ProviderSnapshot(provider, configured: false));
        Assert.False(vm.ShowSupplementary);
    }

    [Fact]
    public void MissingClaudePlan_IsNotInvented()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", true, meters: [new Meter("session", "session", "Session", "5-hour window", 10)]));
        Assert.Equal("Not reported", Assert.Single(vm.SupplementaryRows, r => r.Label == "Plan").Value);
    }

    [Fact]
    public void Refresh_PreservesExpandedDetails_AndClearsMetadata()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", true, stats: [new Stat("Plan", "Plus")]));
        vm.Supplementary!.IsResetDetailsExpanded = true;
        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", true));
        Assert.True(vm.Supplementary!.IsResetDetailsExpanded);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Value == "Plus");
    }

    [Fact]
    public void OtherProviderStats_AreNotFiltered()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        vm.UpdateFromSnapshot(new ProviderSnapshot("gemini", true, stats: [new Stat("Plan", "Reported tier")]));
        Assert.Equal("Reported tier", Assert.Single(vm.StatTiles).Value);
    }

    [Fact]
    public void ApiMode_DoesNotClaimCodexTelemetry()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", true,
            detection: new DetectionInfo("openai", DetectionState.Connected, "manual", "Admin key")));
        Assert.Equal("Not available in API mode", vm.ResetBadge);
        Assert.Equal("Account activity", vm.ActivityTitle);
    }
}
