using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using AIMonitor.Infrastructure.Linux;

namespace AIMonitor.Infrastructure.Tests.Linux;

/// <summary>
/// The Linux single-instance coordinator (R-024), driven against real Unix sockets, real lock
/// files and real folder modes in a temp folder of its own, so all of it is Linux-only. Every
/// folder is passed in; nothing here reads or sets an environment variable.
///
/// The folder name is kept short on purpose: a Unix socket path has about 104 bytes to live in.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSingleInstanceTests : IAsyncLifetime
{
    private const string AppId = "app";
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aim" + Guid.NewGuid().ToString("N")[..6]);

    private readonly List<LinuxSingleInstance> _instances = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
            }
        }

        foreach (var instance in _instances)
        {
            try
            {
                await instance.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or UnauthorizedAccessException)
            {
            }
        }

        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Prepend(_root))
        {
            try
            {
                if (new FileInfo(entry).LinkTarget is null && !IsSocket(entry))
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

    private static bool IsSocket(string path) =>
        path.EndsWith(".sock", StringComparison.Ordinal);

    // ---- helpers -------------------------------------------------------------------------------

    private static UnixFileMode Mode(string octal) => (UnixFileMode)Convert.ToInt32(octal, 8);

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & Mode("7777");

    private string Dir(string name) => Path.Combine(_root, name);

    private string MakeDirectory(string name, string octalMode)
    {
        var path = Dir(name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, Mode(octalMode));
        return path;
    }

    private LinuxSingleInstance New(
        string directory,
        TimeSpan? connectTimeout = null,
        TimeSpan? lockTimeout = null,
        TimeSpan? disposeLockTimeout = null)
    {
        var instance = new LinuxSingleInstance(directory, AppId, connectTimeout, lockTimeout, disposeLockTimeout);
        _instances.Add(instance);
        return instance;
    }

    private FileStream HoldLock(LinuxSingleInstance instance)
    {
        var held = LinuxFileLock.TryAcquire(instance.LockPath, TimeSpan.Zero);
        Assert.NotNull(held);
        _disposables.Add(held);
        return held;
    }

    private static Task<string[]> FirstActivation(LinuxSingleInstance instance)
    {
        var source = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.Activated += arguments => source.TrySetResult(arguments);
        return source.Task;
    }

    private static async Task<T> Within<T>(Task<T> task, TimeSpan limit, string what)
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"timed out after {limit.TotalSeconds:0.#} s: {what}");
            throw new InvalidOperationException("unreachable");
        }
    }

    private static async Task Within(Task task, TimeSpan limit, string what)
    {
        try
        {
            await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"timed out after {limit.TotalSeconds:0.#} s: {what}");
        }
    }

    private static async Task PollUntil(Func<bool> condition, TimeSpan limit, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > limit)
            {
                Assert.Fail($"timed out after {limit.TotalSeconds:0.#} s waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<bool> CanConnect(string socketPath)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), limit.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // A leftover socket file nobody listens on: bind, rename the file away from the bound path,
    // then close. Closing unlinks only the bound path, so the renamed file stays behind.
    private static void MakeStaleSocket(string socketPath)
    {
        var bound = Path.Combine(Path.GetDirectoryName(socketPath)!, "x.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(bound));
        socket.Listen(1);
        File.Move(bound, socketPath);
    }

    // ---- constructor ---------------------------------------------------------------------------

    [LinuxOnlyFact("builds the Linux coordinator")]
    public void Defaults_AreFiveSecondsToWaitForTheLockAndTenToGiveItBackOnClose()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LinuxSingleInstance.DefaultLockTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), LinuxSingleInstance.DefaultDisposeLockTimeout);
    }

    [LinuxOnlyFact("builds the Linux coordinator")]
    public void P1_17_ConstructingTouchesNoDisk_AndTheLockFileSitsBesideTheSocket()
    {
        var directory = Dir("s");

        var instance = New(directory);

        Assert.False(Directory.Exists(directory));
        Assert.False(Directory.Exists(_root));
        Assert.Equal(directory, instance.SocketDirectory);
        Assert.EndsWith(".sock", instance.SocketPath, StringComparison.Ordinal);
        Assert.Equal(instance.SocketPath[..^".sock".Length] + ".lock", instance.LockPath);
        Assert.False(instance.HasOwnership);
        Assert.False(instance.IsClosing);
    }

    [LinuxOnlyFact("builds the Linux coordinator")]
    public void Constructor_RefusesBlankArguments_AndNegativeTimeouts_AndAPathThatIsTooLong()
    {
        Assert.Equal("socketDirectory", Assert.Throws<ArgumentNullException>(() => new LinuxSingleInstance(null!, AppId)).ParamName);
        Assert.Equal("scopedAppId", Assert.Throws<ArgumentNullException>(() => new LinuxSingleInstance(Dir("s"), null!)).ParamName);
        Assert.Equal("socketDirectory", Assert.Throws<ArgumentException>(() => new LinuxSingleInstance("  ", AppId)).ParamName);
        Assert.Equal("scopedAppId", Assert.Throws<ArgumentException>(() => new LinuxSingleInstance(Dir("s"), "")).ParamName);

        var negative = TimeSpan.FromMilliseconds(-1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxSingleInstance(Dir("s"), AppId, negative));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxSingleInstance(Dir("s"), AppId, null, negative));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxSingleInstance(Dir("s"), AppId, null, null, negative));

        Assert.Throws<ArgumentException>(() => new LinuxSingleInstance("/" + new string('d', 120), AppId));
        Assert.False(Directory.Exists(_root));
    }

    // ---- taking and keeping ownership ----------------------------------------------------------

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_18_TheFirstInstanceOwnsAPrivateSocketInAPrivateFolder()
    {
        var first = New(Dir("s"));

        Assert.True(await first.TryAcquireAsync());

        Assert.True(first.HasOwnership);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(first.SocketPath));
        Assert.Equal(Mode("0700"), ModeOf(first.SocketDirectory));
        Assert.True(File.Exists(first.LockPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(first.LockPath));
        Assert.True(await CanConnect(first.SocketPath));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_18_AcquiringAgainAsTheOwnerIsStillTrue()
    {
        var first = New(Dir("s"));

        Assert.True(await first.TryAcquireAsync());
        Assert.True(await first.TryAcquireAsync());
        Assert.True(first.HasOwnership);
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_19_AnExistingFolderOpenToOthersIsMadePrivate()
    {
        var directory = MakeDirectory("s", "0755");
        var first = New(directory);

        Assert.True(await first.TryAcquireAsync());

        Assert.Equal(Mode("0700"), ModeOf(directory));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_20_ALeftOverSocketFileNobodyListensOnIsReplaced()
    {
        var first = New(MakeDirectory("s", "0700"));
        MakeStaleSocket(first.SocketPath);
        Assert.True(File.Exists(first.SocketPath));

        Assert.True(await first.TryAcquireAsync());

        Assert.True(first.HasOwnership);
        Assert.True(await CanConnect(first.SocketPath));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_22e_AStaleSocketIsReplacedUnderTheLock_SoTheNewOwnerAnswers()
    {
        var directory = MakeDirectory("s", "0700");
        var first = New(directory);
        MakeStaleSocket(first.SocketPath);

        Assert.True(await first.TryAcquireAsync());

        Assert.True(first.HasOwnership);
        Assert.True(await CanConnect(first.SocketPath));
        var second = New(directory);
        Assert.False(await second.TryAcquireAsync());
    }

    [LinuxOnlyFact("races real launches")]
    public async Task P1_21_TwoLaunchesAtOnceNeverBothOwn()
    {
        for (var round = 0; round < 20; round++)
        {
            var directory = Dir("r" + round);
            var a = New(directory);
            var b = New(directory);

            var results = await Within(
                Task.WhenAll(Task.Run(() => a.TryAcquireAsync().AsTask()), Task.Run(() => b.TryAcquireAsync().AsTask())),
                Ten,
                $"round {round}");

            Assert.True(
                results.Count(won => won) == 1 && results.Count(won => !won) == 1,
                $"round {round}: results were {string.Join(",", results)}");
            Assert.True(
                (a.HasOwnership ? 1 : 0) + (b.HasOwnership ? 1 : 0) == 1,
                $"round {round}: {(a.HasOwnership ? 1 : 0) + (b.HasOwnership ? 1 : 0)} owners");
        }
    }

    // ---- the lock is respected -----------------------------------------------------------------

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_22a_WhileSomeoneElseHoldsTheLock_NothingIsBound_ThenTheNextLaunchOwns()
    {
        var directory = MakeDirectory("s", "0700");
        var waiting = New(directory, lockTimeout: TimeSpan.FromMilliseconds(200));
        var held = HoldLock(waiting);

        var clock = Stopwatch.StartNew();
        var result = await waiting.TryAcquireAsync();
        clock.Stop();

        Assert.True(result);
        Assert.False(waiting.HasOwnership);
        Assert.True(clock.ElapsedMilliseconds >= 150, $"returned after only {clock.ElapsedMilliseconds} ms");
        Assert.False(File.Exists(waiting.SocketPath));

        held.Dispose();
        var owner = New(directory);
        Assert.True(await owner.TryAcquireAsync());

        Assert.True(owner.HasOwnership);
        Assert.False(waiting.HasOwnership);
        Assert.True(File.Exists(owner.SocketPath));
        Assert.True(await CanConnect(owner.SocketPath));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_22b_WhileSomeoneElseHoldsTheLock_AnExistingOwnerIsStillFoundAndNotified()
    {
        var directory = Dir("s");
        var owner = New(directory);
        Assert.True(await owner.TryAcquireAsync());
        var activated = FirstActivation(owner);
        var before = File.GetLastWriteTimeUtc(owner.SocketPath);
        var newcomer = New(directory, lockTimeout: TimeSpan.FromMilliseconds(200));
        HoldLock(newcomer);

        var result = await newcomer.TryAcquireAsync();
        var notified = await newcomer.NotifyExistingInstanceAsync(["--show"]);

        Assert.False(result);
        Assert.False(newcomer.HasOwnership);
        Assert.True(notified);
        Assert.Equal(["--show"], await Within(activated, Ten, "the owner to be activated"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(owner.SocketPath));
        Assert.True(owner.HasOwnership);
        Assert.True(await CanConnect(owner.SocketPath));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_22c_WhileSomeoneElseHoldsTheLock_TheOwnersSocketIsNeverRemoved()
    {
        var directory = Dir("s");
        var owner = New(directory);
        Assert.True(await owner.TryAcquireAsync());
        var before = File.GetLastWriteTimeUtc(owner.SocketPath);
        HoldLock(owner);

        for (var round = 0; round < 20; round++)
        {
            var b = New(directory, lockTimeout: TimeSpan.FromMilliseconds(200));
            var c = New(directory, lockTimeout: TimeSpan.FromMilliseconds(200));

            var results = await Within(
                Task.WhenAll(Task.Run(() => b.TryAcquireAsync().AsTask()), Task.Run(() => c.TryAcquireAsync().AsTask())),
                Ten,
                $"round {round}");

            Assert.True(results.All(won => !won), $"round {round}: results were {string.Join(",", results)}");
            Assert.True(owner.HasOwnership, $"round {round}: the owner lost ownership");
            Assert.False(b.HasOwnership || c.HasOwnership, $"round {round}: a newcomer owns");
            Assert.True(await CanConnect(owner.SocketPath), $"round {round}: the owner no longer answers");
            Assert.True(before == File.GetLastWriteTimeUtc(owner.SocketPath), $"round {round}: the socket file was replaced");
        }
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_22d_AnOwnerThatIsAliveButCannotBeReached_IsNeverReplaced()
    {
        var directory = MakeDirectory("s", "0700");
        var b = New(directory, connectTimeout: TimeSpan.FromMilliseconds(300), lockTimeout: TimeSpan.FromSeconds(1));
        var endpoint = new UnixDomainSocketEndPoint(b.SocketPath);
        var activations = 0;
        b.Activated += _ => Interlocked.Increment(ref activations);

        // The test plays the already-running app: it listens but never accepts.
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(1);

        // Fill the accept queue. Each client that gets in marks itself with 0x01, so it can be told
        // apart later from the client the test sends after the newcomer has returned.
        var old = new List<Socket>();
        var queueFull = false;
        for (var attempt = 0; attempt < 64 && !queueFull; attempt++)
        {
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
            try
            {
                client.Connect(endpoint);
            }
            catch (SocketException)
            {
                client.Dispose();
                queueFull = true;
                break;
            }

            old.Add(client);
            _disposables.Add(client);
            try
            {
                if (client.Send(new byte[] { 0x01 }) != 1)
                {
                    Assert.Fail("could not mark an old client");
                }
            }
            catch (SocketException)
            {
                Assert.Fail("could not mark an old client");
            }
        }

        if (!queueFull)
        {
            Assert.Fail("could not fill the accept queue");
        }

        using var limit = new CancellationTokenSource(Ten);
        var result = await b.TryAcquireAsync(limit.Token);

        Assert.True(result);
        Assert.False(b.HasOwnership);
        Assert.Equal(0, Volatile.Read(ref activations));
        Assert.True(File.Exists(b.SocketPath));

        // A blocking client that stays open until the end of the test and sends 0xFF once it is in.
        var newClient = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _disposables.Add(newClient);
        var sender = Task.Run(async () =>
        {
            await newClient.ConnectAsync(endpoint);
            await newClient.SendAsync(new byte[] { 0xFF });
        });

        var found = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[1];
        try
        {
            while (!found)
            {
                using var accepted = await listener.AcceptAsync(deadline.Token);
                var read = await accepted.ReceiveAsync(buffer, deadline.Token);
                if (read == 0 || buffer[0] == 0x01)
                {
                    continue;
                }

                Assert.True(buffer[0] == 0xFF, $"unexpected byte 0x{buffer[0]:X2} on the test listener");
                found = true;
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("N was not accepted by the test listener");
        }

        await Within(sender, Ten, "the new client to finish sending");
        Assert.True(found);
        using var afterwards = LinuxFileLock.TryAcquire(b.LockPath, TimeSpan.Zero);
        Assert.NotNull(afterwards);
    }

    // ---- notifying the first instance ----------------------------------------------------------

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_23_ASecondLaunchPassesItsArgumentsToTheFirst()
    {
        var directory = Dir("s");
        var first = New(directory);
        Assert.True(await first.TryAcquireAsync());
        var activated = FirstActivation(first);
        var second = New(directory);

        Assert.False(await second.TryAcquireAsync());
        Assert.True(await second.NotifyExistingInstanceAsync(["--tray", "a b"]));

        Assert.Equal(["--tray", "a b"], await Within(activated, Ten, "the first instance to be activated"));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task NotifyingWithNoOwnerAtAllIsFalse()
    {
        var lonely = New(Dir("s"));

        Assert.False(await lonely.NotifyExistingInstanceAsync(["--show"]));
    }

    // ---- folders that cannot be used -----------------------------------------------------------

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public async Task P1_24_ASocketFolderThatIsASymbolicLinkIsOnlyProbed()
    {
        var target = MakeDirectory("target", "0755");
        var link = Dir("link");
        Directory.CreateSymbolicLink(link, target);
        var instance = New(link);

        var result = await instance.TryAcquireAsync();

        Assert.True(result);
        Assert.False(instance.HasOwnership);
        Assert.Equal(Mode("0755"), ModeOf(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [LinuxOnlyFact("symbolic links are a Linux filesystem feature")]
    public async Task P1_24a_ASymbolicLinkGivenWithATrailingSlashIsStillOnlyProbed()
    {
        var target = MakeDirectory("target", "0755");
        var link = Dir("link");
        Directory.CreateSymbolicLink(link, target);
        var instance = New(link + "/");

        var result = await instance.TryAcquireAsync();

        Assert.True(result);
        Assert.False(instance.HasOwnership);
        Assert.Equal(Mode("0755"), ModeOf(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [LinuxOnlyFact("a plain file is a Linux-visible obstacle")]
    public async Task ASocketFolderThatIsAPlainFileIsOnlyProbed()
    {
        Directory.CreateDirectory(_root);
        var file = Dir("file");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, Mode("0644"));
        var instance = New(file);

        Assert.True(await instance.TryAcquireAsync());

        Assert.False(instance.HasOwnership);
        Assert.Equal("x", File.ReadAllText(file));
        Assert.Equal(Mode("0644"), ModeOf(file));
    }

    [LinuxNonRootOnlyFact("root can create folders in a read-only parent")]
    public async Task P1_25_AFolderThatCannotBeCreatedLetsTheAppRunWithoutOwnership()
    {
        var parent = MakeDirectory("ro", "0500");
        var directory = Path.Combine(parent, "s");
        var first = New(directory);
        var second = New(directory);

        var result = await first.TryAcquireAsync();

        Assert.True(result);
        Assert.False(first.HasOwnership);
        Assert.False(await second.NotifyExistingInstanceAsync(["--show"]));
        Assert.False(Directory.Exists(directory));
    }

    // ---- closing -------------------------------------------------------------------------------

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_26_DisposingTheOwnerRemovesTheSocketButKeepsTheLockFile()
    {
        var owner = New(Dir("s"));
        Assert.True(await owner.TryAcquireAsync());

        await owner.DisposeAsync();
        await owner.DisposeAsync();

        Assert.False(File.Exists(owner.SocketPath));
        Assert.True(File.Exists(owner.LockPath));
        Assert.False(owner.HasOwnership);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await owner.TryAcquireAsync());
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_26_DisposingAnInstanceThatNeverOwnedChangesNothing()
    {
        var directory = Dir("s");
        var owner = New(directory);
        Assert.True(await owner.TryAcquireAsync());
        var other = New(directory);
        Assert.False(await other.TryAcquireAsync());

        await other.DisposeAsync();

        Assert.True(File.Exists(owner.SocketPath));
        Assert.True(await CanConnect(owner.SocketPath));
        Assert.True(owner.HasOwnership);
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_26a_DisposeWaitsForTheLock_BeforeItClosesTheListener()
    {
        var directory = Dir("s");
        var owner = New(directory, disposeLockTimeout: TimeSpan.FromSeconds(10));
        Assert.True(await owner.TryAcquireAsync());
        var held = HoldLock(owner);

        var disposing = Task.Run(() => owner.DisposeAsync().AsTask());
        await PollUntil(() => !owner.HasOwnership, TimeSpan.FromSeconds(5), "dispose to start");
        await Task.Delay(300);

        Assert.False(disposing.IsCompleted);
        Assert.True(File.Exists(owner.SocketPath));
        Assert.True(await CanConnect(owner.SocketPath));

        held.Dispose();
        await Within(disposing, Ten, "dispose to finish once the lock is free");

        Assert.False(File.Exists(owner.SocketPath));
        var b = New(directory);
        Assert.True(await b.TryAcquireAsync());
        Assert.True(b.HasOwnership);
        var activated = FirstActivation(b);
        var c = New(directory);
        Assert.False(await c.TryAcquireAsync());
        Assert.True(await c.NotifyExistingInstanceAsync(["x"]));
        Assert.Equal(["x"], await Within(activated, Ten, "the new owner to be activated"));
    }

    [LinuxOnlyFact("races real launches; a statistical check, not a proof")]
    public async Task P1_26b_ClosingTheOwnerWhileANewcomerArrivesLeavesExactlyOneOwner()
    {
        for (var round = 0; round < 30; round++)
        {
            var directory = Dir("r" + round);
            var x = New(directory);
            Assert.True(await x.TryAcquireAsync());
            var y = New(directory);
            var z = New(directory);
            var yActivated = FirstActivation(y);
            var zActivated = FirstActivation(z);

            await Within(
                Task.WhenAll(Task.Run(() => x.DisposeAsync().AsTask()), Task.Run(() => y.TryAcquireAsync().AsTask())),
                Ten,
                $"round {round}: dispose and acquire");
            await Within(z.TryAcquireAsync().AsTask(), Ten, $"round {round}: the third launch");

            Assert.False(y.HasOwnership && z.HasOwnership, $"round {round}: two owners");
            Assert.True(y.HasOwnership || z.HasOwnership, $"round {round}: no owner");
            var w = New(directory);
            Assert.True(await w.NotifyExistingInstanceAsync(["r"]), $"round {round}: nobody answered");
            var owner = y.HasOwnership ? yActivated : zActivated;
            Assert.Equal(["r"], await Within(owner, Ten, $"round {round}: the owner to be activated"));
        }
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_26c_DisposeThatCannotGetTheLockLeavesTheListenerOpen_AndCanBeRetried()
    {
        var owner = New(Dir("s"), disposeLockTimeout: TimeSpan.FromMilliseconds(200));
        Assert.True(await owner.TryAcquireAsync());
        var held = HoldLock(owner);

        var clock = Stopwatch.StartNew();
        await Within(Task.Run(() => owner.DisposeAsync().AsTask()), TimeSpan.FromSeconds(5), "dispose to give up");
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds >= 150, $"gave up after only {clock.ElapsedMilliseconds} ms");
        Assert.False(owner.HasOwnership);
        Assert.True(File.Exists(owner.SocketPath));
        Assert.True(await CanConnect(owner.SocketPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await owner.TryAcquireAsync());

        held.Dispose();
        await owner.DisposeAsync();

        Assert.False(File.Exists(owner.SocketPath));
    }

    [LinuxOnlyFact("drives a real Unix socket")]
    public async Task P1_26d_DisposeDuringAnAcquireOfTheSameInstanceWaitsForItToFinish()
    {
        var directory = MakeDirectory("s", "0755");
        var instance = New(directory, lockTimeout: TimeSpan.FromSeconds(5));
        var held = HoldLock(instance);

        var acquiring = Task.Run(() => instance.TryAcquireAsync().AsTask());
        await PollUntil(() => ModeOf(directory) == Mode("0700"), TimeSpan.FromSeconds(5), "the acquire to reach the lock");
        var disposing = Task.Run(() => instance.DisposeAsync().AsTask());
        await PollUntil(() => instance.IsClosing, TimeSpan.FromSeconds(5), "dispose to start");
        await Task.Delay(300);

        Assert.False(acquiring.IsCompleted);
        Assert.False(disposing.IsCompleted);

        held.Dispose();
        Assert.True(await Within(acquiring, Ten, "the acquire that had started to finish"));
        await Within(disposing, Ten, "dispose to finish after the acquire");

        Assert.False(instance.HasOwnership);
        Assert.False(File.Exists(instance.SocketPath));
        var next = New(directory);
        Assert.True(await next.TryAcquireAsync());
        Assert.True(next.HasOwnership);
    }
}
