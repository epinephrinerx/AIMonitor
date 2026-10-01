using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// Secret-bearing Gemini types must never leak their secret through <see cref="object.ToString"/>,
/// an exception message, or an equality-assertion failure message - whether the secret is a Gemini
/// CLI OAuth bearer token or a service-account private key.
/// </summary>
public sealed class GeminiCredentialSecretRedactionTests
{
    private const string SecretToken = "SENTINEL_ACCESS_TOKEN_UNSAFE_7f2a";
    private const string SecretPath = @"C:\Users\someone\SENTINEL_KEY_PATH_UNSAFE\key.json";
    private const string SecretPrivateKey = "SENTINEL_PRIVATE_KEY_UNSAFE_4d8b";

    [Fact]
    public void GeminiCredential_ToString_NeverContainsAnOAuthAccessToken()
    {
        var credential = new GeminiCredential(GeminiCredentialKind.OAuth, SecretToken, account: "user@example.com");

        Assert.DoesNotContain(SecretToken, credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GeminiCredential_ToString_NeverContainsAServiceAccountPath()
    {
        // Value holds a filesystem path (not a cryptographic secret) for a usable service-account
        // credential, but it is still redacted: nothing about a credential's location should be
        // echoed unnecessarily.
        var credential = new GeminiCredential(GeminiCredentialKind.ServiceAccount, SecretPath, account: "robot@example.com");

        Assert.DoesNotContain(SecretPath, credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GeminiCredential_ToString_IsFixedRegardlessOfWhichFieldsAreSet()
    {
        var connected = new GeminiCredential(GeminiCredentialKind.ServiceAccount, "path", account: "a", project: "p", usageCapable: true);
        var limited = new GeminiCredential(GeminiCredentialKind.ServiceAccount, string.Empty, usageCapable: false, limitedReason: "why");

        Assert.Equal(connected.ToString(), limited.ToString());
    }

    [Fact]
    public void GeminiCredential_EqualityFailureDiagnostics_NeverContainTheAccessToken()
    {
        var a = new GeminiCredential(GeminiCredentialKind.OAuth, SecretToken);
        var b = new GeminiCredential(GeminiCredentialKind.OAuth, "SENTINEL_OTHER_TOKEN_UNSAFE_9c1e");

        var failure = Record.Exception(() => Assert.Equal(a, b));

        Assert.NotNull(failure);
        Assert.DoesNotContain(SecretToken, failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL_OTHER_TOKEN_UNSAFE_9c1e", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeminiServiceAccountKey_ToString_NeverContainsThePrivateKey()
    {
        var key = new GeminiServiceAccountKey("service_account", "robot@example.com", SecretPrivateKey, "my-project");

        Assert.DoesNotContain(SecretPrivateKey, key.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GeminiServiceAccountKey_EqualityFailureDiagnostics_NeverContainThePrivateKey()
    {
        var a = new GeminiServiceAccountKey("service_account", "robot@example.com", SecretPrivateKey, "my-project");
        var b = new GeminiServiceAccountKey("service_account", "robot@example.com", "SENTINEL_OTHER_KEY_UNSAFE", "my-project");

        var failure = Record.Exception(() => Assert.Equal(a, b));

        Assert.NotNull(failure);
        Assert.DoesNotContain(SecretPrivateKey, failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL_OTHER_KEY_UNSAFE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceAccountCredentialReader_LimitedReason_NeverContainsRawFileContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aimonitor-gemini-redaction-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "key.json");
            var payload = $$"""{"type":"service_account","client_email":"robot@example.com","private_key":"{{SecretPrivateKey}}"}""";
            // Deliberately truncate the JSON so parsing fails - a malformed file's raw bytes must
            // never surface in the actionable reason shown to the user.
            await File.WriteAllTextAsync(path, payload[..(payload.Length - 5)]);

            var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

            Assert.NotNull(credential);
            Assert.DoesNotContain(SecretPrivateKey, credential!.LimitedReason, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretPrivateKey, credential.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
