using AIMonitor.Infrastructure.AgentUsage;

namespace AIMonitor.Infrastructure.Tests.AgentUsage;

/// <summary>
/// Where ccusage is looked for on PATH. Each operating system has its own names: the Windows ones
/// on macOS meant ccusage was reported missing on every Mac that had it installed.
/// </summary>
public sealed class CcUsageRunnerExecutableTests
{
    [WindowsOnlyFact("ccusage.exe and the npm ccusage.cmd shim exist only on Windows")]
    public void TheExeIsPreferredOverTheCmdShimInTheSameDirectory()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\npm\ccusage.cmd", @"C:\npm\ccusage.exe" };

        Assert.Equal(@"C:\npm\ccusage.exe", CcUsageRunner.ResolveExecutable(@"C:\npm", files.Contains));
    }

    [WindowsOnlyFact("drive-relative paths such as C:dir exist only on Windows")]
    public void DriveRelativeEntriesAreSkipped()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:npm\ccusage.cmd", @"D:\real\ccusage.cmd" };

        Assert.Equal(@"D:\real\ccusage.cmd", CcUsageRunner.ResolveExecutable(@"C:npm;D:\real", files.Contains));
    }

    [MacOSOnlyFact("the plain ccusage name is what npm links on macOS")]
    public void APlainCcusageOnPathIsFound()
    {
        var files = new HashSet<string> { "/opt/homebrew/bin/ccusage" };

        Assert.Equal("/opt/homebrew/bin/ccusage", CcUsageRunner.ResolveExecutable("/usr/bin:/opt/homebrew/bin", files.Contains));
    }

    [MacOSOnlyFact("macOS has no .exe or .cmd; a Windows-named file must not count as ccusage")]
    public void WindowsNamedFilesAreNotCcusage()
    {
        var files = new HashSet<string> { "/opt/homebrew/bin/ccusage.exe", "/opt/homebrew/bin/ccusage.cmd" };

        Assert.Null(CcUsageRunner.ResolveExecutable("/opt/homebrew/bin", files.Contains));
    }

    [MacOSOnlyFact("POSIX PATH entries are what macOS uses")]
    public void RelativeEntriesAreSkipped()
    {
        var files = new HashSet<string> { "bin/ccusage", "/real/bin/ccusage" };

        Assert.Equal("/real/bin/ccusage", CcUsageRunner.ResolveExecutable("bin:/real/bin", files.Contains));
    }

    [Fact]
    public void NothingOnPathMeansNotFound()
    {
        Assert.Null(CcUsageRunner.ResolveExecutable(null, _ => false));
        Assert.Null(CcUsageRunner.ResolveExecutable("", _ => true));
    }
}
