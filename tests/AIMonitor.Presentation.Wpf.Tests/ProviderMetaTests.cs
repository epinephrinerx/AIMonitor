using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ProviderMetaTests
{
    [Fact]
    public void Claude_Metadata_MatchesSpecificationVerbatim()
    {
        var meta = ProviderMeta.Claude;

        Assert.Equal("claude", meta.Id);
        Assert.Equal("Claude", meta.DisplayName);
        Assert.Equal("C", meta.BadgeLetter);
        Assert.Equal("Claude Code · full quota", meta.Tagline);
        Assert.False(meta.NeedsKey);
        Assert.Equal("", meta.KeyLabel);
        Assert.Equal("", meta.KeyPlaceholder);
        Assert.Equal("", meta.ExtraLabel);
        Assert.Equal("", meta.ExtraPlaceholder);
        Assert.Equal(
            "Sign in to Claude Code on this machine (run `claude` in a terminal). This app reads that existing login read-only and never writes to it.",
            meta.SetupHint);
    }

    [Fact]
    public void OpenAi_Metadata_MatchesSpecificationVerbatim()
    {
        var meta = ProviderMeta.OpenAi;

        Assert.Equal("openai", meta.Id);
        Assert.Equal("OpenAI / Codex", meta.DisplayName);
        Assert.Equal("O", meta.BadgeLetter);
        Assert.Equal("Codex quota · optional API platform spend", meta.Tagline);
        Assert.True(meta.NeedsKey);
        Assert.Equal("Admin API key (optional; API spend)", meta.KeyLabel);
        Assert.Equal("sk-admin-…", meta.KeyPlaceholder);
        Assert.Equal("Monthly budget (USD, optional)", meta.ExtraLabel);
        Assert.Equal("e.g. 50 — a local target, not an OpenAI limit", meta.ExtraPlaceholder);

        var expectedHint =
            "To connect Codex with ChatGPT, sign in via terminal: run `codex login` for browser ChatGPT OAuth, or `codex login --device-auth` for device-code flow (enable it in ChatGPT security/workspace settings if required). Run `codex login status` to check mode, then return to AIMonitor and click Refresh.\n\n" +
            "This app reads existing file-backed login (~/.codex/auth.json) read-only and does not initiate OAuth or refresh external tokens; credential-store-only logins may not be detected. Open Codex to renew an expired login.\n\n" +
            "Codex OAuth always has priority over API keys (including expired logins), and saved keys are retained. For API platform spend instead, provide an optional organization Admin key (sk-admin-…) via Connections or OPENAI_ADMIN_KEY + restart; ordinary project keys cannot read organization spend.";
        Assert.Equal(expectedHint, meta.SetupHint);
    }

    [Fact]
    public void Agy_Metadata_IsKeylessAndUsesCliOnly()
    {
        var meta = ProviderMeta.Agy;
        Assert.Equal("agy", meta.Id);
        Assert.Equal("Gemini / AGY", meta.DisplayName);
        Assert.False(meta.NeedsKey);
        Assert.Empty(meta.KeyLabel);
        Assert.Empty(meta.KeyPlaceholder);
        Assert.Empty(meta.ExtraLabel);
        Assert.Empty(meta.ExtraPlaceholder);
        Assert.Contains("/usage", meta.SetupHint);
        Assert.Contains("PATH", meta.SetupHint);
        Assert.Null(ProviderMeta.TryGet("gemini"));
    }

    [Fact]
    public void All_ContainsClaudeOpenAiGeminiInOrder()
    {
        Assert.Equal(3, ProviderMeta.All.Count);
        Assert.Equal("claude", ProviderMeta.All[0].Id);
        Assert.Equal("openai", ProviderMeta.All[1].Id);
        Assert.Equal("agy", ProviderMeta.All[2].Id);
    }

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("CLAUDE", "Claude")]
    [InlineData("openai", "OpenAI / Codex")]
    [InlineData("agy", "Gemini / AGY")]
    public void TryGet_CaseInsensitive_ReturnsCorrectMetadata(string providerId, string expectedDisplayName)
    {
        var meta = ProviderMeta.TryGet(providerId);
        Assert.NotNull(meta);
        Assert.Equal(expectedDisplayName, meta.DisplayName);
    }

    [Fact]
    public void TryGet_UnknownProvider_ReturnsNull()
    {
        Assert.Null(ProviderMeta.TryGet("unknown"));
        Assert.Null(ProviderMeta.TryGet(""));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Mask_WhenNullOrEmpty_ReturnsEmpty(string? secret, string expected)
    {
        Assert.Equal(expected, ConnectDialogViewModel.Mask(secret));
    }

    [Theory]
    [InlineData("a", "•")]
    [InlineData("12345", "•••••")]
    [InlineData("123456789012", "••••••••••••")]
    public void Mask_WhenLengthUpTo12_ReturnsBulletsOfMatchingLength(string secret, string expected)
    {
        var masked = ConnectDialogViewModel.Mask(secret);
        Assert.Equal(expected, masked);
        Assert.Equal(secret.Length, masked.Length);
    }

    [Fact]
    public void Mask_WhenLengthGreaterThan12_ReturnsFirst8EllipsisLast4()
    {
        var secret = "sk-admin-123456789abcdef";
        var masked = ConnectDialogViewModel.Mask(secret);

        Assert.Equal("sk-admin…cdef", masked);
        Assert.StartsWith("sk-admin", masked);
        Assert.EndsWith("cdef", masked);
        Assert.Contains("…", masked);
        Assert.DoesNotContain("123456789ab", masked);
    }
}
