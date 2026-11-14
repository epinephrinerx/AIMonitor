using System.ComponentModel;
using System.Globalization;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// Exercises <see cref="CodexAppServerLauncher"/>'s process orchestration - disposable temporary
/// home, environment sanitization, the fixed call sequence, and cleanup - against a hand-written fake
/// <see cref="ICodexProcess"/>. Never launches a real subprocess.
/// </summary>
[Trait("Category", "Contract")]
public sealed class CodexAppServerLauncherTests
{
    /// <summary>A stdout that never produces a byte, simulating a process that has not answered yet.
    /// Overrides <see cref="ReadAsync(Memory{char}, CancellationToken)"/> specifically, since that is
    /// what <see cref="CodexJsonRpcClient"/>'s bounded line reader actually calls - overriding the
    /// no-longer-called <c>ReadLineAsync()</c> here would fall through to the base implementation's
    /// immediate EOF instead of hanging as intended.</summary>
    private sealed class NeverRespondingReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken) =>
            new(new TaskCompletionSource<int>().Task);
    }

    private sealed class FakeCodexProcess : ICodexProcess
    {
        private readonly TextReader _stdout;

        public FakeCodexProcess(string scriptedStdout) => _stdout = new StringReader(scriptedStdout);

        public FakeCodexProcess() => _stdout = new NeverRespondingReader();

        public StringWriter StdinWriter { get; } = new();

        public int KillCallCount { get; private set; }

        public int WaitForExitCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public TextWriter StandardInput => StdinWriter;

        public TextReader StandardOutput => _stdout;

        public bool HasExited { get; private set; }

        /// <summary>When set, <see cref="Kill"/> throws this after still recording the call - used to
        /// prove cleanup survives a throwing step and still runs the ones after it.</summary>
        public Exception? ThrowOnKill { get; init; }

        public Exception? ThrowOnWaitForExit { get; init; }

        public Exception? ThrowOnDispose { get; init; }

        public void Kill()
        {
            KillCallCount++;
            HasExited = true;
            if (ThrowOnKill is not null)
            {
                throw ThrowOnKill;
            }
        }

        public Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            WaitForExitCallCount++;
            return ThrowOnWaitForExit is not null ? Task.FromException<bool>(ThrowOnWaitForExit) : Task.FromResult(true);
        }

        public void Dispose()
        {
            DisposeCallCount++;
            if (ThrowOnDispose is not null)
            {
                throw ThrowOnDispose;
            }
        }
    }

    /// <summary>Creates temporary "home" directories under its own isolated root rather than the real,
    /// globally-shared OS temp directory, and counts calls - so a test can assert "no temp directory
    /// was created" (or how many were) directly, without scanning a shared glob that other tests or
    /// processes could race with (finding #9).</summary>
    private sealed class CountingTemporaryHomeFactory : IDisposable
    {
        private readonly string _root =
            Directory.CreateTempSubdirectory("aimonitor-codex-launcher-tests-").FullName;

        public int CallCount { get; private set; }

        public string Create()
        {
            CallCount++;
            return Directory.CreateDirectory(Path.Combine(_root, "call-" + CallCount.ToString(CultureInfo.InvariantCulture))).FullName;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static string ScriptedStdout(bool includeUsage, string rateLimitsResult = "{}", string usageResult = "{}")
    {
        var lines = new List<string>
        {
            """{"id":1,"result":{}}""", // initialize
            """{"id":2,"result":{}}""", // account/login/start
            $$"""{"id":3,"result":{{rateLimitsResult}}}""", // account/rateLimits/read
        };
        if (includeUsage)
        {
            lines.Add($$"""{"id":4,"result":{{usageResult}}}"""); // account/usage/read
        }

        return string.Join('\n', lines) + "\n";
    }

    private static OpenAiCredential ValidCredential(string token = "secret-token-value") =>
        new(OpenAiCredentialKind.OAuth, token, accountId: "acct-1");

    [Fact]
    public async Task RunAsync_Success_ReturnsRateLimitsAndUsageAndCleansUpProcess()
    {
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request => fakeProcess = new FakeCodexProcess(
                ScriptedStdout(includeUsage: true, rateLimitsResult: """{"ok":"limits"}""", usageResult: """{"ok":"usage"}""")),
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: true, CancellationToken.None);

        Assert.Equal("limits", result.RateLimits.GetProperty("ok").GetString());
        Assert.Equal("usage", result.Usage!.Value.GetProperty("ok").GetString());
        Assert.Null(result.HistoryError);
        Assert.Equal(1, fakeProcess!.KillCallCount);
        Assert.Equal(1, fakeProcess.DisposeCallCount);
    }

    [Fact]
    public async Task RunAsync_WithoutHistoryRequested_NeverCallsUsageRead()
    {
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => new FakeCodexProcess(ScriptedStdout(includeUsage: false)),
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None);

        Assert.Null(result.Usage);
        Assert.Null(result.HistoryError);
    }

    [Fact]
    public async Task RunAsync_CreatesUniqueTemporaryHomeAndDeletesItAfterward()
    {
        string? capturedWorkingDirectory = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                capturedWorkingDirectory = request.WorkingDirectory;
                Assert.True(Directory.Exists(request.WorkingDirectory));
                return new FakeCodexProcess(ScriptedStdout(includeUsage: false));
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        await launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None);

        Assert.NotNull(capturedWorkingDirectory);
        Assert.False(Directory.Exists(capturedWorkingDirectory));
    }

    [Fact]
    public async Task RunAsync_NeverPutsTheAccessTokenIntoTheChildsEnvironment()
    {
        CodexProcessStartRequest? capturedRequest = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                capturedRequest = request;
                return new FakeCodexProcess(ScriptedStdout(includeUsage: false));
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?> { ["PATH"] = "x", ["UNRELATED"] = "keep-me" });

        await launcher.RunAsync(ValidCredential("super-secret-token"), includeHistory: false, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.DoesNotContain(capturedRequest!.Environment.Values, value => value == "super-secret-token");
        Assert.Equal("keep-me", capturedRequest.Environment["UNRELATED"]);
        Assert.Equal(capturedRequest.WorkingDirectory, capturedRequest.Environment["CODEX_HOME"]);
    }

    [Fact]
    public async Task RunAsync_SendsTheAccessTokenOnlyThroughStdinLoginCall_NeverAsAnArgument()
    {
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => fakeProcess = new FakeCodexProcess(ScriptedStdout(includeUsage: false)),
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        await launcher.RunAsync(ValidCredential("super-secret-token"), includeHistory: false, CancellationToken.None);

        var sent = fakeProcess!.StdinWriter.ToString();
        Assert.Contains("\"accessToken\":\"super-secret-token\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"chatgptAccountId\":\"acct-1\"", sent, StringComparison.Ordinal);
        // CodexProcessStartRequest carries only an executable path, working directory, and
        // environment - there is no argument list field a credential could ever flow through.
        Assert.DoesNotContain("refreshToken", sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_IncompleteCredential_ThrowsWithoutLaunchingAnyProcess()
    {
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => throw new InvalidOperationException("must not launch a process"),
            executableLocator: () => @"C:\fake\codex.exe");

        var credential = new OpenAiCredential(OpenAiCredentialKind.OAuth, "token", accountId: string.Empty);

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(credential, includeHistory: false, CancellationToken.None));

        Assert.True(ex.Unauthorized);
    }

    [Fact]
    public async Task RunAsync_ExecutableNotFound_ThrowsWithoutLaunchingAnyProcessOrCreatingATempDirectory()
    {
        using var temporaryHomes = new CountingTemporaryHomeFactory();
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => throw new InvalidOperationException("must not launch a process"),
            executableLocator: () => null,
            temporaryHomeFactory: temporaryHomes.Create);

        await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None));

        Assert.Equal(0, temporaryHomes.CallCount);
    }

    [Fact]
    public async Task RunAsync_ProcessFailsToStart_DeletesTempDirectoryAndThrowsASafeMessage()
    {
        string? workingDirectory = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                throw new Win32Exception("no such file or directory");
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None));

        Assert.DoesNotContain("no such file", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(workingDirectory);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_UsageCallFails_ReturnsHistoryErrorButStillReturnsRateLimits()
    {
        var lines = string.Join('\n',
        [
            """{"id":1,"result":{}}""",
            """{"id":2,"result":{}}""",
            """{"id":3,"result":{"ok":"limits"}}""",
            """{"id":4,"error":{"message":"boom"}}""",
        ]) + "\n";
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => new FakeCodexProcess(lines),
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: true, CancellationToken.None);

        Assert.Equal("limits", result.RateLimits.GetProperty("ok").GetString());
        Assert.Null(result.Usage);
        Assert.NotNull(result.HistoryError);
        Assert.DoesNotContain("boom", result.HistoryError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RateLimitsCallFails_ThrowsRatherThanReturningAPartialResult()
    {
        var lines = string.Join('\n',
        [
            """{"id":1,"result":{}}""",
            """{"id":2,"result":{}}""",
            """{"id":3,"error":{"message":"boom"}}""",
        ]) + "\n";
        var launcher = new CodexAppServerLauncher(
            processFactory: _ => new FakeCodexProcess(lines),
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_Cancelled_PropagatesCancellationAndStillKillsAndCleansUp()
    {
        FakeCodexProcess? fakeProcess = null;
        string? workingDirectory = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return fakeProcess = new FakeCodexProcess();
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>(),
            callTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, cts.Token));

        Assert.Equal(1, fakeProcess!.KillCallCount);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_CallTimesOut_ThrowsSafeMessageAndStillCleansUp()
    {
        string? workingDirectory = null;
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return fakeProcess = new FakeCodexProcess();
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>(),
            callTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fakeProcess!.KillCallCount);
        Assert.False(Directory.Exists(workingDirectory));
    }

    // -- Cleanup resilience (finding #6): Kill/WaitForExitAsync/Dispose throwing must never mask the
    // primary result and must never skip a later cleanup step. --------------------------------------

    [Fact]
    public async Task RunAsync_KillThrows_StillDisposesProcessAndDeletesTempHomeAndReturnsThePrimaryResult()
    {
        string? workingDirectory = null;
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return fakeProcess = new FakeCodexProcess(ScriptedStdout(includeUsage: false))
                {
                    ThrowOnKill = new InvalidOperationException("kill boom"),
                };
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None);

        Assert.Equal("{}", result.RateLimits.GetRawText());
        Assert.Equal(1, fakeProcess!.KillCallCount);
        Assert.Equal(1, fakeProcess.WaitForExitCallCount);
        Assert.Equal(1, fakeProcess.DisposeCallCount);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_WaitForExitThrows_StillDisposesProcessAndDeletesTempHome()
    {
        string? workingDirectory = null;
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return fakeProcess = new FakeCodexProcess(ScriptedStdout(includeUsage: false))
                {
                    ThrowOnWaitForExit = new InvalidOperationException("wait boom"),
                };
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None);

        Assert.Equal("{}", result.RateLimits.GetRawText());
        Assert.Equal(1, fakeProcess!.DisposeCallCount);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_DisposeThrows_StillDeletesTempHomeWithoutMaskingThePrimaryResult()
    {
        string? workingDirectory = null;
        FakeCodexProcess? fakeProcess = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return fakeProcess = new FakeCodexProcess(ScriptedStdout(includeUsage: false))
                {
                    ThrowOnDispose = new InvalidOperationException("dispose boom"),
                };
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var result = await launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None);

        Assert.Equal("{}", result.RateLimits.GetRawText());
        Assert.Equal(1, fakeProcess!.DisposeCallCount);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_AllCleanupStepsThrow_StillDeletesTempHomeAndDoesNotMaskThePrimaryException()
    {
        // The primary failure here is the rate-limits call erroring - proves that even when every
        // single cleanup step also throws, the original CodexAppServerException from the try block
        // (not one of the cleanup exceptions) is what callers see.
        var lines = string.Join('\n',
        [
            """{"id":1,"result":{}}""",
            """{"id":2,"result":{}}""",
            """{"id":3,"error":{"message":"boom"}}""",
        ]) + "\n";
        string? workingDirectory = null;
        var launcher = new CodexAppServerLauncher(
            processFactory: request =>
            {
                workingDirectory = request.WorkingDirectory;
                return new FakeCodexProcess(lines)
                {
                    ThrowOnKill = new InvalidOperationException("kill boom"),
                    ThrowOnWaitForExit = new InvalidOperationException("wait boom"),
                    ThrowOnDispose = new InvalidOperationException("dispose boom"),
                };
            },
            executableLocator: () => @"C:\fake\codex.exe",
            environmentProvider: () => new Dictionary<string, string?>());

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(
            () => launcher.RunAsync(ValidCredential(), includeHistory: false, CancellationToken.None));

        Assert.DoesNotContain("boom", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workingDirectory));
    }
}
