using AIMonitor.Infrastructure.Providers.OpenAI;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

public class CodexOAuthCredentialSecretRedactionTests
{
    private const string SecretToken = "SENTINEL_ACCESS_TOKEN_UNSAFE_7f2a";

    private static CodexOAuthCredential Credential() =>
        new(SecretToken, DateTimeOffset.UtcNow.AddHours(1), account: "user@example.com", accountId: "acct-123");

    [Fact]
    public void ToString_NeverContainsTheAccessToken()
    {
        var text = Credential().ToString();

        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResultToString_NeverContainsTheAccessToken()
    {
        var result = CodexOAuthReadResult.Found(Credential(), "C:\\fake\\auth.json");

        Assert.DoesNotContain(SecretToken, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentException_NeverContainsTheAccessTokenValue()
    {
        // A blank token is rejected by the constructor guard; the resulting message must describe
        // the problem without ever echoing back whatever blank/garbage value was supplied.
        var ex = Assert.Throws<ArgumentException>(() => new CodexOAuthCredential(
            "   ", DateTimeOffset.UtcNow, account: "", accountId: ""));

        Assert.DoesNotContain(SecretToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NeverContainsTheTokenEvenWhenDuplicatedIntoMetadata()
    {
        // A corrupted or tampered auth file could stuff the token into account/account ID claims
        // too; ToString must stay fixed and safe regardless.
        var credential = new CodexOAuthCredential(
            SecretToken, DateTimeOffset.UtcNow.AddHours(1), account: SecretToken, accountId: SecretToken);

        var text = credential.ToString();

        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityFailureDiagnostics_NeverContainTheAccessToken()
    {
        var a = Credential();
        var b = new CodexOAuthCredential("SENTINEL_OTHER_TOKEN_UNSAFE_9c1e", a.ExpiresAtUtc, a.Account, a.AccountId);

        var failure = Record.Exception(() => Assert.Equal(a, b));

        Assert.NotNull(failure);
        Assert.DoesNotContain(SecretToken, failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL_OTHER_TOKEN_UNSAFE_9c1e", failure.Message, StringComparison.Ordinal);
    }
}
