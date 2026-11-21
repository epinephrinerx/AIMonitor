using System.Runtime.Versioning;
using System.Security.Cryptography;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The key file the Linux secret store falls back to when there is no Secret Service (R-024).
/// These work on real files, folders and modes inside a temp folder of their own, so they are
/// Linux-only. Every path is passed in; nothing here reads or sets an environment variable.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SecretKeyFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim-key-" + Guid.NewGuid().ToString("N")[..8]);

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

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & Mode("7777");

    private string MakeDirectory(string name, string octalMode)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, Mode(octalMode));
        return path;
    }

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static string[] NamesIn(string directory) =>
        [.. Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [LinuxOnlyFact("works on real files and modes")]
    public void KeyBytes_IsThirtyTwo()
    {
        Assert.Equal(32, SecretKeyFile.KeyBytes);
    }

    // ---- ReadOrCreate -----------------------------------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void ReadOrCreate_P3_13_MakesAPrivateFileInAPrivateFolderAndLeavesNothingElse()
    {
        var path = Path.Combine(_root, "a", "b", "secrets.key");

        var key = SecretKeyFile.ReadOrCreate(path);

        Assert.Equal(32, key.Length);
        Assert.Equal(key, File.ReadAllBytes(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));
        Assert.Equal(Mode("0700"), ModeOf(Path.Combine(_root, "a", "b")));
        Assert.Equal(["secrets.key"], NamesIn(Path.Combine(_root, "a", "b")));
        Assert.Contains(key, b => b != 0);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void ReadOrCreate_P3_14_ReturnsTheSameKeyEveryTime()
    {
        var path = Path.Combine(_root, "d", "secrets.key");

        var first = SecretKeyFile.ReadOrCreate(path);
        var second = SecretKeyFile.ReadOrCreate(path);

        Assert.Equal(first, second);
        Assert.Equal(first, SecretKeyFile.TryRead(path));
        Assert.Equal(["secrets.key"], NamesIn(Path.GetDirectoryName(path)!));
    }

    [LinuxOnlyFact("races real threads on one file; a statistical check, not a proof")]
    public void ReadOrCreate_P3_20a_EightThreadsAtOnceAllGetTheSameKey_AndLeaveNoTemporaryFile()
    {
        for (var round = 0; round < 30; round++)
        {
            var path = Path.Combine(_root, "r" + round, "d", "secrets.key");
            using var barrier = new Barrier(8);
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        return SecretKeyFile.ReadOrCreate(path);
                    },
                    TaskCreationOptions.LongRunning))
                .ToArray();

            var keys = Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

            var onDisk = File.ReadAllBytes(path);
            Assert.True(32 == onDisk.Length, $"round {round}: the key on disk is {onDisk.Length} bytes");
            Assert.True(keys.All(key => key.AsSpan().SequenceEqual(onDisk)), $"round {round}: the threads did not all get the key on disk");
            Assert.True(
                NamesIn(Path.GetDirectoryName(path)!).SequenceEqual(["secrets.key"]),
                $"round {round}: the folder holds {string.Join(",", NamesIn(Path.GetDirectoryName(path)!))}");
            Assert.True((UnixFileMode.UserRead | UnixFileMode.UserWrite) == ModeOf(path), $"round {round}: the key file is not 0600");
        }
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void ReadOrCreate_MakesADifferentKeyForADifferentFile()
    {
        var first = SecretKeyFile.ReadOrCreate(Path.Combine(_root, "one", "secrets.key"));
        var second = SecretKeyFile.ReadOrCreate(Path.Combine(_root, "two", "secrets.key"));

        Assert.NotEqual(first, second);
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void ReadOrCreate_P3_15_AnExistingFolderIsMadePrivate()
    {
        foreach (var mode in new[] { "0755", "0500" })
        {
            var directory = MakeDirectory("m" + mode, mode);
            var path = Path.Combine(directory, "secrets.key");

            SecretKeyFile.ReadOrCreate(path);

            Assert.True(Mode("0700") == ModeOf(directory), $"folder mode {mode} was not made private");
            Assert.True(File.Exists(path), $"folder mode {mode}: no key was written");
        }
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void ReadOrCreate_ReturnsAnExistingKeyWithoutChangingIt()
    {
        var directory = MakeDirectory("d", "0700");
        var path = Path.Combine(directory, "secrets.key");
        var existing = RandomBytes(32);
        File.WriteAllBytes(path, existing);
        File.SetUnixFileMode(path, Mode("0600"));

        Assert.Equal(existing, SecretKeyFile.ReadOrCreate(path));
        Assert.Equal(existing, File.ReadAllBytes(path));
    }

    [LinuxNonRootOnlyFact("root can create folders anywhere")]
    public void ReadOrCreate_WhenTheFolderCannotBeMade_IsACryptographicExceptionWithTheCauseInside()
    {
        var parent = MakeDirectory("ro", "0500");
        var path = Path.Combine(parent, "d", "secrets.key");

        var thrown = Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(path));

        Assert.NotNull(thrown.InnerException);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    // ---- TryRead ----------------------------------------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void TryRead_P3_16_WithoutAFileReturnsNullAndCreatesNothing()
    {
        var directory = MakeDirectory("d", "0755");

        var key = SecretKeyFile.TryRead(Path.Combine(directory, "secrets.key"));

        Assert.Null(key);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        Assert.Equal(Mode("0755"), ModeOf(directory));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void TryRead_WithoutEvenTheFolderReturnsNullAndCreatesNothing()
    {
        var key = SecretKeyFile.TryRead(Path.Combine(_root, "nodir", "secrets.key"));

        Assert.Null(key);
        Assert.False(Directory.Exists(Path.Combine(_root, "nodir")));
    }

    [LinuxOnlyFact("works on real files and modes, as root or not")]
    public void TryRead_P3_17_MakesALooseFileAndFolderPrivateAndReturnsTheKey()
    {
        foreach (var mode in new[] { "0644", "0400", "0000", "4600", "0700" })
        {
            var directory = MakeDirectory("m" + mode, "0755");
            var path = Path.Combine(directory, "secrets.key");
            var key = RandomBytes(32);
            File.WriteAllBytes(path, key);
            File.SetUnixFileMode(path, Mode(mode));

            var read = SecretKeyFile.TryRead(path);

            Assert.True(key.AsSpan().SequenceEqual(read), $"file mode {mode}: wrong bytes");
            Assert.True((UnixFileMode.UserRead | UnixFileMode.UserWrite) == ModeOf(path), $"file mode {mode}: not 0600 afterwards");
            Assert.True(Mode("0700") == ModeOf(directory), $"file mode {mode}: folder not 0700 afterwards");
        }
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void TryRead_AWrongLengthIsRefusedAndTheFileIsNeverTouched()
    {
        foreach (var length in new[] { 0, 1, 31, 33, 64 })
        {
            var directory = MakeDirectory("l" + length, "0700");
            var path = Path.Combine(directory, "secrets.key");
            var original = RandomBytes(length);
            File.WriteAllBytes(path, original);
            File.SetUnixFileMode(path, Mode("0600"));

            Assert.Throws<CryptographicException>(() => SecretKeyFile.TryRead(path));
            Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(path));

            Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), $"length {length}: the file was changed");
            Assert.Equal(["secrets.key"], NamesIn(directory));
        }
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_19_ASymbolicLinkToAGoodKeyIsRefused()
    {
        var directory = MakeDirectory("d", "0700");
        var target = Path.Combine(directory, "real.key");
        var key = RandomBytes(32);
        File.WriteAllBytes(target, key);
        File.SetUnixFileMode(target, Mode("0644"));
        var link = Path.Combine(directory, "secrets.key");
        File.CreateSymbolicLink(link, target);

        Assert.Throws<CryptographicException>(() => SecretKeyFile.TryRead(link));
        Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(link));

        Assert.Equal(key, File.ReadAllBytes(target));
        Assert.Equal(Mode("0644"), ModeOf(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_19_ASymbolicLinkWhoseTargetIsMissingIsRefused_AndNothingIsCreatedThroughIt()
    {
        var directory = MakeDirectory("d", "0700");
        var missing = Path.Combine(directory, "missing.key");
        var link = Path.Combine(directory, "secrets.key");
        File.CreateSymbolicLink(link, missing);

        Assert.Throws<CryptographicException>(() => SecretKeyFile.TryRead(link));
        Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(link));

        Assert.False(File.Exists(missing));
        Assert.Equal(missing, new FileInfo(link).LinkTarget);
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_19_AFolderThatIsASymbolicLinkIsRefused_BeforeAnyModeIsChanged()
    {
        var target = MakeDirectory("target", "0755");
        var key = RandomBytes(32);
        File.WriteAllBytes(Path.Combine(target, "secrets.key"), key);
        File.SetUnixFileMode(Path.Combine(target, "secrets.key"), Mode("0644"));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        var path = Path.Combine(link, "secrets.key");

        Assert.Throws<CryptographicException>(() => SecretKeyFile.TryRead(path));
        Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(path));

        Assert.Equal(Mode("0755"), ModeOf(target));
        Assert.Equal(Mode("0644"), ModeOf(Path.Combine(target, "secrets.key")));
        Assert.Equal(["secrets.key"], NamesIn(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P3_19_AFolderThatIsASymbolicLinkIsRefusedEvenWhenItHoldsNoKey()
    {
        var target = MakeDirectory("target", "0755");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);

        Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(Path.Combine(link, "secrets.key")));

        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Equal(Mode("0755"), ModeOf(target));
    }

    [LinuxOnlyFact("works on real files and modes")]
    public void P3_20_AFolderWhereTheKeyShouldBeIsRefused()
    {
        var directory = MakeDirectory("d", "0700");
        var path = Path.Combine(directory, "secrets.key");
        Directory.CreateDirectory(path);

        Assert.Throws<CryptographicException>(() => SecretKeyFile.TryRead(path));
        Assert.Throws<CryptographicException>(() => SecretKeyFile.ReadOrCreate(path));

        Assert.True(Directory.Exists(path));
        Assert.Equal(["secrets.key"], NamesIn(directory));
    }

    // ---- arguments --------------------------------------------------------------------------------

    [LinuxOnlyFact("works on real files and modes")]
    public void BadPathsAreRefused_WithTheParameterNamed()
    {
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => SecretKeyFile.TryRead(null!)).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => SecretKeyFile.ReadOrCreate(null!)).ParamName);
        foreach (var bad in new[] { "", "   ", "rel/secrets.key", "secrets.key" })
        {
            Assert.True("path" == Assert.Throws<ArgumentException>(() => SecretKeyFile.TryRead(bad)).ParamName, $"TryRead '{bad}'");
            Assert.True("path" == Assert.Throws<ArgumentException>(() => SecretKeyFile.ReadOrCreate(bad)).ParamName, $"ReadOrCreate '{bad}'");
        }
    }
}
