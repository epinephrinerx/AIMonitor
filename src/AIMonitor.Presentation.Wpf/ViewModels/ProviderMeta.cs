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
        SetupHint: "To connect Codex with ChatGPT, sign in via terminal: run `codex login` for browser ChatGPT OAuth, or `codex login --device-auth` for device-code flow (enable it in ChatGPT security/workspace settings if required). Run `codex login status` to check mode, then return to AIMonitor and click Refresh.\n\nThis app reads existing file-backed login (~/.codex/auth.json) read-only and does not initiate OAuth or refresh external tokens; credential-store-only logins may not be detected. Open Codex to renew an expired login.\n\nCodex OAuth always has priority over API keys (including expired logins), and saved keys are retained. For API platform spend instead, provide an optional organization Admin key (sk-admin-…) via Connections or OPENAI_ADMIN_KEY + restart; ordinary project keys cannot read organization spend.");

    public static ProviderMeta Agy { get; } = new(
        Id: "agy",
        DisplayName: "AGY",
        BadgeLetter: "A",
        Tagline: "Antigravity CLI · shared quota pools",
        NeedsKey: false,
        KeyLabel: "",
        KeyPlaceholder: "",
        ExtraLabel: "",
        ExtraPlaceholder: "",
        SetupHint: "Install AGY and sign in through Antigravity, then click Refresh. Ensure agy.exe is available on PATH; restart AIMonitor after changing PATH. This app reads AGY's /usage JSON output, without starting a model conversation or changing your login. AGY pools are separate from standalone Claude/Codex quotas. No API key or Google Cloud project is needed.");

    public static ProviderMeta Gemini { get; } = new(
        Id: "gemini",
        DisplayName: "Gemini",
        BadgeLetter: "G",
        Tagline: "Gemini API · Cloud Monitoring metrics",
        NeedsKey: true,
        KeyLabel: "Service account key JSON (optional)",
        KeyPlaceholder: "path/to/service-account.json",
        ExtraLabel: "Google Cloud Project ID (optional)",
        ExtraPlaceholder: "my-gcp-project-id",
        SetupHint: "Legacy Gemini provider.");

    public static IReadOnlyList<ProviderMeta> All { get; } = [Claude, OpenAi, Agy];

    public static ProviderMeta? TryGet(string providerId) =>
        All.FirstOrDefault(m => string.Equals(m.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
