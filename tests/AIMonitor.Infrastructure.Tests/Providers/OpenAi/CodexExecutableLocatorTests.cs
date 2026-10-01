using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>Every filesystem/environment access is injected, so these never touch the real machine's
/// <c>PATH</c> or <c>%LOCALAPPDATA%</c>.</summary>
public sealed class CodexExecutableLocatorTests
{
    [Fact]
    public void Locate_FindsCodexExeOnPath_BeforeCheckingLocalAppData()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: [@"C:\first", @"C:\second"],
            fileExists: path => path == @"C:\second\codex.exe",
            localAppData: @"C:\Users\test\AppData\Local",
            enumerateCodexBinCandidates: _ => throw new InvalidOperationException("must not be reached"),
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(@"C:\second\codex.exe", result);
    }

    [Fact]
    public void Locate_FallsBackToLocalAppDataInstallRoot_WhenNotOnPath()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: [@"C:\first"],
            fileExists: _ => false,
            localAppData: @"C:\Users\test\AppData\Local",
            enumerateCodexBinCandidates: root =>
                root == @"C:\Users\test\AppData\Local\OpenAI\Codex\bin"
                    ? [@"C:\Users\test\AppData\Local\OpenAI\Codex\bin\1.0.0\codex.exe"]
                    : [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(@"C:\Users\test\AppData\Local\OpenAI\Codex\bin\1.0.0\codex.exe", result);
    }

    [Fact]
    public void Locate_PicksTheMostRecentlyModifiedVersionDirectory()
    {
        var older = @"C:\bin\1.0.0\codex.exe";
        var newer = @"C:\bin\2.0.0\codex.exe";

        var result = CodexExecutableLocator.Locate(
            pathDirectories: [],
            fileExists: _ => false,
            localAppData: @"C:\appdata",
            enumerateCodexBinCandidates: _ => [older, newer],
            lastWriteTimeUtc: path => path == newer ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(newer, result);
    }

    [Fact]
    public void Locate_NotOnPathAndNoLocalAppData_ReturnsNull()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: [@"C:\first"],
            fileExists: _ => false,
            localAppData: null,
            enumerateCodexBinCandidates: _ => throw new InvalidOperationException("must not be reached"),
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Null(result);
    }

    [Fact]
    public void Locate_NotOnPathAndLocalAppDataHasNoCandidates_ReturnsNull()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: [],
            fileExists: _ => false,
            localAppData: @"C:\appdata",
            enumerateCodexBinCandidates: _ => [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Null(result);
    }

    [Fact]
    public void Locate_SkipsBlankPathEntries()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: ["", "   ", @"C:\real"],
            fileExists: path => path == @"C:\real\codex.exe",
            localAppData: null,
            enumerateCodexBinCandidates: _ => [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(@"C:\real\codex.exe", result);
    }
}
