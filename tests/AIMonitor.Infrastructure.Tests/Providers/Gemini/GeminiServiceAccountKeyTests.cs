using System.Text.Json;
using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// PAR-012: a service-account JSON must be an object and contain every baseline-required field
/// (<c>type</c> == <c>"service_account"</c>, non-blank <c>client_email</c>, non-blank
/// <c>private_key</c>) with the correct primitive type. Mirrors the Python baseline's
/// <c>usable_service_account()</c> and its <c>JsonThatIsNotAnObjectTests</c>/
/// <c>DetectorAndProviderAgreeTests</c> coverage.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiServiceAccountKeyTests
{
    private const string CompleteKey =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project","private_key":"-----BEGIN PRIVATE KEY----- not-a-real-key"}""";

    private static GeminiServiceAccountKey Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GeminiServiceAccountKey.FromJsonObject(document.RootElement);
    }

    [Fact]
    public void FromJsonObject_CompleteKey_IsUsable()
    {
        var key = Parse(CompleteKey);

        Assert.True(key.IsUsableServiceAccount);
        Assert.Equal("robot@example.iam.gserviceaccount.com", key.ClientEmail);
        Assert.Equal("my-project", key.ProjectId);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("client_email")]
    [InlineData("private_key")]
    public void FromJsonObject_MissingRequiredField_IsNotUsable(string fieldToRemove)
    {
        using var document = JsonDocument.Parse(CompleteKey);
        var payload = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != fieldToRemove)
            {
                payload[property.Name] = property.Value.GetString();
            }
        }

        var key = Parse(JsonSerializer.Serialize(payload));

        Assert.False(key.IsUsableServiceAccount, $"a key with no {fieldToRemove} must not be usable");
    }

    [Fact]
    public void FromJsonObject_EmptyStringField_CountsAsMissing()
    {
        var key = Parse(
            """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","private_key":""}""");

        Assert.False(key.IsUsableServiceAccount);
    }

    [Theory]
    [InlineData("""{"type":"service_account","client_email":123,"private_key":"k"}""")]
    [InlineData("""{"type":"service_account","client_email":["a@b.c"],"private_key":"k"}""")]
    [InlineData("""{"type":"service_account","client_email":"a@b.c","private_key":12345}""")]
    [InlineData("""{"type":"service_account","client_email":"a@b.c","private_key":{"nested":"object"}}""")]
    [InlineData("""{"type":"service_account","client_email":"a@b.c","private_key":true}""")]
    [InlineData("""{"type":123,"client_email":"a@b.c","private_key":"k"}""")]
    public void FromJsonObject_RequiredFieldWithWrongPrimitiveType_IsNotUsable(string json)
    {
        var key = Parse(json);

        Assert.False(key.IsUsableServiceAccount, "a non-string value for a required field must not be coerced into usable");
    }

    [Fact]
    public void FromJsonObject_WrongTypeValue_IsNotUsable()
    {
        var key = Parse("""{"type":"authorized_user","account":"someone@example.invalid","client_id":"123.apps.googleusercontent.com","refresh_token":"not-read"}""");

        Assert.False(key.IsUsableServiceAccount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    public void FromJsonObject_NonObjectRoot_AnswersFalseRatherThanThrowing(string json)
    {
        using var document = JsonDocument.Parse(json);

        var key = GeminiServiceAccountKey.FromJsonObject(document.RootElement);

        Assert.False(key.IsUsableServiceAccount);
        Assert.Equal(string.Empty, key.ClientEmail);
    }

    [Fact]
    public void ToString_NeverContainsThePrivateKey()
    {
        const string secretKey = "SENTINEL_PRIVATE_KEY_UNSAFE_4d8b";
        var key = new GeminiServiceAccountKey("service_account", "robot@example.com", secretKey, "my-project");

        Assert.DoesNotContain(secretKey, key.ToString(), StringComparison.Ordinal);
    }
}
