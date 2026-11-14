using AIMonitor.Infrastructure;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>Every filesystem/environment access is injected, so these never touch the real machine's
/// <c>PATH</c> or <c>%LOCALAPPDATA%</c>. Expected paths are built with <see cref="Path.Combine(string, string)"/>
/// rather than written as literals, because the locator joins with the host's separator and the
/// search order under test is not Windows-specific.</summary>
public sealed class CodexExecutableLocatorTests
{
    /// <summary>What the tool is called here: Windows adds a suffix, macOS does not.</summary>
    private static readonly string CodexFileName = ExecutableNames.OnThisPlatform(ExecutableNames.Codex);

    [Fact]
    public void Locate_FindsCodexExeOnPath_BeforeCheckingLocalAppData()
    {
        var first = Path.Combine("apps", "first");
        var second = Path.Combine("apps", "second");
        var expected = Path.Combine(second, CodexFileName);

        var result = CodexExecutableLocator.Locate(
            pathDirectories: [first, second],
            fileExists: path => path == expected,
            localAppData: Path.Combine("users", "test", "AppData", "Local"),
            enumerateCodexBinCandidates: _ => throw new InvalidOperationException("must not be reached"),
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Locate_FallsBackToLocalAppDataInstallRoot_WhenNotOnPath()
    {
        var localAppData = Path.Combine("users", "test", "AppData", "Local");
        var installRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        var installed = Path.Combine(installRoot, "1.0.0", CodexFileName);

        var result = CodexExecutableLocator.Locate(
            pathDirectories: [Path.Combine("apps", "first")],
            fileExists: _ => false,
            localAppData: localAppData,
            enumerateCodexBinCandidates: root => root == installRoot ? [installed] : [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(installed, result);
    }

    [Fact]
    public void Locate_PicksTheMostRecentlyModifiedVersionDirectory()
    {
        var older = Path.Combine("bin", "1.0.0", CodexFileName);
        var newer = Path.Combine("bin", "2.0.0", CodexFileName);

        var result = CodexExecutableLocator.Locate(
            pathDirectories: [],
            fileExists: _ => false,
            localAppData: "appdata",
            enumerateCodexBinCandidates: _ => [older, newer],
            lastWriteTimeUtc: path => path == newer ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(newer, result);
    }

    [Fact]
    public void Locate_NotOnPathAndNoLocalAppData_ReturnsNull()
    {
        var result = CodexExecutableLocator.Locate(
            pathDirectories: [Path.Combine("apps", "first")],
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
            localAppData: "appdata",
            enumerateCodexBinCandidates: _ => [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Null(result);
    }

    [Fact]
    public void Locate_SkipsBlankPathEntries()
    {
        var real = Path.Combine("apps", "real");
        var expected = Path.Combine(real, CodexFileName);

        var result = CodexExecutableLocator.Locate(
            pathDirectories: ["", "   ", real],
            fileExists: path => path == expected,
            localAppData: null,
            enumerateCodexBinCandidates: _ => [],
            lastWriteTimeUtc: _ => DateTimeOffset.MinValue);

        Assert.Equal(expected, result);
    }
}
