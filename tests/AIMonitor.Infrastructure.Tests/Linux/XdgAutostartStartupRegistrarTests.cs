using System.Runtime.Versioning;
using System.Text;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The XDG autostart registrar (R-024). The path rules
/// and constructor checks are pure and run everywhere; the tests that touch the disk are
/// Linux-only and work inside a temp folder of their own, never the real <c>~/.config/autostart</c>.
/// Every value is passed in as a parameter: nothing here reads or sets an environment variable.
/// </summary>
public sealed class XdgAutostartStartupRegistrarTests : IDisposable
{
    // The exact bytes the entry for /opt/x/aim must have, spelled out so no test reads them back from the code under test.
    private const string BuiltForOptX =
        "[Desktop Entry]\nType=Application\nName=AI Usage Monitor\nComment=Start AI Usage Monitor when you log in\nExec=/opt/x/aim\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    private const string BuiltForOptY =
        "[Desktop Entry]\nType=Application\nName=AI Usage Monitor\nComment=Start AI Usage Monitor when you log in\nExec=/opt/y/aim\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim-xdg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // A test may have taken the permissions away; give them back so the delete can succeed.
            foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Prepend(_root))
            {
                try
                {
                    File.SetUnixFileMode(
                        entry,
                        Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null
                            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private XdgAutostartStartupRegistrar NewRegistrar(string relativeDirectory = "cfg/autostart", string fileId = "net.x") =>
        new(Path.Combine(_root, relativeDirectory), fileId);

    private static string[] EntriesOf(string directory) =>
        [.. Directory.GetFileSystemEntries(directory).Select(Path.GetFileName)!];

    // ---- Pure: constants and path rules ----------------------------------------------------------

    [Fact]
    public void DefaultFileId_IsTheLoginItemNameTheOtherPlatformsUse()
    {
        Assert.Equal("net.apichart.aiusagemonitor", XdgAutostartStartupRegistrar.DefaultFileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cfg")]
    [InlineData("/x\0y")]
    public void ResolveAutostartDirectory_P2_50_FallsBackToTheHomeFolderWhenXdgConfigHomeIsUnusable(string? xdgConfigHome)
    {
        Assert.Equal("/home/u/.config/autostart", XdgAutostartStartupRegistrar.ResolveAutostartDirectory(xdgConfigHome, "/home/u"));
    }

    [Theory]
    [InlineData("/xdg/cfg", "/xdg/cfg/autostart")]
    [InlineData("/xdg/cfg/", "/xdg/cfg/autostart")]
    [InlineData("/", "/autostart")]
    public void ResolveAutostartDirectory_P2_51_UsesAnAbsoluteXdgConfigHome(string xdgConfigHome, string expected)
    {
        Assert.Equal(expected, XdgAutostartStartupRegistrar.ResolveAutostartDirectory(xdgConfigHome, "/home/u"));
    }

    [Fact]
    public void ResolveAutostartDirectory_P2_52_DropsATrailingSlashOfTheHomeFolder()
    {
        Assert.Equal("/home/u/.config/autostart", XdgAutostartStartupRegistrar.ResolveAutostartDirectory(null, "/home/u/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("home/u")]
    [InlineData(@"C:\Users\u")]
    [InlineData("/home/\0u")]
    public void ResolveAutostartDirectory_P2_53_RefusesAHomeFolderThatIsNotAnAbsolutePath(string home)
    {
        var thrown = Assert.Throws<ArgumentException>(() => XdgAutostartStartupRegistrar.ResolveAutostartDirectory(null, home));

        Assert.Equal("homeDirectory", thrown.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("home/u")]
    [InlineData("/home/\0u")]
    public void ResolveAutostartDirectory_AValidXdgConfigHomeDoesNotExcuseAnInvalidHome(string home)
    {
        // The home folder is validated first, whether or not it would have been used.
        var thrown = Assert.Throws<ArgumentException>(() => XdgAutostartStartupRegistrar.ResolveAutostartDirectory("/xdg/cfg", home));

        Assert.Equal("homeDirectory", thrown.ParamName);
        Assert.Equal("homeDirectory", Assert.Throws<ArgumentNullException>(() => XdgAutostartStartupRegistrar.ResolveAutostartDirectory("/xdg/cfg", null!)).ParamName);
    }

    [Theory]
    [InlineData(" /xdg/cfg", "/home/u/.config/autostart")]
    [InlineData("/xdg/cfg ", "/xdg/cfg /autostart")]
    [InlineData("\t/xdg/cfg", "/home/u/.config/autostart")]
    public void ResolveAutostartDirectory_TheXdgConfigHomeIsNeverTrimmed(string xdgConfigHome, string expected)
    {
        Assert.Equal(expected, XdgAutostartStartupRegistrar.ResolveAutostartDirectory(xdgConfigHome, "/home/u"));
    }

    [Fact]
    public void ResolveAutostartDirectory_P2_53_RefusesANullHomeFolder()
    {
        Assert.Equal("homeDirectory", Assert.Throws<ArgumentNullException>(() => XdgAutostartStartupRegistrar.ResolveAutostartDirectory(null, null!)).ParamName);
    }

    [Theory]
    [InlineData("autostart", "id", "autostartDirectory")]
    [InlineData("/a", "a/b", "fileId")]
    [InlineData("/a", " ", "fileId")]
    [InlineData("/a", "a\0b", "fileId")]
    [InlineData("/a\0", "id", "autostartDirectory")]
    [InlineData(" ", "id", "autostartDirectory")]
    [InlineData("", "id", "autostartDirectory")]
    [InlineData("/a", "", "fileId")]
    public void Constructor_P2_54_RefusesADirectoryOrFileIdTheSpecRules_Out(string directory, string fileId, string parameter)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new XdgAutostartStartupRegistrar(directory, fileId));

        Assert.Equal(parameter, thrown.ParamName);
    }

    [Fact]
    public void Constructor_RefusesNullArguments()
    {
        Assert.Equal("autostartDirectory", Assert.Throws<ArgumentNullException>(() => new XdgAutostartStartupRegistrar(null!, "id")).ParamName);
        Assert.Equal("fileId", Assert.Throws<ArgumentNullException>(() => new XdgAutostartStartupRegistrar("/a", null!)).ParamName);
    }

    [Theory]
    [InlineData("/home/u/.config/autostart")]
    [InlineData("/home/u/.config/autostart/")]
    public void Constructor_P2_55_BuildsTheDesktopFilePathWithoutADoubledSlash(string directory)
    {
        var registrar = new XdgAutostartStartupRegistrar(directory, "net.x");

        Assert.Equal("/home/u/.config/autostart/net.x.desktop", registrar.DesktopFilePath);
        Assert.Equal(directory, registrar.AutostartDirectory);
        Assert.Equal("net.x", registrar.FileId);
    }

    // ---- Linux: the disk ---------------------------------------------------------------------------

    [LinuxOnlyFact("the registrar writes real files under a per-user folder")]
    public void Constructor_P2_56_DoesNotTouchTheDisk()
    {
        _ = NewRegistrar();

        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [LinuxOnlyFact("reads a real, missing folder")]
    public void P2_57_WithoutTheFolder_NothingIsRegistered()
    {
        var registrar = NewRegistrar();

        Assert.False(registrar.GetRegistrationState());
        Assert.False(registrar.IsRegistered());
    }

    [LinuxOnlyFact("writes a real file")]
    public void Register_P2_58_CreatesTheFoldersAndWritesTheEntryWithoutABomOrLeftovers()
    {
        var registrar = NewRegistrar("a/b/autostart");

        registrar.Register("/opt/x/aim");

        var bytes = File.ReadAllBytes(registrar.DesktopFilePath);
        Assert.Equal(Encoding.UTF8.GetBytes(BuiltForOptX), bytes);
        Assert.Equal(0x5B, bytes[0]);
        Assert.True(registrar.GetRegistrationState());
        Assert.True(registrar.IsRegistered());
        Assert.Equal(["net.x.desktop"], EntriesOf(registrar.AutostartDirectory));
    }

    [LinuxOnlyFact("writes a real file")]
    public void Register_P2_58_PassesTheArgumentsOn()
    {
        var registrar = NewRegistrar();

        registrar.Register("/opt/AI Monitor/aim", "--minimized");

        Assert.Equal(
            BuiltForOptX.Replace("Exec=/opt/x/aim\n", "Exec=\"/opt/AI Monitor/aim\" --minimized\n", StringComparison.Ordinal),
            File.ReadAllText(registrar.DesktopFilePath));
    }

    [LinuxOnlyFact("writes a real file")]
    public void Register_P2_59_IsIdempotent()
    {
        var registrar = NewRegistrar();

        registrar.Register("/opt/x/aim");
        var first = File.ReadAllBytes(registrar.DesktopFilePath);
        registrar.Register("/opt/x/aim");

        Assert.Equal(first, File.ReadAllBytes(registrar.DesktopFilePath));
        Assert.Equal(["net.x.desktop"], EntriesOf(registrar.AutostartDirectory));
    }

    [LinuxOnlyFact("writes a real file")]
    public void Register_P2_60_ReplacesAHiddenEntryWithAnEnabledOne()
    {
        var registrar = NewRegistrar();
        registrar.Register("/opt/x/aim");
        File.AppendAllText(registrar.DesktopFilePath, "Hidden=true\n");
        Assert.False(registrar.GetRegistrationState());

        registrar.Register("/opt/x/aim");

        Assert.Equal(BuiltForOptX, File.ReadAllText(registrar.DesktopFilePath));
        Assert.True(registrar.GetRegistrationState());
    }

    [LinuxOnlyFact("deletes a real file")]
    public void Unregister_P2_61_RemovesTheFileButKeepsTheFolder()
    {
        var registrar = NewRegistrar();
        registrar.Register("/opt/x/aim");

        registrar.Unregister();

        Assert.False(File.Exists(registrar.DesktopFilePath));
        Assert.True(Directory.Exists(registrar.AutostartDirectory));
        Assert.False(registrar.GetRegistrationState());

        registrar.Unregister();
        NewRegistrar("never/created").Unregister();
        Assert.False(Directory.Exists(Path.Combine(_root, "never")));
    }

    [LinuxOnlyFact("writes and deletes real files")]
    public void P2_62_OtherEntriesInTheFolderAreLeftAlone()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.AutostartDirectory);
        var otherPath = Path.Combine(registrar.AutostartDirectory, "other.desktop");
        byte[] other = [0x5B, 0x44, 0x0A, 0xFF, 0x00];
        File.WriteAllBytes(otherPath, other);

        registrar.Register("/opt/x/aim");
        registrar.Unregister();

        Assert.Equal(other, File.ReadAllBytes(otherPath));
        Assert.Equal(["other.desktop"], EntriesOf(registrar.AutostartDirectory));
    }

    [LinuxOnlyFact("checks that nothing is created on disk")]
    public void Register_P2_63_WithABlankExecutable_IsRefusedBeforeTheDiskIsTouched()
    {
        var registrar = NewRegistrar();

        Assert.Equal("executablePath", Assert.Throws<ArgumentException>(() => registrar.Register("  ")).ParamName);
        Assert.Equal("executablePath", Assert.Throws<ArgumentException>(() => registrar.Register("\t")).ParamName);

        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [LinuxOnlyFact("checks that nothing is created on disk")]
    public void Register_P2_63_WithANullExecutable_IsRefusedBeforeTheDiskIsTouched()
    {
        var registrar = NewRegistrar();

        Assert.Equal("executablePath", Assert.Throws<ArgumentNullException>(() => registrar.Register(null!)).ParamName);

        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [LinuxOnlyFact("checks that nothing is created on disk")]
    public void Register_P2_63_WithARelativeExecutable_IsRefusedBeforeTheDiskIsTouched()
    {
        var registrar = NewRegistrar();

        Assert.Throws<InvalidOperationException>(() => registrar.Register("relative/aim"));

        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [LinuxOnlyFact("watches a real file while it is replaced; a statistical check, not a proof")]
    public void Register_AReaderNeverSeesAMissingOrPartialEntryWhileItIsBeingReplaced()
    {
        var registrar = NewRegistrar();
        registrar.Register("/opt/x/aim");
        var anomalies = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var text = File.ReadAllText(registrar.DesktopFilePath);
                    if (text != BuiltForOptX && text != BuiltForOptY)
                    {
                        anomalies.Enqueue("partial or unexpected content: " + text);
                    }
                }
                catch (FileNotFoundException)
                {
                    anomalies.Enqueue("the entry was missing");
                }
                catch (DirectoryNotFoundException)
                {
                    anomalies.Enqueue("the folder was missing");
                }
            }
        });

        for (var round = 0; round < 300; round++)
        {
            registrar.Register(round % 2 == 0 ? "/opt/y/aim" : "/opt/x/aim");
        }

        stop.Cancel();
        reader.GetAwaiter().GetResult();
        Assert.Empty(anomalies);
        Assert.Equal(["net.x.desktop"], EntriesOf(registrar.AutostartDirectory));
    }

    [LinuxOnlyFact("a real directory sits where the entry would be")]
    public void P2_64_AFolderWhereTheEntryBelongs_IsNeverReplacedOrRemoved()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.DesktopFilePath);
        var marker = Path.Combine(registrar.DesktopFilePath, "marker.txt");
        File.WriteAllText(marker, "keep");

        Assert.False(registrar.GetRegistrationState());
        var register = Assert.Throws<InvalidOperationException>(() => registrar.Register("/opt/x/aim"));
        Assert.Equal($"Cannot write the autostart entry at '{registrar.DesktopFilePath}'.", register.Message);
        Assert.NotNull(register.InnerException);
        Assert.True(Directory.Exists(registrar.DesktopFilePath));
        Assert.Equal(["net.x.desktop"], EntriesOf(registrar.AutostartDirectory));

        var unregister = Assert.Throws<InvalidOperationException>(() => registrar.Unregister());
        Assert.Equal($"Cannot remove the autostart entry at '{registrar.DesktopFilePath}'.", unregister.Message);
        Assert.NotNull(unregister.InnerException);
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void Register_P2_65_ReplacesASymbolicLinkWithARegularFile_LeavingItsTargetAlone()
    {
        var registrar = NewRegistrar();
        var target = LinkTo(registrar, BuiltForOptX);

        Assert.True(registrar.GetRegistrationState());

        registrar.Register("/opt/y/aim");

        Assert.Null(new FileInfo(registrar.DesktopFilePath).LinkTarget);
        Assert.Equal(BuiltForOptY, File.ReadAllText(registrar.DesktopFilePath));
        Assert.Equal(BuiltForOptX, File.ReadAllText(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void Unregister_P2_65_RemovesTheLinkNotItsTarget()
    {
        var registrar = NewRegistrar();
        var target = LinkTo(registrar, BuiltForOptX);

        registrar.Unregister();

        Assert.Null(new FileInfo(registrar.DesktopFilePath).LinkTarget);
        Assert.False(File.Exists(registrar.DesktopFilePath));
        Assert.Equal(BuiltForOptX, File.ReadAllText(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P2_69_ASymbolicLinkToAMissingFileIsNotRegistered_AndRegisterReplacesItWithARegularFile()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.AutostartDirectory);
        var missing = Path.Combine(registrar.AutostartDirectory, "missing.desktop");
        File.CreateSymbolicLink(registrar.DesktopFilePath, missing);

        Assert.False(registrar.GetRegistrationState());
        Assert.False(registrar.IsRegistered());

        registrar.Register("/opt/x/aim");

        Assert.Null(new FileInfo(registrar.DesktopFilePath).LinkTarget);
        Assert.Equal(BuiltForOptX, File.ReadAllText(registrar.DesktopFilePath));
        Assert.True(registrar.GetRegistrationState());
        Assert.False(File.Exists(missing));
    }

    private string LinkTo(XdgAutostartStartupRegistrar registrar, string contents)
    {
        Directory.CreateDirectory(registrar.AutostartDirectory);
        var target = Path.Combine(_root, "target.desktop");
        File.WriteAllText(target, contents);
        File.CreateSymbolicLink(registrar.DesktopFilePath, target);
        return target;
    }

    [LinuxNonRootOnlyFact("root ignores a read-only folder, so the write would succeed")]
    [SupportedOSPlatform("linux")]
    public void Register_P2_66_AFailedWriteLeavesTheExistingEntryIntact()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.AutostartDirectory);
        File.WriteAllText(registrar.DesktopFilePath, BuiltForOptX);
        var original = File.ReadAllBytes(registrar.DesktopFilePath);
        File.SetUnixFileMode(registrar.AutostartDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var thrown = Assert.Throws<InvalidOperationException>(() => registrar.Register("/opt/y/aim"));

        File.SetUnixFileMode(
            registrar.AutostartDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal($"Cannot write the autostart entry at '{registrar.DesktopFilePath}'.", thrown.Message);
        Assert.NotNull(thrown.InnerException);
        Assert.Equal(original, File.ReadAllBytes(registrar.DesktopFilePath));
        Assert.Equal(["net.x.desktop"], EntriesOf(registrar.AutostartDirectory));
    }

    [LinuxOnlyFact("reads a real file")]
    public void GetRegistrationState_P2_67_GarbageIsNotRegistered_AndNotKnown()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.AutostartDirectory);
        File.WriteAllText(registrar.DesktopFilePath, "garbage");

        Assert.Null(registrar.GetRegistrationState());
        Assert.False(registrar.IsRegistered());
    }

    [LinuxOnlyFact("reads real files holding malformed UTF-8")]
    public void GetRegistrationState_P2_70_MalformedUtf8IsReadLeniently_AndRegisterReplacesTheFile()
    {
        var cases = new (string Name, byte[] Bytes, bool Expected)[]
        {
            ("bad bytes in Exec", [.. Encoding.UTF8.GetBytes("[Desktop Entry]\nExec=/x"), 0xFF, 0xFE, .. "\n"u8.ToArray()], true),
            ("bad byte inside a boolean", [.. Encoding.UTF8.GetBytes("[Desktop Entry]\nExec=/x\nHidden="), 0xC3, .. "true\n"u8.ToArray()], true),
            ("bad byte after a real Hidden=true", [.. Encoding.UTF8.GetBytes("[Desktop Entry]\nExec=/x\nHidden=true\n"), 0xFF], false),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var registrar = NewRegistrar("cfg" + index + "/autostart");
            Directory.CreateDirectory(registrar.AutostartDirectory);
            File.WriteAllBytes(registrar.DesktopFilePath, cases[index].Bytes);

            Assert.True(cases[index].Expected == registrar.GetRegistrationState(), cases[index].Name);

            if (index == 0)
            {
                registrar.Register("/opt/x/aim");

                Assert.Equal(Encoding.UTF8.GetBytes(BuiltForOptX), File.ReadAllBytes(registrar.DesktopFilePath));
                Assert.True(registrar.GetRegistrationState());
            }
        }
    }

    [LinuxNonRootOnlyFact("root can read a mode-000 file")]
    [SupportedOSPlatform("linux")]
    public void GetRegistrationState_P2_68_AnUnreadableFileIsUnknownNotFalse()
    {
        var registrar = NewRegistrar();
        Directory.CreateDirectory(registrar.AutostartDirectory);
        File.WriteAllText(registrar.DesktopFilePath, BuiltForOptX);
        File.SetUnixFileMode(registrar.DesktopFilePath, UnixFileMode.None);

        Assert.Null(registrar.GetRegistrationState());
        Assert.False(registrar.IsRegistered());
    }
}
