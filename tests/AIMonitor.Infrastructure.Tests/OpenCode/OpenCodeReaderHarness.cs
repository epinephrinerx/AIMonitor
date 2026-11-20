using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AIMonitor.Application.OpenCode;
using AIMonitor.Infrastructure.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// Shared fakes for the R-020 reader tests (Docs/SPEC_R020_R021.md F1-F5b, fix round 1 H1-H15). Nothing here
/// starts a process, touches the disk or reads the real environment: PATH/env, file content and the process
/// start are all injected.
///
/// SEAM (fix round 1). The constructor is
/// <c>(Func&lt;string?&gt; path, Func&lt;string,string?&gt; environment,
/// Func&lt;string,int,CancellationToken,Task&lt;byte[]?&gt;&gt; readFile,
/// Func&lt;ProcessStartInfo,IOpenCodeProcess&gt; start, TimeSpan versionDeadline, TimeSpan authListDeadline)</c>;
/// <c>readFile(path, maxBytes, ct)</c> returns null when the file is missing, otherwise at most
/// <c>maxBytes</c> bytes (the fake truncates, like a bounded read does); the reader passes 1 MB + 1. The old
/// <c>fileLength</c> seam is gone. The static <c>OpenCodeAuthReader.ResolveExecutable(string? searchPath,
/// Func&lt;string,bool&gt; fileExists)</c> is unchanged. Every assumption about the constructor and about
/// IOpenCodeProcess lives in this one file: <see cref="ReaderHarness.CreateReader"/> and
/// <see cref="FakeOpenCodeProcess"/>. The harness deadlines default to 30 s (never reached by a passing test);
/// timeout tests set a short deadline (H15), so no test waits for the production 10 s / 20 s.
/// </summary>
internal interface IKillAware
{
    void OnKilled();
}

/// <summary>Base for scripted streams: every read overload funnels into <see cref="ReadCoreAsync"/>.</summary>
internal abstract class ScriptedReader : TextReader, IKillAware
{
    private volatile bool _killed;

    protected bool Killed => _killed;

    public void OnKilled()
    {
        _killed = true;
        Wake();
    }

    protected abstract Task<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken);

    protected virtual void Wake()
    {
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
        new(ReadCoreAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
        ReadCoreAsync(buffer.AsMemory(index, count), CancellationToken.None);

    public override int Read(char[] buffer, int index, int count) =>
        ReadCoreAsync(buffer.AsMemory(index, count), CancellationToken.None).GetAwaiter().GetResult();

    public override int Read(Span<char> buffer)
    {
        var temp = new char[buffer.Length];
        var n = ReadCoreAsync(temp, CancellationToken.None).GetAwaiter().GetResult();
        temp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }
}

/// <summary>Stdout/stderr that keeps producing data asynchronously (every read yields first) until the
/// process is killed or the token is canceled: a process that floods past the output cap.</summary>
internal sealed class FloodingReader : ScriptedReader
{
    protected override async Task<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
    {
        if (Killed)
        {
            return 0;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (Killed)
        {
            return 0;
        }

        var n = Math.Min(buffer.Length, 2048);
        buffer.Span[..n].Fill('x');
        return n;
    }
}

/// <summary>A stream that never produces anything and ends only when the process is killed. With
/// <c>honorCancellation: false</c> it also ignores the cancellation token, like a pipe whose read cannot be
/// interrupted: only the kill unblocks it.</summary>
internal sealed class StallingReader(bool honorCancellation) : ScriptedReader
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
    {
        if (honorCancellation)
        {
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _released.Task.ConfigureAwait(false);
        }

        return 0;
    }

    protected override void Wake() => _released.TrySetResult();
}

/// <summary>A stream that never produces anything and never ends: it ignores its token AND the kill (J1: a pipe
/// whose read cannot be interrupted, held open by a grandchild that survives the kill).</summary>
internal sealed class NeverEndingReader : ScriptedReader
{
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
    {
        await _never.Task.ConfigureAwait(false);
        return 0;
    }
}

internal sealed class FakeOpenCodeProcess : IOpenCodeProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeOpenCodeProcess(string output = "", string error = "", int exitCode = 0, bool hang = false)
        : this(new StringReader(output), new StringReader(error), exitCode, hang)
    {
    }

    public FakeOpenCodeProcess(TextReader output, TextReader error, int exitCode = 0, bool hang = false, bool exitIgnoresCancellation = false)
    {
        Output = output;
        Error = error;
        ExitCode = exitCode;
        Hang = hang;
        ExitIgnoresCancellation = exitIgnoresCancellation;
    }

    public TextReader Output { get; }

    public TextReader Error { get; }

    public int ExitCode { get; }

    public bool Hang { get; }

    /// <summary>J1: the wait for exit never completes and ignores its token, even after the kill (the process object
    /// is wedged); only the reader's own bound can end the wait.</summary>
    public bool ExitIgnoresCancellation { get; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Completed the first time the reader waits for this process to exit.</summary>
    public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        WaitStarted.TrySetResult();
        if (ExitIgnoresCancellation)
        {
            return new TaskCompletionSource().Task;
        }

        return Hang ? _exit.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult();
        (Output as IKillAware)?.OnKilled();
        (Error as IKillAware)?.OnKilled();
    }

    public void Dispose()
    {
        Disposed = true;
        Output.Dispose();
        Error.Dispose();
    }
}

internal sealed class ReaderHarness
{
    public const string ExpectedErrorText = "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check.";

    // Paths are this OS's own: a C:\ path is not rooted on macOS, so the reader would rightly refuse it.
    public static readonly string DefaultBin = OperatingSystem.IsWindows() ? @"C:\tools\bin" : "/tools/bin";

    public static readonly string DefaultProfile = OperatingSystem.IsWindows() ? @"C:\Users\tester" : "/Users/tester";

    public static readonly string XdgRoot = OperatingSystem.IsWindows() ? @"D:\xdg\data" : "/xdg/data";

    public static readonly string DefaultExe = Path.Combine(DefaultBin, OpenCodeAuthReader.ExecutableFileNames[0]);

    public static readonly string DefaultAuthJson = Path.Combine(DefaultProfile, ".local", "share", "opencode", "auth.json");

    public static readonly string[] VersionArguments = ["--version"];

    public static readonly string[] ListArguments = ["auth", "list", "--format", "json"];

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _openFailures = new(StringComparer.OrdinalIgnoreCase);

    public ReaderHarness()
    {
        Env["PATH"] = DefaultBin;
        Env[OpenCodeAuthReader.ProfileVariable] = DefaultProfile;
        AddFile(DefaultExe, "");
    }

    public Dictionary<string, string?> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ProcessStartInfo> Starts { get; } = [];

    public List<FakeOpenCodeProcess> Processes { get; } = [];

    private readonly object _processGate = new();
    private readonly List<TaskCompletionSource<FakeOpenCodeProcess>> _processStarted = [];

    /// <summary>Completes when the reader has started the process with this index (0 = --version, 1 = auth list)
    /// and the fake has been asked to wait for its exit: a signal, not a poll.</summary>
    public async Task<FakeOpenCodeProcess> WaitForProcessAsync(int index)
    {
        Task<FakeOpenCodeProcess> started;
        lock (_processGate)
        {
            while (_processStarted.Count <= index)
            {
                _processStarted.Add(new TaskCompletionSource<FakeOpenCodeProcess>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            started = _processStarted[index].Task;
        }

        var process = await started.WaitAsync(TimeSpan.FromSeconds(30));
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return process;
    }

    /// <summary>Paths whose content was read (readFile calls, also those that found no file).</summary>
    public List<string> OpenedFiles { get; } = [];

    /// <summary>The <c>maxBytes</c> argument of every readFile call (H4: always 1 MB + 1).</summary>
    public List<int> ReadLimits { get; } = [];

    /// <summary>Every path the reader touched at all, distinct.</summary>
    public IReadOnlyList<string> AccessedPaths =>
        OpenedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>H15: injected deadlines. 30 s: never reached by a passing test; timeout tests set a short one.</summary>
    public TimeSpan VersionDeadline { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan AuthListDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Replaces the whole readFile seam (a read that blocks, throws or over-returns).</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? ReadOverride { get; set; }

    public List<string> EnvironmentReads { get; } = [];

    /// <summary>Arguments of any start that is neither `--version` nor `auth list --format json`.</summary>
    public List<string> UnexpectedCommands { get; } = [];

    /// <summary>What `opencode --version` does. Default: OpenCode 1.19.5 (the 1.x line of the spec facts).</summary>
    public Func<FakeOpenCodeProcess> OnVersion { get; set; } = () => new FakeOpenCodeProcess("1.19.5\n");

    /// <summary>What `opencode auth list --format json` does. Default: an empty 2.x list.</summary>
    public Func<FakeOpenCodeProcess> OnList { get; set; } = () => new FakeOpenCodeProcess("[]");

    public void AddFile(string path, string content) => _files[Normalize(path)] = Encoding.UTF8.GetBytes(content);

    public void AddFile(string path, byte[] content) => _files[Normalize(path)] = content;

    public void RemoveFile(string path) => _files.Remove(Normalize(path));

    public void FailOpen(string path, Exception failure) => _openFailures[Normalize(path)] = failure;

    public void SetAuthJson(string json) => AddFile(DefaultAuthJson, json);

    public void SetVersion(string text) => OnVersion = () => new FakeOpenCodeProcess(text);

    public void SetListJson(string json) => OnList = () => new FakeOpenCodeProcess(json);

    /// <summary>Installs OpenCode 2.x whose `auth list` prints <paramref name="json"/>.</summary>
    public void UseTwoX(string json)
    {
        SetVersion("2.2.7\n");
        SetListJson(json);
    }

    // Only Windows treats / and \ as the same separator; on macOS a backslash is part of a name.
    public static string Normalize(string path) => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <summary>The ONLY place that builds the reader under test.</summary>
    public OpenCodeAuthReader CreateReader() =>
        new(
            () => OpenCodeAuthReader.ResolveExecutable(
                Env.TryGetValue("PATH", out var searchPath) ? searchPath : null,
                path => _files.ContainsKey(Normalize(path))),
            name =>
            {
                EnvironmentReads.Add(name);
                return Env.TryGetValue(name, out var value) ? value : null;
            },
            ReadFileAsync,
            StartProcess,
            VersionDeadline,
            AuthListDeadline);

    private Task<byte[]?> ReadFileAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var key = Normalize(path);
        OpenedFiles.Add(key);
        ReadLimits.Add(maxBytes);
        if (ReadOverride is not null)
        {
            return ReadOverride(key, maxBytes, cancellationToken);
        }

        if (_openFailures.TryGetValue(key, out var failure))
        {
            return Task.FromException<byte[]?>(failure);
        }

        if (!_files.TryGetValue(key, out var bytes))
        {
            return Task.FromResult<byte[]?>(null); // missing file
        }

        // A bounded read never returns more than the limit, whatever the file holds.
        return Task.FromResult<byte[]?>(bytes.Length <= maxBytes ? bytes : bytes[..maxBytes]);
    }

    public async Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        var status = await CreateReader().ReadAsync(cancellationToken);
        AssertOnlyAllowedCommands();
        return status;
    }

    /// <summary>F2: never any other subcommand, never --standalone, never `auth export`.</summary>
    public void AssertOnlyAllowedCommands()
    {
        Assert.Empty(UnexpectedCommands);
        foreach (var start in Starts)
        {
            var arguments = start.ArgumentList.ToArray();
            Assert.True(
                arguments.SequenceEqual(VersionArguments) || arguments.SequenceEqual(ListArguments),
                "Unexpected opencode arguments: " + string.Join(' ', arguments));
            Assert.DoesNotContain("--standalone", arguments);
            Assert.DoesNotContain("export", arguments);
        }
    }

    private IOpenCodeProcess StartProcess(ProcessStartInfo info)
    {
        Starts.Add(info);
        var arguments = info.ArgumentList.ToArray();

        Func<FakeOpenCodeProcess> script;
        if (arguments.SequenceEqual(VersionArguments))
        {
            script = OnVersion;
        }
        else if (arguments.SequenceEqual(ListArguments))
        {
            script = OnList;
        }
        else
        {
            UnexpectedCommands.Add(string.Join(' ', arguments));
            throw new InvalidOperationException("unexpected command");
        }

        var process = script();
        lock (_processGate)
        {
            Processes.Add(process);
            var index = Processes.Count - 1;
            while (_processStarted.Count <= index)
            {
                _processStarted.Add(new TaskCompletionSource<FakeOpenCodeProcess>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            _processStarted[index].TrySetResult(process);
        }

        return process;
    }

    public static Exception StartFailure() => new Win32Exception(2, "synthetic start failure");

    // ── result helpers ────────────────────────────────────────────────────────────────

    public static string[] Pairs(OpenCodeAuthStatus status) =>
        status.SignIns.Select(s => s.Name + "|" + s.Kind).ToArray();

    /// <summary>Everything a user or a log could see from the status object.</summary>
    public static string AllText(OpenCodeAuthStatus status) =>
        string.Join(
            "\n",
            status.Source,
            status.Error,
            status.ToString(),
            string.Join("\n", status.SignIns.Select(s => s.Name + "\n" + s.Kind + "\n" + s.ToString())));

    public static void AssertNoLeak(OpenCodeAuthStatus status, params string[] needles)
    {
        var text = AllText(status);
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
