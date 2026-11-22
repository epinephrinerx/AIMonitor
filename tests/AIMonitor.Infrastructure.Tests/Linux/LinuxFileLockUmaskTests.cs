using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// L5(5): a lock file whose mode the process umask trimmed at creation is normalized to 0600 once
/// the lock is held. The umask is process-wide, so this class joins the collection that runs with
/// no other test in parallel, changes it only around the call under test and always restores it.
///
/// Not observable (L5(3), initial creation mode no wider than 0600): the file is created with
/// 0600 and normalized again before <c>TryAcquire</c> returns, so only the final mode can be seen.
/// A creation mode of 0640 followed by the post-lock normalization leaves the same 0600, and a
/// concurrent observer cannot be made deterministic; no test is written for it.
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LinuxFileLockUmaskTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetUmask(uint mask);

    [LinuxOnlyFact("changes the process umask and reads a real file mode")]
    public void P1_12a_ANewLockFileWhoseCreationModeTheUmaskTrimmedEndsUpAs0600()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "a.lock");
        var previous = SetUmask(Convert.ToUInt32("0277", 8)); // 0600 & ~0277 = 0400 at creation
        FileStream? held;
        try
        {
            held = LinuxFileLock.TryAcquire(path, TimeSpan.Zero);
        }
        finally
        {
            SetUmask(previous);
        }

        using (held)
        {
            Assert.NotNull(held);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & (UnixFileMode)Convert.ToInt32("7777", 8));
        }
    }
}
