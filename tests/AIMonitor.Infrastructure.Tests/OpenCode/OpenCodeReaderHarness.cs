using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AIMonitor.Application.OpenCode;
using AIMonitor.Infrastructure.OpenCode;

namespace AIMonitor.Infrastructure.Tests.OpenCode;

/// <summary>
/// Shared fakes for the R-020 reader tests (Docs/SPEC_R020_R021.md F1-F5b). Nothing here starts a
/// process, touches the disk or reads the real environment: PATH/env, file existence, file content and
/// the process start are all injected.
///
/// SEAM ASSUMPTIONS (the spec fixes F2 only as "the process start is injected
/// (Func&lt;ProcessStartInfo, IOpenCodeProcess&gt;) like ICcUsageProcess, and so are the PATH/env/file
/// reads"). Every assumption about the constructor and about IOpenCodeProcess lives in this one file:
/// <see cref="ReaderHarness.CreateReader"/> and <see cref="FakeOpenCodeProcess"/>.
/// </summary>
internal sealed class FakeOpenCodeProcess : IOpenCodeProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeOpenCodeProcess(string output = "", string error = "", int exitCode = 0, bool hang = false)
    {
        Output = new StringReader(output);
        Error = new StringReader(error);
        ExitCode = exitCode;
        Hang = hang;
    }

    public TextReader Output { get; }

    public TextReader Error { get; }

    public int ExitCode { get; }

    public bool Hang { get; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Completed the first time the reader waits for this process to exit.</summary>
    public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        WaitStarted.TrySetResult();
        return Hang ? _exit.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult();
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

    public const string DefaultExe = @"C:\tools\bin\opencode.exe";

    public const string DefaultAuthJson = @"C:\Users\tester\.local\share\opencode\auth.json";

    public static readonly string[] VersionArguments = ["--version"];

    public static readonly string[] ListArguments = ["auth", "list", "--format", "json"];

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _openFailures = new(StringComparer.OrdinalIgnoreCase);

    public ReaderHarness()
    {
        Env["PATH"] = @"C:\tools\bin";
        Env["USERPROFILE"] = @"C:\Users\tester";
        AddFile(DefaultExe, "");
    }

    public Dictionary<string, string?> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ProcessStartInfo> Starts { get; } = [];

    public List<FakeOpenCodeProcess> Processes { get; } = [];

    public List<string> OpenedFiles { get; } = [];

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

    public static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>The ONLY place that builds the reader under test.</summary>
    public OpenCodeAuthReader CreateReader(TimeSpan? versionTimeout = null, TimeSpan? listTimeout = null) =>
        new(
            name =>
            {
                EnvironmentReads.Add(name);
                return Env.TryGetValue(name, out var value) ? value : null;
            },
            path => _files.ContainsKey(Normalize(path)),
            path =>
            {
                var key = Normalize(path);
                OpenedFiles.Add(key);
                if (_openFailures.TryGetValue(key, out var failure))
                {
                    throw failure;
                }

                return new MemoryStream(_files[key], writable: false);
            },
            StartProcess,
            versionTimeout ?? TimeSpan.FromSeconds(30),
            listTimeout ?? TimeSpan.FromSeconds(30));

    public async Task<OpenCodeAuthStatus> ReadAsync(
        TimeSpan? versionTimeout = null,
        TimeSpan? listTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var status = await CreateReader(versionTimeout, listTimeout).ReadAsync(cancellationToken);
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
        Processes.Add(process);
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
