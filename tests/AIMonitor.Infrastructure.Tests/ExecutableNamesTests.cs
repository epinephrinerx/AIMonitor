using AIMonitor.Infrastructure;

namespace AIMonitor.Infrastructure.Tests;

public sealed class ExecutableNamesTests
{
    [Theory]
    [InlineData("agy")]
    [InlineData("codex")]
    public void ACommandIsSpelledTheWayThisOperatingSystemSpellsIt(string command)
    {
        var resolved = ExecutableNames.OnThisPlatform(command);

        // A build that searched for "agy.exe" on a Mac found nothing and then told the user to
        // install a tool they already had.
        Assert.Equal(OperatingSystem.IsWindows() ? command + ".exe" : command, resolved);
    }

    [Fact]
    public void TheSuffixIsAddedOnlyOnce()
    {
        var once = ExecutableNames.OnThisPlatform(ExecutableNames.Agy);

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, once.Split(".exe").Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ACommandWithNoName_IsRefused(string? command)
    {
        Assert.ThrowsAny<ArgumentException>(() => ExecutableNames.OnThisPlatform(command!));
    }
}
