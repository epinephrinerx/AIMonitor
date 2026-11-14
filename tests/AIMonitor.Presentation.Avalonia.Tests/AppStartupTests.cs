namespace AIMonitor.Presentation.Avalonia.Tests;

public sealed class AppStartupTests
{
    [Theory]
    [InlineData("--minimized")]
    [InlineData("--tray")]
    [InlineData("-m")]
    public void TheDocumentedSwitchesAskForAHiddenStart(string argument)
    {
        Assert.True(App.WantsHiddenStart([argument]));
    }

    [Fact]
    public void AHiddenStartIsAskedForEvenWhenTheSwitchIsNotFirst()
    {
        Assert.True(App.WantsHiddenStart(["--open=usage log", "--tray"]));
    }

    [Theory]
    [InlineData]
    [InlineData("--open=usage log")]
    [InlineData("-minimized")]
    [InlineData("--Tray")]
    public void AnythingElseStartsVisible(params string[] arguments)
    {
        Assert.False(App.WantsHiddenStart(arguments));
    }
}
