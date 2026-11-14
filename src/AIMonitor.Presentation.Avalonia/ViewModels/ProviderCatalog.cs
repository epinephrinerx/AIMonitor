namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>What the connections page needs to know about one service to describe and configure it.</summary>
public sealed record ProviderDescription(
    string Id,
    string DisplayName,
    string Tagline,
    bool NeedsKey,
    string KeyLabel,
    string KeyPlaceholder,
    string ExtraLabel,
    string ExtraPlaceholder,
    string SetupHint);

/// <summary>The three services the app monitors, in the order the dashboard shows them.</summary>
public static class ProviderCatalog
{
    public static ProviderDescription Claude { get; } = new(
        Id: "claude",
        DisplayName: "Claude",
        Tagline: "Claude Code · full quota",
        NeedsKey: false,
        KeyLabel: "",
        KeyPlaceholder: "",
        ExtraLabel: "",
        ExtraPlaceholder: "",
        SetupHint: "Sign in to Claude Code on this machine (run `claude` in a terminal). On macOS that "
            + "login lives in the login Keychain rather than a file. This app reads it read-only and "
            + "never writes to it.");

    public static ProviderDescription OpenAi { get; } = new(
        Id: "openai",
        DisplayName: "OpenAI / Codex",
        Tagline: "Codex login, or an Admin API key",
        NeedsKey: true,
        KeyLabel: "Admin API key",
        KeyPlaceholder: "sk-admin-…",
        ExtraLabel: "Monthly budget (USD)",
        ExtraPlaceholder: "e.g. 120",
        SetupHint: "A Codex sign-in on this machine is used when there is one. Without it, an Admin API "
            + "key gives spend and budget instead of quota windows.");

    public static ProviderDescription Agy { get; } = new(
        Id: "agy",
        DisplayName: "Gemini / AGY",
        Tagline: "AGY command line",
        NeedsKey: false,
        KeyLabel: "",
        KeyPlaceholder: "",
        ExtraLabel: "",
        ExtraPlaceholder: "",
        SetupHint: "Install the AGY command-line tool and sign in with it. This app runs it read-only "
            + "to read the quota it reports.");

    public static IReadOnlyList<ProviderDescription> All { get; } = [Claude, OpenAi, Agy];

    public static ProviderDescription For(string providerId) =>
        All.FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "No such service.");
}
