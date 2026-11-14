using System.Text.Json;
using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>Exercises <see cref="ClaudeTranscriptStore.ParseTokenCount"/> directly - the same rules
/// applied to every token field read out of a transcript record.</summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreTokenCountTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseTokenCount_MissingProperty_IsZero() =>
        Assert.Equal(0, ClaudeTranscriptStore.ParseTokenCount(null));

    [Fact]
    public void ParseTokenCount_JsonNull_IsZero() =>
        Assert.Equal(0, ClaudeTranscriptStore.ParseTokenCount(Parse("null")));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12345", 12345)]
    public void ParseTokenCount_PlainIntegers_PassThrough(string json, long expected) =>
        Assert.Equal(expected, ClaudeTranscriptStore.ParseTokenCount(Parse(json)));

    [Fact]
    public void ParseTokenCount_EmptyString_IsZero() =>
        Assert.Equal(0, ClaudeTranscriptStore.ParseTokenCount(Parse("\"\"")));

    [Fact]
    public void ParseTokenCount_WhitespaceOnlyString_IsZero() =>
        Assert.Equal(0, ClaudeTranscriptStore.ParseTokenCount(Parse("\"   \"")));

    [Theory]
    [InlineData("\"42\"", 42)]
    [InlineData("\" 42 \"", 42)]
    public void ParseTokenCount_NumericStrings_AreAccepted(string json, long expected) =>
        Assert.Equal(expected, ClaudeTranscriptStore.ParseTokenCount(Parse(json)));

    [Fact]
    public void ParseTokenCount_WholeFloat_IsAccepted() =>
        Assert.Equal(1, ClaudeTranscriptStore.ParseTokenCount(Parse("1.0")));

    [Fact]
    public void ParseTokenCount_FractionalFloat_IsRejected() =>
        Assert.Null(ClaudeTranscriptStore.ParseTokenCount(Parse("1.5")));

    [Fact]
    public void ParseTokenCount_True_IsRejected() =>
        Assert.Null(ClaudeTranscriptStore.ParseTokenCount(Parse("true")));

    [Fact]
    public void ParseTokenCount_False_IsRejected() =>
        Assert.Null(ClaudeTranscriptStore.ParseTokenCount(Parse("false")));

    [Theory]
    [InlineData("-1")]
    [InlineData("\"-1\"")]
    [InlineData("\"not-a-number\"")]
    [InlineData("[1]")]
    [InlineData("{\"n\":1}")]
    public void ParseTokenCount_NegativesAndNonsense_AreRejected(string json) =>
        Assert.Null(ClaudeTranscriptStore.ParseTokenCount(Parse(json)));
}
