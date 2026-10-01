namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Runs one Codex App Server session over an isolated temporary home and returns its raw quota/usage
/// results. Callers must only pass a <see cref="OpenAiCredential"/> that is already known to be
/// OAuth-kind, connected, and usage-capable (non-blank access token and account ID) - detection and
/// expiry are the caller's responsibility (<see cref="OpenAiCredentialResolver"/>), not this type's.
/// </summary>
public interface ICodexAppServerLauncher
{
    /// <summary>
    /// Launches the App Server, performs the fixed <c>initialize</c> -&gt;
    /// <c>initialized</c> -&gt; <c>account/login/start</c> -&gt; <c>account/rateLimits/read</c>
    /// -&gt; (optionally) <c>account/usage/read</c> sequence, and always cleans up the process and its
    /// temporary home before returning or throwing - including when this call is cancelled.
    /// </summary>
    /// <exception cref="CodexAppServerException">The executable could not be found, the process could
    /// not be started or communicated with, or a required call (everything up to and including
    /// <c>account/rateLimits/read</c>) failed. A failure of only the optional
    /// <c>account/usage/read</c> call is reported through <see cref="CodexAppServerResult.HistoryError"/>
    /// instead.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled.</exception>
    Task<CodexAppServerResult> RunAsync(OpenAiCredential credential, bool includeHistory, CancellationToken cancellationToken);
}
