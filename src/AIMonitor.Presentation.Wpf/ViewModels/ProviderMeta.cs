namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// Immutable metadata defining an AI provider's display attributes, configuration fields, and setup guidance.
/// Conforms to the Python 1.3.3 baseline specifications.
/// </summary>
/// <param name="Id">Unique identifier matching provider registration ("claude", "openai", "gemini").</param>
/// <param name="DisplayName">User-facing name matching tab headers ("Claude", "OpenAI / Codex", "Gemini").</param>
/// <param name="BadgeLetter">Single uppercase initial for the circular service badge.</param>
/// <param name="Tagline">Summary subtitle describing provider connection capabilities.</param>
/// <param name="NeedsKey">True if the provider supports or requires user-supplied credentials.</param>
/// <param name="KeyLabel">Label for the primary credential/key input field.</param>
/// <param name="KeyPlaceholder">Default placeholder text for the key input field.</param>
/// <param name="ExtraLabel">Label for provider-specific secondary configuration, or empty if unsupported.</param>
/// <param name="ExtraPlaceholder">Placeholder text for the secondary configuration field.</param>
/// <param name="SetupHint">Plain-text guidance explaining login mechanisms, priorities, and limitations.</param>
public sealed record ProviderMeta(
    string Id,
    string DisplayName,
    string BadgeLetter,
    string Tagline,
    bool NeedsKey,
    string KeyLabel,
    string KeyPlaceholder,
    string ExtraLabel,
    string ExtraPlaceholder,
    string SetupHint)
{
    public static ProviderMeta Claude { get; } = new(
        Id: "claude",
        DisplayName: "Claude",
        BadgeLetter: "C",
        Tagline: "Claude Code · full quota",
        NeedsKey: false,
        KeyLabel: "",
        KeyPlaceholder: "",
        ExtraLabel: "",
        ExtraPlaceholder: "",
        SetupHint: "Sign in to Claude Code on this machine (run `claude` in a terminal). This app reads that existing login read-only and never writes to it.");

    public static ProviderMeta OpenAi { get; } = new(
        Id: "openai",
        DisplayName: "OpenAI / Codex",
        BadgeLetter: "O",
        Tagline: "Codex quota · optional API platform spend",
        NeedsKey: true,
        KeyLabel: "Admin API key (optional; API spend)",
        KeyPlaceholder: "sk-admin-…",
        ExtraLabel: "Monthly budget (USD, optional)",
        ExtraPlaceholder: "e.g. 50 — a local target, not an OpenAI limit",
        SetupHint: "Sign in to Codex with ChatGPT on this machine to see Codex quota percentages, reset times and available daily token totals. Requires Codex CLI or the Codex desktop app. The existing auth.json is read-only; open Codex to renew an expired login.\n\nA Codex ChatGPT login takes priority over saved or environment keys. Your existing keys are kept. When no Codex ChatGPT login is found, an optional organization Admin key provides API platform spend. Regular project keys cannot read API spend.");

    public static ProviderMeta Gemini { get; } = new(
        Id: "gemini",
        DisplayName: "Gemini",
        BadgeLetter: "G",
        Tagline: "Gemini CLI · Cloud Monitoring",
        NeedsKey: true,
        KeyLabel: "Service account JSON",
        KeyPlaceholder: @"C:\path\to\service-account.json  (optional)",
        ExtraLabel: "Google Cloud project id",
        ExtraPlaceholder: "auto-detected from your login if left blank",
        SetupHint: "Detected automatically from your Gemini CLI login if you have signed in with gemini — that token already carries the cloud-platform scope this needs.\n\nOtherwise point this at a Google Cloud service account JSON with the Monitoring Viewer role.\n\nEither way, usage comes from Cloud Monitoring, because Google publishes no usage endpoint for Gemini. Gemini Advanced subscription limits are not available from any public API.");

    public static IReadOnlyList<ProviderMeta> All { get; } = [Claude, OpenAi, Gemini];

    public static ProviderMeta? TryGet(string providerId) =>
        All.FirstOrDefault(m => string.Equals(m.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
