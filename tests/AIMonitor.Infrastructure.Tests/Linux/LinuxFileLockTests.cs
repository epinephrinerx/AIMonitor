using System.Diagnostics;
using System.Runtime.Versioning;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The advisory lock file the Linux adapters use to take turns (R-024). These hold real locks on
/// real files, so they are Linux-only. Every path is passed in; nothing here reads the environment.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFileLockTests : IDisposable
{
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

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & Mode("7777");

    private string LockPath(string name = "a.lock")
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void Constants_PollEveryFiftyMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), LinuxFileLock.PollInterval);
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_12_ANewLockFileIsCreatedPrivate()
    {
        var path = LockPath();

        using var held = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);

        Assert.NotNull(held);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_13_AHeldLockMakesTheNextCallerWaitThenGiveUp()
    {
        var path = LockPath();
        using var first = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        Assert.NotNull(first);

        var clock = Stopwatch.StartNew();
        var second = LinuxFileLock.TryAcquire(path, TimeSpan.FromMilliseconds(200));
        clock.Stop();

        Assert.Null(second);
        Assert.True(clock.ElapsedMilliseconds >= 150, $"gave up after only {clock.ElapsedMilliseconds} ms");
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_13_AZeroTimeoutTriesExactlyOnceAndReturnsAtOnce()
    {
        var path = LockPath();
        using var first = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);

        var clock = Stopwatch.StartNew();
        var second = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        clock.Stop();

        Assert.Null(second);
        // A generous bound for a loaded machine: a polling loop would wait on the 50 ms interval and a
        // real timeout in the seconds, so staying far below those still tells one attempt from many.
        Assert.True(clock.ElapsedMilliseconds < 2000, $"a single attempt took {clock.ElapsedMilliseconds} ms");
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_13_ALockReleasedWhileAnotherCallerWaitsIsTakenByIt()
    {
        var path = LockPath();
        var first = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        Assert.NotNull(first);
        var waiter = Task.Run(() => LinuxFileLock.TryAcquire(path, TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);

        first.Dispose();

        using var taken = waiter.GetAwaiter().GetResult();
        Assert.NotNull(taken);
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_14_DisposingReleasesTheLock_AndKeepsTheFile()
    {
        var path = LockPath();
        LinuxFileLock.TryAcquire(path, TimeSpan.Zero)!.Dispose();

        Assert.True(File.Exists(path));
        using var again = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [LinuxOnlyFact("sets and reads real file modes, as root or not")]
    public void P1_14a_AnExistingLockFileIsMadePrivateWithoutBeingTruncated()
    {
        foreach (var octal in new[] { "0644", "0400", "0200", "0000", "4600", "0700" })
        {
            var path = LockPath("m" + octal + ".lock");
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, Mode(octal));

            using var held = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);

            Assert.True(held is not null, $"mode {octal}: the lock was not taken");
            Assert.True(
                (UnixFileMode.UserRead | UnixFileMode.UserWrite) == ModeOf(path),
                $"mode {octal}: the file ended up as {Convert.ToString((int)ModeOf(path), 8)}");
            Assert.True(1 == new FileInfo(path).Length, $"mode {octal}: the file was truncated");
        }
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public void P1_14b_ASymbolicLinkIsRefusedAndItsTargetIsNotTouched()
    {
        var target = LockPath("target");
        File.WriteAllText(target, "x");
        File.SetUnixFileMode(target, Mode("0644"));
        var link = LockPath("link.lock");
        File.CreateSymbolicLink(link, target);

        var thrown = Assert.Throws<IOException>(() => LinuxFileLock.TryAcquire(link, TimeSpan.Zero));

        Assert.Contains("symbolic link", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(Mode("0644"), ModeOf(target));
        Assert.Equal(1, new FileInfo(target).Length);
    }

    [LinuxOnlyFact("sets and reads real file modes, as root or not")]
    public void P1_14d_AHeldLockStaysHeldWhenSomeoneElseChangesTheMode()
    {
        var path = LockPath();
        var first = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        Assert.NotNull(first);
        File.SetUnixFileMode(path, UnixFileMode.None);

        var second = LinuxFileLock.TryAcquire(path, TimeSpan.FromMilliseconds(200));

        Assert.Null(second);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));

        first.Dispose();
        using var third = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        Assert.NotNull(third);
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_15_AMissingFolderIsReportedNotCreated()
    {
        var path = Path.Combine(_root, "nodir", "x.lock");
        Directory.CreateDirectory(_root);

        Assert.Throws<DirectoryNotFoundException>(() => LinuxFileLock.TryAcquire(path, TimeSpan.Zero));

        Assert.False(Directory.Exists(Path.Combine(_root, "nodir")));
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void P1_16_BadArgumentsAreRefused()
    {
        Assert.Equal("lockPath", Assert.Throws<ArgumentException>(() => LinuxFileLock.TryAcquire("rel.lock", TimeSpan.Zero)).ParamName);
        Assert.Equal("lockPath", Assert.Throws<ArgumentNullException>(() => LinuxFileLock.TryAcquire(null!, TimeSpan.Zero)).ParamName);
        Assert.Equal("lockPath", Assert.Throws<ArgumentException>(() => LinuxFileLock.TryAcquire("   ", TimeSpan.Zero)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxFileLock.TryAcquire(LockPath(), TimeSpan.FromMilliseconds(-1)));
    }

    [LinuxOnlyFact("takes a real lock on a real file")]
    public void TwoDifferentLockFilesDoNotBlockEachOther()
    {
        using var a = LinuxFileLock.TryAcquire(LockPath("a.lock"), TimeSpan.Zero);
        using var b = LinuxFileLock.TryAcquire(LockPath("b.lock"), TimeSpan.Zero);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}
