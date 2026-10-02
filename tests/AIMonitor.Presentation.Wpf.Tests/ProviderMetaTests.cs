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
            "Sign in to Codex with ChatGPT on this machine to see Codex quota percentages, reset times and available daily token totals. Requires Codex CLI or the Codex desktop app. The existing auth.json is read-only; open Codex to renew an expired login.\n\n" +
            "A Codex ChatGPT login takes priority over saved or environment keys. Your existing keys are kept. When no Codex ChatGPT login is found, an optional organization Admin key provides API platform spend. Regular project keys cannot read API spend.";
        Assert.Equal(expectedHint, meta.SetupHint);
    }

    [Fact]
    public void Gemini_Metadata_MatchesSpecificationVerbatim()
    {
        var meta = ProviderMeta.Gemini;

        Assert.Equal("gemini", meta.Id);
        Assert.Equal("Gemini", meta.DisplayName);
        Assert.Equal("G", meta.BadgeLetter);
        Assert.Equal("Gemini CLI · Cloud Monitoring", meta.Tagline);
        Assert.True(meta.NeedsKey);
        Assert.Equal("Service account JSON", meta.KeyLabel);
        Assert.Equal(@"C:\path\to\service-account.json  (optional)", meta.KeyPlaceholder);
        Assert.Equal("Google Cloud project id", meta.ExtraLabel);
        Assert.Equal("auto-detected from your login if left blank", meta.ExtraPlaceholder);

        var expectedHint =
            "Detected automatically from your Gemini CLI login if you have signed in with gemini — that token already carries the cloud-platform scope this needs.\n\n" +
            "Otherwise point this at a Google Cloud service account JSON with the Monitoring Viewer role.\n\n" +
            "Either way, usage comes from Cloud Monitoring, because Google publishes no usage endpoint for Gemini. Gemini Advanced subscription limits are not available from any public API.";
        Assert.Equal(expectedHint, meta.SetupHint);
    }

    [Fact]
    public void All_ContainsClaudeOpenAiGeminiInOrder()
    {
        Assert.Equal(3, ProviderMeta.All.Count);
        Assert.Equal("claude", ProviderMeta.All[0].Id);
        Assert.Equal("openai", ProviderMeta.All[1].Id);
        Assert.Equal("gemini", ProviderMeta.All[2].Id);
    }

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("CLAUDE", "Claude")]
    [InlineData("openai", "OpenAI / Codex")]
    [InlineData("gemini", "Gemini")]
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
