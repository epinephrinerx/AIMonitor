using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// PAR-009: the isolated Codex App Server child process must never see another provider's credentials
/// or the real <c>CODEX_HOME</c>. A pure function of its inputs - never touches the real environment.
/// </summary>
public sealed class CodexEnvironmentSanitizerTests
{
    [Fact]
    public void Sanitize_StripsOpenAiCodexAndAnthropicPrefixedVariables()
    {
        var source = new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "sk-secret",
            ["OPENAI_ADMIN_KEY"] = "sk-admin-secret",
            ["CODEX_HOME"] = @"C:\Users\real\.codex",
            ["ANTHROPIC_API_KEY"] = "claude-secret",
            ["PATH"] = @"C:\Windows",
            ["USERNAME"] = "someone",
        };

        var result = CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.False(result.ContainsKey("OPENAI_API_KEY"));
        Assert.False(result.ContainsKey("OPENAI_ADMIN_KEY"));
        Assert.False(result.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal(@"C:\Windows", result["PATH"]);
        Assert.Equal("someone", result["USERNAME"]);
    }

    [Fact]
    public void Sanitize_StripsClaudeGeminiAndGooglePrefixedVariables()
    {
        // PAR-009's isolation is not OpenAI-specific: every other provider this app knows about must
        // be scrubbed too, since the child process must never see a credential it has no business
        // touching, regardless of which provider it belongs to.
        var source = new Dictionary<string, string?>
        {
            ["CLAUDE_API_KEY"] = "claude-secret",
            ["CLAUDE_CONFIG_DIR"] = @"C:\Users\real\.claude",
            ["GEMINI_API_KEY"] = "gemini-secret",
            ["GOOGLE_API_KEY"] = "google-secret",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = @"C:\Users\real\adc.json",
            ["PATH"] = @"C:\Windows",
        };

        var result = CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.False(result.ContainsKey("CLAUDE_API_KEY"));
        Assert.False(result.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.False(result.ContainsKey("GEMINI_API_KEY"));
        Assert.False(result.ContainsKey("GOOGLE_API_KEY"));
        Assert.False(result.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS"));
        Assert.Equal(@"C:\Windows", result["PATH"]);
    }

    [Fact]
    public void Sanitize_SetsCodexHomeToTheSuppliedTemporaryDirectory_OverridingTheRealOne()
    {
        var source = new Dictionary<string, string?> { ["CODEX_HOME"] = @"C:\Users\real\.codex" };

        var result = CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.Equal(@"C:\temp\aimonitor-codex-abc", result["CODEX_HOME"]);
    }

    [Fact]
    public void Sanitize_PrefixMatchIsCaseInsensitive()
    {
        var source = new Dictionary<string, string?> { ["openai_api_key"] = "sk-secret", ["codex_home"] = "x" };

        var result = CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.DoesNotContain(result.Keys, key => key.Equals("openai_api_key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sanitize_DoesNotStripUnrelatedVariablesThatMerelyContainTheSubstring()
    {
        var source = new Dictionary<string, string?> { ["MY_OPENAI_WRAPPER_PATH"] = "kept" };

        var result = CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.Equal("kept", result["MY_OPENAI_WRAPPER_PATH"]);
    }

    [Fact]
    public void Sanitize_NeverMutatesTheSourceDictionary()
    {
        var source = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "sk-secret", ["PATH"] = "x" };

        CodexEnvironmentSanitizer.Sanitize(source, @"C:\temp\aimonitor-codex-abc");

        Assert.True(source.ContainsKey("OPENAI_API_KEY"));
    }
}
