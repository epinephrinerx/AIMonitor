namespace AIMonitor.Application.Providers;

/// <summary>
/// Connection details for a provider: an optional credential/key and provider-specific extra configuration.
/// </summary>
/// <param name="Key">The stored secret key, or <see langword="null"/> if none is configured.</param>
/// <param name="Extra">Provider-specific extra configuration string (e.g. monthly budget for OpenAI, project override for Gemini).</param>
public sealed record ProviderConnection(string? Key, string Extra = "")
{
    /// <summary>
    /// Returns a safe textual representation that never includes the raw secret key. The key, when
    /// present, is rendered as a fixed redaction marker so log output, exception messages and
    /// debugger tooltips cannot leak credentials (R-019 E8).
    /// </summary>
    public override string ToString()
    {
        var maskedKey = string.IsNullOrEmpty(Key) ? "(none)" : "(redacted)";
        return $"ProviderConnection(Key={maskedKey}, Extra=\"{Extra}\")";
    }
}
