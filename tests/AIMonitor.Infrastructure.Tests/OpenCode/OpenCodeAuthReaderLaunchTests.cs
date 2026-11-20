using System.Text;
using AIMonitor.Infrastructure.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// R-020 F2 (Docs/SPEC_R020_R021.md): executable resolution "exactly like CcUsageRunner.ResolveExecutable"
/// (rooted, non-drive-relative PATH entries in order; opencode.exe before opencode.cmd in each directory;
/// not found is Installed=false with no error) and the launch "exactly like CcUsageRunner.BuildStartInfo"
/// (full path as file name, UseShellExecute=false, CreateNoWindow=true, all three streams redirected,
/// UTF-8 no BOM + throw on invalid, NO_COLOR=1, arguments only as fixed ArgumentList tokens, no cmd.exe).
/// </summary>
public sealed class OpenCodeAuthReaderLaunchTests
{
    [Fact]
    public async Task NotOnPath_IsNotInstalled_WithoutErrorOrProcessOrFileRead_F2_F5()
    {
        // F2: not found -> Installed=false, no error. F5: "not on PATH -> Installed=false".
        var h = new ReaderHarness();
        h.Env["PATH"] = @"C:\empty;D:\also-empty";
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.SetAuthJson("""{"anthropic":{"type":"oauth"}}"""); // must not be read when OpenCode is absent

        var status = await h.ReadAsync();

        Assert.False(status.Installed);
        Assert.Empty(status.SignIns);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.False(status.MayBeOutdated);
        Assert.Empty(h.Starts);
        Assert.Empty(h.AccessedPaths);
    }

    [Fact]
    public async Task NoPathVariable_IsNotInstalled_AndDoesNotThrow_F2()
    {
        var h = new ReaderHarness();
        h.Env.Remove("PATH");

        var status = await h.ReadAsync();

        Assert.False(status.Installed);
        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Empty(h.Starts);
    }

    [WindowsOnlyFact("opencode.exe and the npm opencode.cmd shim exist only on Windows; macOS looks for a plain opencode")]
    public async Task ExeIsPreferredOverCmdInTheSameDirectory_F2()
    {
        var h = new ReaderHarness();
        h.AddFile(@"C:\tools\bin\opencode.cmd", "");

        await h.ReadAsync();

        Assert.NotEmpty(h.Starts);
        Assert.All(h.Starts, s => Assert.Equal(@"C:\tools\bin\opencode.exe", s.FileName, ignoreCase: true));
    }

    [WindowsOnlyFact("opencode.exe and the npm opencode.cmd shim exist only on Windows; macOS looks for a plain opencode")]
    public async Task PathOrderBeatsExtension_ACmdInAnEarlierDirectoryWinsOverALaterExe_F2()
    {
        // "rooted, non-drive-relative PATH entries in order; opencode.exe before opencode.cmd in EACH directory".
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = @"C:\first;C:\second";
        h.AddFile(@"C:\first\opencode.cmd", "");
        h.AddFile(@"C:\second\opencode.exe", "");

        await h.ReadAsync();

        Assert.All(h.Starts, s => Assert.Equal(@"C:\first\opencode.cmd", s.FileName, ignoreCase: true));
    }

    [WindowsOnlyFact("drive-relative paths such as C:dir exist only on Windows; the macOS rule is covered by RelativePathEntriesAreSkipped_OnMacOS")]
    public async Task RelativeAndDriveRelativePathEntriesAreSkipped_F2()
    {
        // Same rule as CcUsageRunner: only rooted, non-drive-relative entries are considered, even when the
        // fake file system would say the file exists there.
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = @"relative\dir;C:driverelative;C:\real\bin";
        h.AddFile(@"relative\dir\opencode.exe", "");
        h.AddFile(@"C:driverelative\opencode.exe", "");
        h.AddFile(@"C:\real\bin\opencode.exe", "");

        await h.ReadAsync();

        Assert.All(h.Starts, s => Assert.Equal(@"C:\real\bin\opencode.exe", s.FileName, ignoreCase: true));
    }

    [Fact]
    public async Task OnlyRelativePathEntries_MeansNotInstalled_F2()
    {
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = @"relative\dir;C:driverelative";
        h.AddFile(@"relative\dir\opencode.exe", "");
        h.AddFile(@"C:driverelative\opencode.exe", "");

        var status = await h.ReadAsync();

        Assert.False(status.Installed);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public async Task QuotedPathEntryWithSpaces_IsTrimmedAndResolved_F2()
    {
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        var directory = OperatingSystem.IsWindows() ? @"C:\Program Files\Open Code" : "/Applications/Open Code/bin";
        var executable = Path.Combine(directory, OpenCodeAuthReader.ExecutableFileNames[0]);
        h.Env["PATH"] = $"\"{directory}\"";
        h.AddFile(executable, "");

        await h.ReadAsync();

        Assert.NotEmpty(h.Starts);
        Assert.All(h.Starts, s => Assert.Equal(executable, s.FileName, ignoreCase: true));
    }

    [WindowsOnlyFact("the npm .cmd shim and cmd.exe exist only on Windows")]
    public async Task CmdShimAtAPathWithSpaces_IsLaunchedDirectlyByFullPath_NoCmdExe_NoQuotesInArguments_F2()
    {
        // F2 + the ".cmd launch check": new ProcessStartInfo(fullPath), UseShellExecute=false, ArgumentList only.
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = @"C:\Users\me\AppData\Roaming\npm bin";
        h.AddFile(@"C:\Users\me\AppData\Roaming\npm bin\opencode.cmd", "");
        h.UseTwoX("[]");

        await h.ReadAsync();

        Assert.Equal(2, h.Starts.Count);
        foreach (var start in h.Starts)
        {
            Assert.Equal(@"C:\Users\me\AppData\Roaming\npm bin\opencode.cmd", start.FileName, ignoreCase: true);
            Assert.False(start.UseShellExecute);
            Assert.DoesNotContain("cmd.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cmd.exe", start.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/c", start.ArgumentList);
            Assert.All(start.ArgumentList, a => Assert.DoesNotContain("\"", a, StringComparison.Ordinal));
            Assert.Equal("", start.Arguments); // arguments only through ArgumentList (no string concatenation)
        }
    }

    [Fact]
    public async Task VersionThenAuthList_UseExactlyTheFixedTokens_F2()
    {
        var h = new ReaderHarness();
        h.UseTwoX("[]");

        await h.ReadAsync();

        Assert.Equal(2, h.Starts.Count);
        Assert.Equal(new[] { "--version" }, h.Starts[0].ArgumentList);
        Assert.Equal(new[] { "auth", "list", "--format", "json" }, h.Starts[1].ArgumentList);
    }

    [Fact]
    public async Task OneX_StartsOnlyTheVersionCommand_NeverAuthList_F2_F5()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.SetAuthJson("{}");

        await h.ReadAsync();

        Assert.Single(h.Starts);
        Assert.Equal(new[] { "--version" }, h.Starts[0].ArgumentList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EveryStartInfo_HiddenShellFree_Redirected_Utf8_NoColor_F2(int index)
    {
        var h = new ReaderHarness();
        h.UseTwoX("[]");

        await h.ReadAsync();

        var start = h.Starts[index];
        Assert.Equal(ReaderHarness.DefaultExe, start.FileName, ignoreCase: true);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal("1", start.Environment["NO_COLOR"]);

        foreach (var encoding in new[] { start.StandardOutputEncoding, start.StandardErrorEncoding })
        {
            var utf8 = Assert.IsType<UTF8Encoding>(encoding);
            Assert.Empty(utf8.GetPreamble()); // no BOM
            Assert.Throws<DecoderFallbackException>(() => utf8.GetString([0xFF, 0xFE, 0xFD])); // throw on invalid
        }
    }

    [Fact]
    public async Task EveryStartedProcessIsDisposed_F2()
    {
        var h = new ReaderHarness();
        h.UseTwoX("""[{"id":"anthropic","name":"Anthropic","connections":[]}]""");

        await h.ReadAsync();

        Assert.Equal(2, h.Processes.Count);
        Assert.All(h.Processes, p => Assert.True(p.Disposed));
    }

    [Fact]
    public async Task AuthJsonIsNotReadAtAll_WhenTwoXAuthListSucceeds_F5()
    {
        // Major >= 2 and the command succeeds -> F3 only; auth.json (a stale legacy import) is not consulted.
        var h = new ReaderHarness();
        h.SetAuthJson("""{"legacy":{"type":"oauth"}}""");
        h.UseTwoX("""[{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","id":"cred_1","label":"x","method":"oauth"}]}]""");

        var status = await h.ReadAsync();

        Assert.Empty(h.AccessedPaths);
        Assert.Equal(new[] { "Anthropic|OAuth" }, ReaderHarness.Pairs(status));
    }

    [MacOSOnlyFact("the plain opencode name and POSIX PATH entries are what macOS uses")]
    public async Task APlainOpencodeOnPath_IsFound_OnMacOS()
    {
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = "/usr/bin:/opt/homebrew/bin";
        h.AddFile("/opt/homebrew/bin/opencode", "");

        var status = await h.ReadAsync();

        Assert.True(status.Installed);
        Assert.NotEmpty(h.Starts);
        Assert.All(h.Starts, s => Assert.Equal("/opt/homebrew/bin/opencode", s.FileName));
    }

    [MacOSOnlyFact("macOS has no .exe or .cmd; a Windows-named file must not count as OpenCode")]
    public async Task WindowsNamedFiles_AreNotOpenCode_OnMacOS()
    {
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = "/opt/homebrew/bin";
        h.AddFile("/opt/homebrew/bin/opencode.exe", "");
        h.AddFile("/opt/homebrew/bin/opencode.cmd", "");

        var status = await h.ReadAsync();

        Assert.False(status.Installed);
        Assert.Empty(h.Starts);
    }

    [MacOSOnlyFact("POSIX PATH entries are what macOS uses")]
    public async Task RelativePathEntriesAreSkipped_OnMacOS()
    {
        var h = new ReaderHarness();
        h.RemoveFile(ReaderHarness.DefaultExe);
        h.Env["PATH"] = "relative/dir:./bin:/real/bin";
        h.AddFile("relative/dir/opencode", "");
        h.AddFile("./bin/opencode", "");
        h.AddFile("/real/bin/opencode", "");

        await h.ReadAsync();

        Assert.NotEmpty(h.Starts);
        Assert.All(h.Starts, s => Assert.Equal("/real/bin/opencode", s.FileName));
    }

    [MacOSOnlyFact("macOS names the home directory HOME, not USERPROFILE")]
    public async Task AuthJsonIsReadFromHome_OnMacOS()
    {
        var h = new ReaderHarness();
        h.SetVersion("1.19.5");
        h.Env.Remove("USERPROFILE");
        h.Env["HOME"] = "/Users/someone";
        h.AddFile("/Users/someone/.local/share/opencode/auth.json", """{"anthropic":{"type":"oauth"}}""");

        var status = await h.ReadAsync();

        Assert.True(string.IsNullOrEmpty(status.Error));
        Assert.Equal(new[] { "/Users/someone/.local/share/opencode/auth.json" }, h.AccessedPaths);
    }
}
