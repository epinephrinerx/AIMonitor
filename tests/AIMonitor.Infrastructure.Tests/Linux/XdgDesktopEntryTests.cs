using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The pure Desktop Entry rules (SPEC_R024 section 2, A1-A8). Every expected value is the raw text
/// the spec's tables give, written as a verbatim string so the backslashes and quotes read as they
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

    // ---- FormatExec: table A5 (P2-1..24) -------------------------------------------------------

    [Theory]
    [InlineData(@"/home/u/apps/aim/AIMonitor.Presentation.Avalonia", "", @"/home/u/apps/aim/AIMonitor.Presentation.Avalonia")] // E1
    [InlineData(@"/opt/AI Monitor/aim", "--minimized", @"""/opt/AI Monitor/aim"" --minimized")] // E2
    [InlineData(@" ""/opt/AI Monitor/aim"" ", null, @"""/opt/AI Monitor/aim""")] // E3
    [InlineData(@"/opt/a$b/aim", "", @"""/opt/a\\$b/aim""")] // E4
    [InlineData(@"/opt/back\slash/aim", "", @"""/opt/back\\\\slash/aim""")] // E5: one backslash in, four out
    [InlineData(@"/opt/q""uote/aim", "", @"""/opt/q\\""uote/aim""")] // E6
    [InlineData(@"/opt/100%/aim", "", @"/opt/100%%/aim")] // E7
    [InlineData(@"/opt/it's/aim", "", @"""/opt/it's/aim""")] // E8
    [InlineData(@"/opt/50% off/aim", "", @"""/opt/50%% off/aim""")] // E9
    [InlineData(@"/opt/x/aim", "  --tray   --minimized ", @"/opt/x/aim --tray --minimized")] // E10
    [InlineData(@"/opt/x~y/aim", "", @"""/opt/x~y/aim""")] // E11
    [InlineData(@"/opt/x/aim", "--open=a=b", @"/opt/x/aim --open=a=b")] // E12
    [InlineData(@"/opt/x/aim", "--name=$HOME", @"/opt/x/aim ""--name=\\$HOME""")] // E13
    [InlineData("/opt/back`tick/aim", "", @"""/opt/back\\`tick/aim""")] // E14
    [InlineData(@"/opt/a=b/aim", "", @"/opt/a=b/aim")] // E18
    [InlineData(@"/opt/x/aim", @"--x ""a b""", @"/opt/x/aim --x ""\\""a"" ""b\\""""")] // E21: no quoting dialect, split at the space
    [InlineData(@"/opt/x/aim", @"""""", @"/opt/x/aim ""\\""\\""""")] // E22
    [InlineData(@"/opt/x/aim""", "", @"/opt/x/aim")] // E23: a trailing literal quote is trimmed away
    public void FormatExec_QuotesAndEscapesAsTheSpecTableSays(string executablePath, string? arguments, string expected)
    {
        Assert.Equal(expected, XdgDesktopEntry.FormatExec(executablePath, arguments));
    }

    [Theory]
    [InlineData("relative/aim", "")] // E15
    [InlineData("/opt/a\tb/aim", "")] // E16
    [InlineData("/opt/x/aim\n", "")] // E19: a trailing newline is caught before the trim
    [InlineData("/opt/x/aim", "--tray\t--minimized")] // E20
    [InlineData(@"C:\aim.exe", "")] // E24: not a path on any operating system
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

    // ---- FormatExec: rules the table only implies (A2, A3, A4) ---------------------------------

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

    // ---- Build (P2-25..27) ----------------------------------------------------------------------

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

    // ---- ReadEnabledState: table D (P2-28..49) --------------------------------------------------

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
    [InlineData("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled = false\n", false)] // D4
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=True\n", true)] // D5: case-sensitive, so not a boolean
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=1\n", false)] // D6
    [InlineData("", null)] // D7
    [InlineData("[Other]\nExec=/x\n", null)] // D8
    [InlineData("[Desktop Entry]\nType=Application\n", null)] // D9
    [InlineData("[Desktop Entry]\nExec=\n", null)] // D10
    [InlineData("# c\n[Desktop Entry]\nExec=/x\n[Desktop Action new]\nHidden=true\n", true)] // D12
    [InlineData("Hidden=true\n[Desktop Entry]\nExec=/x\n", true)] // D13
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=true\nHidden=false\n", true)] // D14
    [InlineData("[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=0\n", false)] // D15
    [InlineData("  [Desktop Entry]  \n  Exec = /x  \n", true)] // D16
    [InlineData("[Desktop Entry]\nExec=/x\nHidden=true\nHidden=maybe\n", true)] // D17: the last raw value is not a boolean
    [InlineData("[Desktop Entry]\nExec=/x\nExec=\n", null)] // D18
    [InlineData("[Desktop Entry]\nExec=\nExec=/x\n", true)] // D19
    [InlineData("[Desktop Entry]\nExec=/x\n[Broken\nHidden=true\n", false)] // D20
    [InlineData("[Desktop Entry]\nExec=/x\njunk line\n", true)] // D22
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

    // ---- ReadEnabledState: rules the table only implies (A7, A8) --------------------------------

    [Fact]
    public void ReadEnabledState_GroupNamesAreCaseSensitive()
    {
        Assert.Null(XdgDesktopEntry.ReadEnabledState("[desktop entry]\nExec=/x\n"));
    }

    [Fact]
    public void ReadEnabledState_EntriesOfEveryDesktopEntryGroupCount_WhenTheGroupAppearsTwice()
    {
        // A7: "นับเฉพาะ entry ใน group Desktop Entry (ทุกครั้งที่ปรากฏ)".
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
