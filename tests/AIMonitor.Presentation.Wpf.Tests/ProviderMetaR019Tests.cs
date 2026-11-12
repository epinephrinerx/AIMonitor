using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (from Docs/SPEC_R019.md only): C1 — <see cref="ProviderMeta.Claude"/> gains the optional
/// Anthropic Admin key fields (verbatim strings from the C1 row) and C7 — <see cref="ProviderMeta.Agy"/>
/// gains the one-sentence explanation that Gemini API keys cannot read usage or quota.
/// All tests here fail on base 29f4048, where Claude still has <c>NeedsKey=false</c> and the old
/// tagline/labels and Agy's hint lacks the new sentence. The base ProviderMetaTests verbatim-asserts the
/// old values and is listed by E10 for the merge-side update; it is deliberately not edited here.
/// </summary>
public sealed class ProviderMetaR019Tests
{
    [Fact]
    public void Claude_NeedsKeyIsTrue_ForOptionalAdminKey()
    {
        // C1: NeedsKey=true.
        Assert.True(ProviderMeta.Claude.NeedsKey);
    }

    [Fact]
    public void Claude_KeyLabel_MatchesSpecVerbatim()
    {
        // C1 (verbatim):
        Assert.Equal("Admin API key (optional; API usage and cost)", ProviderMeta.Claude.KeyLabel);
    }

    [Fact]
    public void Claude_KeyPlaceholder_MatchesSpecVerbatim()
    {
        // C1 (verbatim, incl. the U+2026 ellipsis character):
        Assert.Equal("sk-ant-admin…", ProviderMeta.Claude.KeyPlaceholder);
    }

    [Fact]
    public void Claude_HasNoExtraField_LabelsStayEmpty()
    {
        // C1: "no extra field" — unlike OpenAI, Claude gets no secondary configuration.
        Assert.Equal("", ProviderMeta.Claude.ExtraLabel);
        Assert.Equal("", ProviderMeta.Claude.ExtraPlaceholder);
    }

    [Fact]
    public void Claude_Tagline_MatchesSpecVerbatim()
    {
        // C1 (verbatim):
        Assert.Equal("Claude Code · full quota · optional API usage", ProviderMeta.Claude.Tagline);
    }

    [Fact]
    public void Claude_SetupHint_KeepsClaudeCodeSignInAsQuotaSource()
    {
        // C1: "SetupHint adds:" — the existing sign-in guidance stays, so the original full sentence
        // must still appear (base text: "Sign in to Claude Code on this machine (run `claude` in a
        // terminal). This app reads that existing login read-only and never writes to it.").
        Assert.Contains(
            "Sign in to Claude Code on this machine (run `claude` in a terminal).",
            ProviderMeta.Claude.SetupHint,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_SetupHint_ExplainsAdminKeyIsOptionalOrganizationAdminCreatedInConsole()
    {
        // C1 added facts, each pinned by a distinctive fragment of the spec wording:
        var hint = ProviderMeta.Claude.SetupHint;
        Assert.Contains("optional", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("organization", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("admin", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Admin keys", hint, StringComparison.Ordinal); // created in Claude Console → Settings → Admin keys
        Assert.Contains("Settings", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_SetupHint_StatesOrdinaryOrWorkspaceKeysCannotReadUsage()
    {
        // C1: "ordinary/workspace API keys cannot read usage".
        var hint = ProviderMeta.Claude.SetupHint;
        Assert.Contains("ordinary", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agy_SetupHint_AddsGeminiApiKeysCannotReadUsageOrQuotaSentence()
    {
        // C7: one added sentence — "Gemini API keys cannot read usage or quota (no usage API), so AGY
        // uses your Antigravity sign-in." Pinned with the spec's own phrases.
        var hint = ProviderMeta.Agy.SetupHint;
        Assert.Contains("Gemini API keys", hint, StringComparison.Ordinal);
        Assert.Contains("cannot read usage or quota", hint, StringComparison.Ordinal);
        Assert.Contains("no usage API", hint, StringComparison.Ordinal);
        Assert.Contains("Antigravity", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAi_MetadataUnchangedByR019()
    {
        // R-019 must not disturb the OpenAI card (C8 says the saved-key change path already exists).
        var openAi = ProviderMeta.OpenAi;
        Assert.True(openAi.NeedsKey);
        Assert.Equal("Admin API key (optional; API spend)", openAi.KeyLabel);
        Assert.Equal("sk-admin-…", openAi.KeyPlaceholder);
        Assert.Equal("Monthly budget (USD, optional)", openAi.ExtraLabel);
    }
}
