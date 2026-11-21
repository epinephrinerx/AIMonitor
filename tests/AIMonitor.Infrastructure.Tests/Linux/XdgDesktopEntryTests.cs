using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The pure Desktop Entry rules (R-024). Every expected value is the raw text the file must
/// contain, written as a verbatim string so the backslashes and quotes read as they
/// appear in the file. Nothing here touches the disk, so it runs on every operating system.
/// </summary>
public sealed class XdgDesktopEntryTests
{
    private const string BuiltForOptX =
        "[Desktop Entry]\nType=Application\nName=AI Usage Monitor\nComment=Start AI Usage Monitor when you log in\nExec=/opt/x/aim\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    [Fact]
    public void ApplicationName_IsTheNameTheDesktopShowsInItsStartupList()
    {
        Assert.Equal("AI Usage Monitor", XdgDesktopEntry.ApplicationName);
    }

    // ---- FormatExec: the worked examples -------------------------------------------------------

    [Theory]
    [InlineData(@"/home/u/apps/aim/AIMonitor.Presentation.Avalonia", "", @"/home/u/apps/aim/AIMonitor.Presentation.Avalonia")]
    [InlineData(@"/opt/AI Monitor/aim", "--minimized", @"""/opt/AI Monitor/aim"" --minimized")]
    [InlineData(@" ""/opt/AI Monitor/aim"" ", null, @"""/opt/AI Monitor/aim""")]
    [InlineData(@"/opt/a$b/aim", "", @"""/opt/a\\$b/aim""")]
    [InlineData(@"/opt/back\slash/aim", "", @"""/opt/back\\\\slash/aim""")] // one backslash in, four out
    [InlineData(@"/opt/q""uote/aim", "", @"""/opt/q\\""uote/aim""")]
    [InlineData(@"/opt/100%/aim", "", @"/opt/100%%/aim")]
    [InlineData(@"/opt/it's/aim", "", @"""/opt/it's/aim""")]
    [InlineData(@"/opt/50% off/aim", "", @"""/opt/50%% off/aim""")]
    [InlineData(@"/opt/x/aim", "  --tray   --minimized ", @"/opt/x/aim --tray --minimized")]
    [InlineData(@"/opt/x~y/aim", "", @"""/opt/x~y/aim""")]
    [InlineData(@"/opt/x/aim", "--open=a=b", @"/opt/x/aim --open=a=b")]
    [InlineData(@"/opt/x/aim", "--name=$HOME", @"/opt/x/aim ""--name=\\$HOME""")]
    [InlineData("/opt/back`tick/aim", "", @"""/opt/back\\`tick/aim""")]
    [InlineData(@"/opt/a=b/aim", "", @"/opt/a=b/aim")]
    [InlineData(@"/opt/x/aim", @"--x ""a b""", @"/opt/x/aim --x ""\\""a"" ""b\\""""")] // no quoting dialect, split at the space
    [InlineData(@"/opt/x/aim", @"""""", @"/opt/x/aim ""\\""\\""""")]
    [InlineData(@"/opt/x/aim""", "", @"/opt/x/aim")] // a trailing literal quote is trimmed away
    public void FormatExec_QuotesAndEscapesAsTheSpecTableSays(string executablePath, string? arguments, string expected)
    {
        Assert.Equal(expected, XdgDesktopEntry.FormatExec(executablePath, arguments));
    }

    [Theory]
    [InlineData("relative/aim", "")]
    [InlineData("/opt/a\tb/aim", "")]
    [InlineData("/opt/x/aim\n", "")] // a trailing newline is caught before the trim
    [InlineData("/opt/x/aim", "--tray\t--minimized")]
    [InlineData(@"C:\aim.exe", "")] // not a path on any operating system
    public void FormatExec_RefusesWhatCannotBeAnAbsoluteSingleLineCommand(string executablePath, string? arguments)
    {
        Assert.Throws<InvalidOperationException>(() => XdgDesktopEntry.FormatExec(executablePath, arguments));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatExec_WithABlankExecutable_IsAnArgumentException(string executablePath)
    {
        Assert.Throws<ArgumentException>(() => XdgDesktopEntry.FormatExec(executablePath, ""));
    }

    [Fact]
    public void FormatExec_WithANullExecutable_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => XdgDesktopEntry.FormatExec(null!, ""));
    }

    // ---- FormatExec: rules the examples only imply ---------------------------------

    [Theory]
    [InlineData("/opt/x/aim\u007F", "")]
    [InlineData("/opt/x/aim\0", "")]
    [InlineData("\n/opt/x/aim", "")]
    [InlineData("/opt/x/aim", "--a\u007F")]
    [InlineData("/opt/x/aim", "--a\r\n--b")]
    [InlineData("/opt/x/aim", "\t")]
    public void FormatExec_RefusesAControlCharacterAnywhere_EvenWhereATrimWouldHaveRemovedIt(string executablePath, string arguments)
    {
        Assert.Throws<InvalidOperationException>(() => XdgDesktopEntry.FormatExec(executablePath, arguments));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\'')]
    [InlineData('>')]
    [InlineData('<')]
    [InlineData('~')]
    [InlineData('|')]
    [InlineData('&')]
    [InlineData(';')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('#')]
    [InlineData('(')]
    [InlineData(')')]
    public void FormatExec_QuotesATokenHoldingAnyReservedCharacter_WithoutEscapingIt(char reserved)
    {
        var result = XdgDesktopEntry.FormatExec($"/opt/a{reserved}b/aim", "");

        Assert.Equal($"\"/opt/a{reserved}b/aim\"", result);
    }

    [Fact]
    public void FormatExec_LeavesAnArgumentWithoutReservedCharactersBare()
    {
        Assert.Equal("/opt/x/aim --a=b --c%%d", XdgDesktopEntry.FormatExec("/opt/x/aim", "--a=b --c%d"));
    }

    // ---- Build ----------------------------------------------------------------------

    [Fact]
    public void Build_WritesTheWholeFileExactlyAsTheSpecSaysItEndsInOneLineFeed()
    {
        Assert.Equal(BuiltForOptX, XdgDesktopEntry.Build("/opt/x/aim"));
    }

    [Fact]
    public void Build_PutsTheFormattedCommandAfterExec()
    {
        var expected = BuiltForOptX.Replace(
            "Exec=/opt/x/aim\n",
            "Exec=\"/opt/AI Monitor/aim\" --minimized\n",
            StringComparison.Ordinal);

        Assert.Equal(expected, XdgDesktopEntry.Build("/opt/AI Monitor/aim", "--minimized"));
    }

    [Fact]
    public void Build_WithARelativePath_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => XdgDesktopEntry.Build("relative"));
    }

    // ---- ReadEnabledState: the worked examples --------------------------------------------------

    [Fact]
    public void ReadEnabledState_D1_AFileBuiltHereIsEnabled()
    {
        Assert.True(XdgDesktopEntry.ReadEnabledState(BuiltForOptX));
    }

    [Fact]
    public void ReadEnabledState_D2_HiddenTrueDisablesIt()
    {
        Assert.False(XdgDesktopEntry.ReadEnabledState(BuiltForOptX + "Hidden=true\n"));
    }

    [Fact]
    public void ReadEnabledState_D3_GnomeEnabledFalseDisablesIt()
    {
        var contents = BuiltForOptX.Replace(
            "X-GNOME-Autostart-enabled=true",
            "X-GNOME-Autostart-enabled=false",
            StringComparison.Ordinal);

        Assert.False(XdgDesktopEntry.ReadEnabledState(contents));
    }

    [Theory]
    [InlineData("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled = false\n", false)]
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=True\n", true)] // case-sensitive, so not a boolean
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=1\n", false)]
    [InlineData("", null)]
    [InlineData("[Other]\nExec=/x\n", null)]
    [InlineData("[Desktop Entry]\nType=Application\n", null)]
    [InlineData("[Desktop Entry]\nExec=\n", null)]
    [InlineData("# c\n[Desktop Entry]\nExec=/x\n[Desktop Action new]\nHidden=true\n", true)]
    [InlineData("Hidden=true\n[Desktop Entry]\nExec=/x\n", true)]
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=true\nHidden=false\n", true)]
    [InlineData("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=0\n", false)]
    [InlineData("  [Desktop Entry]  \n  Exec = /x  \n", true)]
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=true\nHidden=maybe\n", true)] // the last raw value is not a boolean
    [InlineData("[Desktop Entry]\nExec=/x\nExec=\n", null)]
    [InlineData("[Desktop Entry]\nExec=\nExec=/x\n", true)]
    [InlineData("[Desktop Entry]\nExec=/x\n[Broken\nHidden=true\n", false)]
    [InlineData("[Desktop Entry]\nExec=/x\njunk line\n", true)]
    public void ReadEnabledState_FollowsTheSpecTable(string contents, bool? expected)
    {
        Assert.Equal(expected, XdgDesktopEntry.ReadEnabledState(contents));
    }

    [Fact]
    public void ReadEnabledState_D11_ACrLfFileReadsTheSameAsAnLfFile()
    {
        Assert.True(XdgDesktopEntry.ReadEnabledState(BuiltForOptX.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReadEnabledState_D21_NullContentsIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => XdgDesktopEntry.ReadEnabledState(null!));
    }

    // ---- ReadEnabledState: rules the examples only imply --------------------------------

    [Fact]
    public void ReadEnabledState_GroupNamesAreCaseSensitive()
    {
        Assert.Null(XdgDesktopEntry.ReadEnabledState("[desktop entry]\nExec=/x\n"));
    }

    [Fact]
    public void ReadEnabledState_EntriesOfEveryDesktopEntryGroupCount_WhenTheGroupAppearsTwice()
    {
        // Entries of a group count every time that group appears.
        const string contents = "[Desktop Entry]\nExec=/x\n[Other]\nHidden=false\n[Desktop Entry]\nHidden=true\n";

        Assert.False(XdgDesktopEntry.ReadEnabledState(contents));
    }

    [Fact]
    public void ReadEnabledState_CommentsAndBlankLinesAreSkipped()
    {
        Assert.True(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\n\n# Hidden=true\n   # Hidden=true\nExec=/x\n"));
    }

    [Fact]
    public void ReadEnabledState_AKeyIsEverythingBeforeTheFirstEqualsSign()
    {
        // The value keeps the later '=' signs, so "Exec==" has the non-empty value "=".
        Assert.True(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec==\n"));
        Assert.False(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec=/x\nHidden = true\n"));
    }

    [Fact]
    public void ReadEnabledState_HiddenWinsOverGnomeEnabledTrue()
    {
        Assert.False(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=true\nHidden=1\n"));
    }

    [Fact]
    public void ReadEnabledState_DoesNotCheckThatExecPointsAtThisApp()
    {
        Assert.True(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec=/usr/bin/other-app\n"));
    }

    [Fact]
    public void ReadEnabledState_ALaterDuplicateOfTheGnomeKeyWinsBeforeItIsInterpreted()
    {
        Assert.True(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=false\nX-GNOME-Autostart-enabled=true\n"));
        Assert.False(XdgDesktopEntry.ReadEnabledState("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=true\nX-GNOME-Autostart-enabled=false\n"));
    }
}
