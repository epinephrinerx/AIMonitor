namespace AIMonitor.Application.OpenCode;

public sealed record OpenCodeSignIn(string Name, string Kind);

public sealed record OpenCodeAuthStatus(
    bool Installed,
    string Source,
    IReadOnlyList<OpenCodeSignIn> SignIns,
    bool MayBeOutdated,
    string Error);

public interface IOpenCodeAuthReader
{
    Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken);
}
