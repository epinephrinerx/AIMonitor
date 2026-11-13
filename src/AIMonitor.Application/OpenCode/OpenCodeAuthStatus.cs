namespace AIMonitor.Application.OpenCode;

public sealed record OpenCodeSignIn(string Name, string Kind);

public sealed record OpenCodeAuthStatus(
    bool Installed,
    string Source,
    IReadOnlyList<OpenCodeSignIn> SignIns,
    bool MayBeOutdated,
    string Error)
{
    public override string ToString() => $"OpenCodeAuthStatus {{ SignIns = [{string.Join(", ", SignIns.Select(signIn => $"{signIn.Name} ({signIn.Kind})"))}] }}";
}

public interface IOpenCodeAuthReader
{
    Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken);
}
