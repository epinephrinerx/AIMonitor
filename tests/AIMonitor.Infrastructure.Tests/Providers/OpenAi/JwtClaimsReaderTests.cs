using System.Text;
using System.Text.Json;
using AIMonitor.Infrastructure.Providers.OpenAI;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

/// <summary>
/// <see cref="JwtClaimsReader"/> is internal (accessible here via
/// <c>InternalsVisibleTo</c>) and is exercised directly because it is display-only, untrusted-input
/// parsing that deserves its own coverage independent of <see cref="CodexOAuthParser"/>: it must never
/// throw regardless of how malformed the token is, and it must never attempt any signature check.
/// </summary>
[Trait("Category", "Contract")]
public class JwtClaimsReaderTests
{
    private static string Base64Url(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string MakeToken(string payloadJson) => $"header.{Base64Url(payloadJson)}.signature";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryGetClaims_NullOrEmptyToken_ReturnsNull(string? token)
    {
        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Theory]
    [InlineData("only-one-segment")]
    [InlineData("two.segments")]
    [InlineData("way.too.many.segments")]
    public void TryGetClaims_WrongSegmentCount_ReturnsNull(string token)
    {
        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Fact]
    public void TryGetClaims_PayloadSegmentNotValidBase64_ReturnsNullRatherThanThrowing()
    {
        var token = "header.not!!!valid@@@base64url.signature";

        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Fact]
    public void TryGetClaims_PayloadDecodesToNonJson_ReturnsNull()
    {
        var token = $"header.{Base64Url("this is not json")}.signature";

        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void TryGetClaims_PayloadIsNotAJsonObject_ReturnsNull(string nonObjectJson)
    {
        var token = MakeToken(nonObjectJson);

        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Fact]
    public void TryGetClaims_ValidPayload_ReturnsClaimsObject()
    {
        var token = MakeToken("""{"email":"user@example.com","exp":1700000000}""");

        var claims = JwtClaimsReader.TryGetClaims(token);

        Assert.NotNull(claims);
        Assert.Equal(JsonValueKind.Object, claims!.Value.ValueKind);
    }

    [Fact]
    public void TryGetClaims_TokenLongerThanCap_ReturnsNullRatherThanDecoding()
    {
        var hugePayload = Base64Url(new string('a', 200_000));
        var token = $"header.{hugePayload}.signature";

        Assert.Null(JwtClaimsReader.TryGetClaims(token));
    }

    [Fact]
    public void GetString_ClaimsNull_ReturnsNull()
    {
        Assert.Null(JwtClaimsReader.GetString(null, "email"));
    }

    [Fact]
    public void GetString_PropertyMissing_ReturnsNull()
    {
        var claims = JwtClaimsReader.TryGetClaims(MakeToken("""{"sub":"abc"}"""));

        Assert.Null(JwtClaimsReader.GetString(claims, "email"));
    }

    [Fact]
    public void GetString_PropertyPresentButNotAString_ReturnsNull()
    {
        var claims = JwtClaimsReader.TryGetClaims(MakeToken("""{"email":123}"""));

        Assert.Null(JwtClaimsReader.GetString(claims, "email"));
    }

    [Fact]
    public void GetString_PropertyPresentAsString_ReturnsValue()
    {
        var claims = JwtClaimsReader.TryGetClaims(MakeToken("""{"email":"user@example.com"}"""));

        Assert.Equal("user@example.com", JwtClaimsReader.GetString(claims, "email"));
    }

    [Fact]
    public void GetNumber_ClaimsNull_ReturnsNull()
    {
        Assert.Null(JwtClaimsReader.GetNumber(null, "exp"));
    }

    [Fact]
    public void GetNumber_PropertyMissingOrNotNumeric_ReturnsNull()
    {
        var claims = JwtClaimsReader.TryGetClaims(MakeToken("""{"exp":"not-a-number"}"""));

        Assert.Null(JwtClaimsReader.GetNumber(claims, "exp"));
    }

    [Fact]
    public void GetNumber_PropertyNumeric_ReturnsElement()
    {
        var claims = JwtClaimsReader.TryGetClaims(MakeToken("""{"exp":1700000000}"""));

        var value = JwtClaimsReader.GetNumber(claims, "exp");

        Assert.NotNull(value);
        Assert.True(value!.Value.TryGetInt64(out var parsed));
        Assert.Equal(1700000000L, parsed);
    }
}
