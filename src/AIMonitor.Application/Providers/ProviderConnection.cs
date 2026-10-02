namespace AIMonitor.Application.Providers;

/// <summary>
/// Connection details for a provider: an optional credential/key and provider-specific extra configuration.
/// </summary>
/// <param name="Key">The stored secret key, or <see langword="null"/> if none is configured.</param>
/// <param name="Extra">Provider-specific extra configuration string (e.g. monthly budget for OpenAI, project override for Gemini).</param>
public sealed record ProviderConnection(string? Key, string Extra = "");
