using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

public class ClaudeCredentialsSecretRedactionTests
{
    private const string SecretToken = "SENTINEL_ACCESS_TOKEN_UNSAFE_7f2a";

    private static ClaudeCredentials Credentials() =>
        new(SecretToken, DateTimeOffset.UtcNow.AddHours(1), "pro", "default_claude_pro");

    [Fact]
    public void ToString_NeverContainsTheAccessToken()
    {
        var text = Credentials().ToString();

        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResultToString_NeverContainsTheAccessToken()
    {
        var result = ClaudeCredentialReadResult.Found(Credentials(), "C:\\fake\\.credentials.json");

        Assert.DoesNotContain(SecretToken, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentException_NeverContainsTheAccessTokenValue()
    {
        // A blank token is rejected by the constructor guard; the resulting message must describe
        // the problem without ever echoing back whatever blank/garbage value was supplied.
        var ex = Assert.Throws<ArgumentException>(() => new ClaudeCredentials(
            "   ", DateTimeOffset.UtcNow, subscriptionType: null, rateLimitTier: null));

        Assert.DoesNotContain(SecretToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NeverContainsTheTokenEvenWhenDuplicatedIntoMetadata()
    {
        // A corrupted or tampered credentials file could stuff the token into subscriptionType or
        // rateLimitTier too; ToString must stay fixed and safe regardless.
        var credentials = new ClaudeCredentials(
            SecretToken, DateTimeOffset.UtcNow.AddHours(1), subscriptionType: SecretToken, rateLimitTier: SecretToken);

        var text = credentials.ToString();

        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityFailureDiagnostics_NeverContainTheAccessToken()
    {
        var a = Credentials();
        var b = new ClaudeCredentials("SENTINEL_OTHER_TOKEN_UNSAFE_9c1e", a.ExpiresAtUtc, a.SubscriptionType, a.RateLimitTier);

        var failure = Record.Exception(() => Assert.Equal(a, b));

        Assert.NotNull(failure);
        Assert.DoesNotContain(SecretToken, failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL_OTHER_TOKEN_UNSAFE_9c1e", failure.Message, StringComparison.Ordinal);
    }
}
