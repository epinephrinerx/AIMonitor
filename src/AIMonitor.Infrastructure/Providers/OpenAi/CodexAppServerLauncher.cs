using System.ComponentModel;
using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Real <see cref="ICodexAppServerLauncher"/>. Runs the Codex CLI's <c>codex app-server</c> as a
/// hidden child process under a disposable temporary <c>CODEX_HOME</c> that is always deleted before
/// this call returns, sends only the access token (through stdin, as part of the protocol - never a
/// process argument, environment variable, or log line), and never touches the user's real Codex
/// login or config. Every dependency (process launch, executable discovery, environment snapshot) is
/// injectable so tests exercise this orchestration without a real subprocess.
/// </summary>
internal sealed class CodexAppServerLauncher : ICodexAppServerLauncher
{
    private const string ClientName = "aimonitor-csharp";
    private const string IncompleteLoginMessage = "Codex login is incomplete. Sign in to Codex again.";
    private const string ExecutableNotFoundMessage =
        "Codex executable was not found. Install Codex CLI or the Codex desktop app, then restart this monitor.";
    private const string CommunicationFailedMessage = "Could not start or communicate with Codex App Server.";
    private const string HistoryUnavailablePrefix = "Token history unavailable. ";
    private const string TemporaryHomePrefix = "aimonitor-codex-";

    // The one version string that leaves this machine in a Codex support report - resolved from this
    // assembly's own version rather than a second hardcoded literal that could drift from it.
    private static readonly string ClientVersion = typeof(CodexAppServerLauncher).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly Func<CodexProcessStartRequest, ICodexProcess> _processFactory;
    private readonly Func<string?> _executableLocator;
    private readonly Func<IReadOnlyDictionary<string, string?>> _environmentProvider;
    private readonly Func<string> _temporaryHomeFactory;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _shutdownGrace;

    /// <param name="processFactory">Defaults to launching a real <see cref="CodexProcess"/>; tests
    /// inject a hand-written fake instead of a real subprocess.</param>
    /// <param name="executableLocator">Defaults to <see cref="CodexExecutableLocator.LocateOnThisMachine"/>.</param>
    /// <param name="environmentProvider">Defaults to a snapshot of the real process environment; tests
    /// inject a fixed map so environment sanitization is verified deterministically.</param>
    /// <param name="callTimeout">Per-call timeout, overriding the 25-second production default
    /// (matching the Python baseline).</param>
    /// <param name="shutdownGrace">How long to wait for the child to exit after being killed, before
    /// giving up and deleting its temporary home anyway. Overrides a 3-second production default.</param>
    /// <param name="temporaryHomeFactory">Creates the disposable temporary <c>CODEX_HOME</c> and
    /// returns its full path. Defaults to <see cref="Directory.CreateTempSubdirectory(string?)"/>
    /// under the real OS temp root; tests inject a counting/isolated fake so "no temp directory was
    /// created" can be asserted directly instead of scanning the real, globally-shared temp root.</param>
    public CodexAppServerLauncher(
        Func<CodexProcessStartRequest, ICodexProcess>? processFactory = null,
        Func<string?>? executableLocator = null,
        Func<IReadOnlyDictionary<string, string?>>? environmentProvider = null,
        TimeSpan? callTimeout = null,
        TimeSpan? shutdownGrace = null,
        Func<string>? temporaryHomeFactory = null)
    {
        _processFactory = processFactory ?? (request => new CodexProcess(request));
        _executableLocator = executableLocator ?? CodexExecutableLocator.LocateOnThisMachine;
        _environmentProvider = environmentProvider ?? RealEnvironmentSnapshot;
        _temporaryHomeFactory = temporaryHomeFactory ?? (() => Directory.CreateTempSubdirectory(TemporaryHomePrefix).FullName);
        _callTimeout = RequirePositive(callTimeout ?? TimeSpan.FromSeconds(25), nameof(callTimeout));
        _shutdownGrace = RequirePositive(shutdownGrace ?? TimeSpan.FromSeconds(3), nameof(shutdownGrace));
    }

    private static TimeSpan RequirePositive(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Must be a positive duration.");
        }

        return value;
    }

    public async Task<CodexAppServerResult> RunAsync(
        OpenAiCredential credential, bool includeHistory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();

        // Defense-in-depth matching the Python baseline: the only caller in this codebase
        // (OpenAiLiveQuotaClient) never reaches here with a blank token/account ID, but this type does
        // not trust that invariant blindly at its own boundary.
        if (credential.AccountId.Length == 0 || credential.Value.Length == 0)
        {
            throw new CodexAppServerException(IncompleteLoginMessage, unauthorized: true);
        }

        var executablePath = _executableLocator();
        if (string.IsNullOrEmpty(executablePath))
        {
            throw new CodexAppServerException(ExecutableNotFoundMessage);
        }

        var temporaryHome = _temporaryHomeFactory();
        ICodexProcess? process = null;
        CodexJsonRpcClient? client = null;
        try
        {
            var environment = CodexEnvironmentSanitizer.Sanitize(_environmentProvider(), temporaryHome);
            process = _processFactory(new CodexProcessStartRequest(executablePath, temporaryHome, environment));
            client = new CodexJsonRpcClient(process.StandardInput, process.StandardOutput);

            await client.CallAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = ClientName, version = ClientVersion },
                    capabilities = new { experimentalApi = true },
                },
                _callTimeout,
                cancellationToken).ConfigureAwait(false);

            await client.SendNotificationAsync("initialized", cancellationToken).ConfigureAwait(false);

            await client.CallAsync(
                "account/login/start",
                new
                {
                    type = "chatgptAuthTokens",
                    accessToken = credential.Value,
                    chatgptAccountId = credential.AccountId,
                },
                _callTimeout,
                cancellationToken).ConfigureAwait(false);

            var rateLimits = await client
                .CallAsync("account/rateLimits/read", parameters: null, _callTimeout, cancellationToken)
                .ConfigureAwait(false);

            JsonElement? usage = null;
            string? historyError = null;
            if (includeHistory)
            {
                try
                {
                    usage = await client
                        .CallAsync("account/usage/read", parameters: null, _callTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CodexAppServerException ex)
                {
                    // A failure reading daily history is not a provider failure: the rate limits above
                    // already came from the server on this same session (PAR-004).
                    historyError = HistoryUnavailablePrefix + ex.Message;
                }
            }

            return new CodexAppServerResult(rateLimits, usage, historyError);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CodexAppServerException(CommunicationFailedMessage);
        }
        finally
        {
            // Every step below is independently guarded: a cleanup step that throws (Kill, the reader
            // wait, WaitForExitAsync, or Dispose) must never mask RunAsync's primary result/exception
            // from the try block above, and must never skip a later cleanup step - in particular,
            // deleting the temporary CODEX_HOME below always runs, and Dispose is always attempted
            // even if Kill or the wait threw first.
            if (process is not null)
            {
                // Killed before waiting on the reader: that is what actually makes the child's stdout
                // pipe close, which is what unblocks a reader loop that is otherwise waiting on more
                // output that is never coming (matching the Python baseline's cleanup order).
                TryRun(process.Kill);

                if (client is not null)
                {
                    // Best-effort: observes (and discards) the reader loop's own exception, if any,
                    // rather than letting it go unobserved. Bounded so a slow-to-close pipe can never
                    // delay cleanup.
                    await TryRunAsync(() => Task.WhenAny(client.ReaderCompletion, Task.Delay(TimeSpan.FromSeconds(1))))
                        .ConfigureAwait(false);
                }

                await TryRunAsync(() => process.WaitForExitAsync(_shutdownGrace)).ConfigureAwait(false);
                TryRun(process.Dispose);
            }

            TryDeleteDirectory(temporaryHome);
        }
    }

    /// <summary>Runs a synchronous best-effort cleanup step, swallowing any exception it throws. Used
    /// only in <see cref="RunAsync"/>'s <c>finally</c> block, where a cleanup failure must never mask
    /// the primary result/exception the <c>try</c> block already produced, nor stop the cleanup steps
    /// after it from running.</summary>
    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Intentionally unconditional: this is cleanup-only, best-effort work, and every
            // exception type it could plausibly throw (a fake in a test can throw anything) must
            // still leave the remaining cleanup steps - Dispose, then deleting the temporary
            // CODEX_HOME - to run.
        }
    }

    /// <summary>Async counterpart to <see cref="TryRun"/>.</summary>
    private static async Task TryRunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static IReadOnlyDictionary<string, string?> RealEnvironmentSnapshot()
    {
        var result = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value as string;
            }
        }

        return result;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a lingering handle would leave this under the OS temp root, not corrupt
            // anything the app owns. Not worth failing an otherwise-successful (or already-failed) call.
        }
    }
}
