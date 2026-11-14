namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>What it takes to launch the Codex App Server child process: the resolved executable path,
/// the disposable temporary directory to run it in (also its <c>CODEX_HOME</c>), and its fully
/// resolved environment. Carries no credential - the access token is never a process argument or
/// environment variable, only ever written to the child's stdin by the protocol layer.</summary>
internal sealed record CodexProcessStartRequest(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);
