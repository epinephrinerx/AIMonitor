using System.Runtime.Versioning;
using System.Text;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// Which folder holds the single-instance socket on Linux (R-024): the per-user runtime folder when
/// it is safe to use, the app data folder otherwise. Every case works on a real folder with a real
/// mode inside a temp folder of its own, so all of it is Linux-only. The runtime folder and the
/// fallback are passed in as parameters; nothing here reads an environment variable.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxRuntimeDirectoryTests : IDisposable
{
    private const string Fallback = "/fb";
    private const string AppId = "AIUsageMonitor";

    // Kept short: the socket path limit leaves little room for a long temp folder.
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Prepend(_root))
        {
            try
            {
                if (new FileInfo(entry).LinkTarget is null)
                {
                    File.SetUnixFileMode(
                        entry,
                        Directory.Exists(entry)
                            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private static UnixFileMode Mode(string octal) => (UnixFileMode)Convert.ToInt32(octal, 8);

    private string MakeDirectory(string name, string octalMode)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, Mode(octalMode));
        return path;
    }

    private static string Resolve(string? xdg, string appId = AppId) =>
        LinuxRuntimeDirectory.ResolveSocketDirectory(xdg, Fallback, appId);

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void Constants_AreTheSocketPathBudget()
    {
        Assert.Equal("XDG_RUNTIME_DIR", LinuxRuntimeDirectory.EnvironmentVariable);
        Assert.Equal(103, LinuxRuntimeDirectory.MaxSocketPathBytes);
        Assert.Equal(20, LinuxRuntimeDirectory.SocketFileNameBytes);
        Assert.Equal(LinuxRuntimeDirectory.SocketFileNameBytes, "si-".Length + 12 + ".sock".Length);
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_1_AFolderOnlyTheOwnerCanUse_IsUsedWithTheAppIdAppended()
    {
        var dir = MakeDirectory("d1", "0700");

        Assert.Equal(Path.Combine(dir, AppId), Resolve(dir));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_2_AFolderAnyoneElseCanTouch_OrTheOwnerCannotFullyUse_FallsBack()
    {
        foreach (var mode in new[] { "0755", "0710", "0701", "0500", "0000", "0300", "0770", "0707", "0600", "0100" })
        {
            var dir = MakeDirectory("m" + mode, mode);

            Assert.True(Fallback == Resolve(dir), $"mode {mode} must fall back");
        }
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_3_TheStickyBitDoesNotDisqualifyAFolder()
    {
        var dir = MakeDirectory("d3", "1700");

        Assert.Equal(Path.Combine(dir, AppId), Resolve(dir));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_4_AnUnusablePathFallsBack_WithoutBeingTrimmedIntoShape()
    {
        var plainFile = Path.Combine(_root, "plain");
        Directory.CreateDirectory(_root);
        File.WriteAllText(plainFile, "x");
        var good = MakeDirectory("d4", "0700");

        foreach (var (label, path) in new (string, string?)[]
        {
            ("missing", Path.Combine(_root, "does-not-exist")),
            ("relative", "run/user/1000"),
            ("null", null),
            ("empty", ""),
            ("whitespace", "   "),
            ("plain file", plainFile),
            ("embedded NUL", "/tmp/a\0b"),
            ("leading space", " " + good),
        })
        {
            Assert.True(Fallback == Resolve(path), $"{label} must fall back");
        }
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_4_ARelativePathFallsBackEvenWhenItNamesARealPrivateFolder()
    {
        // Relative to the working directory; created there and removed again, never left behind.
        var relative = "aim-rel-" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(relative);
        try
        {
            File.SetUnixFileMode(relative, Mode("0700"));

            Assert.Equal(Fallback, Resolve(relative));
        }
        finally
        {
            Directory.Delete(relative);
        }
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P1_5_ASymbolicLinkToAGoodFolderFallsBack()
    {
        var target = MakeDirectory("target", "0700");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);

        Assert.Equal(Fallback, Resolve(link));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P1_5a_ASymbolicLinkIsStillRefusedWhenTheNameEndsInSlashes()
    {
        var target = MakeDirectory("target5a", "0700");
        var link = Path.Combine(_root, "link5a");
        Directory.CreateSymbolicLink(link, target);

        Assert.Equal(Fallback, Resolve(link + "/"));
        Assert.Equal(Fallback, Resolve(link + "//"));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_5b_ARealFolderGivenWithATrailingSlashIsUsedWithoutADoubledSlash()
    {
        var dir = MakeDirectory("d5b", "0700");

        var result = Resolve(dir + "/");

        Assert.Equal(Path.Combine(dir, AppId), result);
        Assert.DoesNotContain("//", result, StringComparison.Ordinal);
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_6_AScopedAppIdIsAppendedAsItIs()
    {
        var dir = MakeDirectory("d6", "0700");

        Assert.Equal(Path.Combine(dir, "AIUsageMonitor.smoke"), Resolve(dir, "AIUsageMonitor.smoke"));
    }

    // The socket path is the folder, the app id, a separator and a 20-byte file name; the limit is 103 bytes.
    private string DirectoryWhoseCombinedPathIs(string appId, int combinedBytes)
    {
        var folderBytes = combinedBytes - 1 - Encoding.UTF8.GetByteCount(appId);
        var padding = folderBytes - _root.Length - 1;
        Assert.True(padding > 0, "the temp folder is too long for this test");
        var path = Path.Combine(_root, new string('d', padding));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, Mode("0700"));
        Assert.Equal(combinedBytes, Encoding.UTF8.GetByteCount(Path.Combine(path, appId)));
        return path;
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_7_ASocketPathOfExactlyTheLimitIsAccepted()
    {
        var dir = DirectoryWhoseCombinedPathIs(AppId, 82);

        Assert.Equal(Path.Combine(dir, AppId), Resolve(dir));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_8_ASocketPathOneByteOverTheLimitFallsBack()
    {
        var dir = DirectoryWhoseCombinedPathIs(AppId, 83);

        Assert.Equal(Fallback, Resolve(dir));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_9_TheLimitCountsBytesNotCharacters()
    {
        // Two Thai letters are three bytes each: the app id is 17 characters but 21 bytes.
        const string thai = "AIUsageMonitor.ทด";
        Assert.Equal(21, Encoding.UTF8.GetByteCount(thai));

        var fits = DirectoryWhoseCombinedPathIs(thai, 82);
        Assert.Equal(Path.Combine(fits, thai), Resolve(fits, thai));

        var tooLong = DirectoryWhoseCombinedPathIs(thai, 83);
        Assert.Equal(Fallback, Resolve(tooLong, thai));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_10_ABlankFallbackOrAppIdIsRefused()
    {
        var dir = MakeDirectory("d10", "0700");

        Assert.Equal("fallbackDirectory", Assert.Throws<ArgumentNullException>(() => LinuxRuntimeDirectory.ResolveSocketDirectory(dir, null!, AppId)).ParamName);
        Assert.Equal("scopedAppId", Assert.Throws<ArgumentNullException>(() => LinuxRuntimeDirectory.ResolveSocketDirectory(dir, Fallback, null!)).ParamName);
        foreach (var blank in new[] { "", "  " })
        {
            Assert.Equal("fallbackDirectory", Assert.Throws<ArgumentException>(() => LinuxRuntimeDirectory.ResolveSocketDirectory(dir, blank, AppId)).ParamName);
            Assert.Equal("scopedAppId", Assert.Throws<ArgumentException>(() => LinuxRuntimeDirectory.ResolveSocketDirectory(dir, Fallback, blank)).ParamName);
        }
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void P1_11_ResolvingCreatesNothingAndChangesNoMode()
    {
        var dir = MakeDirectory("d11", "0700");

        Resolve(dir);

        Assert.False(Directory.Exists(Path.Combine(dir, AppId)));
        Assert.Equal(Mode("0700"), File.GetUnixFileMode(dir) & Mode("7777"));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    [LinuxOnlyFact("reads the real modes of real folders")]
    public void AFallbackThatIsTooLongIsStillReturnedUnchanged()
    {
        var longFallback = "/" + new string('f', 200);

        Assert.Equal(longFallback, LinuxRuntimeDirectory.ResolveSocketDirectory(null, longFallback, AppId));
    }
}
