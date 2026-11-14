using AIMonitor.Infrastructure.Providers.OpenAI;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

public class OpenAiApiKeyCandidateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SavedAdmin_BlankKey_ReturnsNull(string? blankKey)
    {
        Assert.Null(OpenAiApiKeyCandidate.SavedAdmin(blankKey));
    }

    [Fact]
    public void SavedAdmin_AdminShapedKey_IsUsageCapableWithFixedIdentity()
    {
        var candidate = OpenAiApiKeyCandidate.SavedAdmin("sk-admin-abc123");

        Assert.NotNull(candidate);
        Assert.Equal("manual", candidate!.SourceId);
        Assert.Equal("Admin key saved in this app", candidate.SourceLabel);
        Assert.Equal("saved in this app", candidate.Account);
        Assert.True(candidate.IsUsageCapable);
        Assert.Equal(string.Empty, candidate.LimitedReason);
    }

    [Fact]
    public void SavedAdmin_OrdinaryProjectKey_IsNotUsageCapableWithFixedReason()
    {
        var candidate = OpenAiApiKeyCandidate.SavedAdmin("sk-proj-abc123");

        Assert.NotNull(candidate);
        Assert.False(candidate!.IsUsageCapable);
        Assert.Equal(OpenAiApiKeyCandidate.NotAdminKeyReason, candidate.LimitedReason);
    }

    [Fact]
    public void SavedAdmin_TrimsSurroundingWhitespace()
    {
        var candidate = OpenAiApiKeyCandidate.SavedAdmin("  sk-admin-abc123  ");

        Assert.Equal("sk-admin-abc123", candidate!.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Environment_BlankKey_ReturnsNull(string? blankKey)
    {
        Assert.Null(OpenAiApiKeyCandidate.Environment(blankKey, "OPENAI_ADMIN_KEY"));
    }

    [Fact]
    public void Environment_AdminShapedKey_UsesVariableNameInAccountText()
    {
        var candidate = OpenAiApiKeyCandidate.Environment("sk-admin-xyz", "OPENAI_ADMIN_KEY");

        Assert.NotNull(candidate);
        Assert.Equal("env", candidate!.SourceId);
        Assert.Equal("Environment variable", candidate.SourceLabel);
        Assert.Equal("from $OPENAI_ADMIN_KEY", candidate.Account);
        Assert.True(candidate.IsUsageCapable);
    }

    [Fact]
    public void Environment_OrdinaryKey_IsLimitedWithFixedReason()
    {
        var candidate = OpenAiApiKeyCandidate.Environment("sk-abc", "OPENAI_API_KEY");

        Assert.NotNull(candidate);
        Assert.False(candidate!.IsUsageCapable);
        Assert.Equal(OpenAiApiKeyCandidate.NotAdminKeyReason, candidate.LimitedReason);
    }

    [Fact]
    public void Environment_BlankVariableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => OpenAiApiKeyCandidate.Environment("sk-admin-x", " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CliKey_BlankKey_ReturnsNull(string? blankKey)
    {
        Assert.Null(OpenAiApiKeyCandidate.CliKey(blankKey));
    }

    [Fact]
    public void CliKey_AdminShapedKey_IsUsageCapableWithFixedIdentity()
    {
        var candidate = OpenAiApiKeyCandidate.CliKey("sk-admin-cli");

        Assert.NotNull(candidate);
        Assert.Equal("codex_cli", candidate!.SourceId);
        Assert.Equal("Codex CLI login (ChatGPT)", candidate.SourceLabel);
        Assert.True(candidate.IsUsageCapable);
    }

    [Theory]
    [InlineData("sk-admin-x\ny")]
    [InlineData("sk-admin-x\0y")]
    public void TryCreate_KeyContainingControlCharacter_ReturnsNullRatherThanAMalformedCandidate(string unsafeKey)
    {
        Assert.Null(OpenAiApiKeyCandidate.SavedAdmin(unsafeKey));
        Assert.Null(OpenAiApiKeyCandidate.Environment(unsafeKey, "OPENAI_ADMIN_KEY"));
        Assert.Null(OpenAiApiKeyCandidate.CliKey(unsafeKey));
    }

    [Fact]
    public void ToString_NeverContainsTheKeyValue()
    {
        const string secretKey = "sk-admin-SENTINEL_UNSAFE_7f2a";
        var candidate = OpenAiApiKeyCandidate.SavedAdmin(secretKey);

        var text = candidate!.ToString();

        Assert.DoesNotContain(secretKey, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityFailureDiagnostics_NeverContainTheKeyValue()
    {
        var a = OpenAiApiKeyCandidate.SavedAdmin("sk-admin-SENTINEL_A_UNSAFE");
        var b = OpenAiApiKeyCandidate.SavedAdmin("sk-admin-SENTINEL_B_UNSAFE");

        var failure = Record.Exception(() => Assert.Equal(a, b));

        Assert.NotNull(failure);
        Assert.DoesNotContain("SENTINEL_A_UNSAFE", failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL_B_UNSAFE", failure.Message, StringComparison.Ordinal);
    }
}
