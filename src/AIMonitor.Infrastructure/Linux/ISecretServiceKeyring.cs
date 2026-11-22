namespace AIMonitor.Infrastructure.Linux;

internal enum SecretServiceStatus { Found, NotFound, Done, Unavailable, Failed }

internal readonly record struct SecretServiceResult(
    SecretServiceStatus Status,
    string? Secret = null,
    string? Message = null)
{
    public override string ToString() => $"{Status}: {Message}";
}

internal interface ISecretServiceKeyring
{
    SecretServiceResult Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes);
    SecretServiceResult Store(string schemaName, IReadOnlyDictionary<string, string> attributes, string label, string secret);
    SecretServiceResult Clear(string schemaName, IReadOnlyDictionary<string, string> attributes);
}
