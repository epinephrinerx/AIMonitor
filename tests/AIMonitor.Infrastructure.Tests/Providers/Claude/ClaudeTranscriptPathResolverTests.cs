using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

[Trait("Category", "Contract")]
public class ClaudeTranscriptPathResolverTests
{
    [Fact]
    public void Resolve_NonBlankOverride_UsesOverrideDirectory()
    {
        var path = ClaudeTranscriptPathResolver.Resolve(@"C:\override\claude-config", @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\override\claude-config", "projects"), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverride_FallsBackToUserProfileDotClaude(string? blankOverride)
    {
        var path = ClaudeTranscriptPathResolver.Resolve(blankOverride, @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\Users\someone", ".claude", "projects"), path);
    }

    [Fact]
    public void Resolve_IsDeterministic_ForTheSameInputs()
    {
        var first = ClaudeTranscriptPathResolver.Resolve(null, @"C:\Users\someone");
        var second = ClaudeTranscriptPathResolver.Resolve(null, @"C:\Users\someone");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DoesNotExpandEnvironmentTokensOrTilde()
    {
        var path = ClaudeTranscriptPathResolver.Resolve("%CLAUDE_CONFIG_DIR%", @"C:\Users\someone");

        Assert.Equal(Path.Combine("%CLAUDE_CONFIG_DIR%", "projects"), path);
    }

    [Fact]
    public void Resolve_BlankUserProfileDirectory_Throws() =>
        Assert.Throws<ArgumentException>(() => ClaudeTranscriptPathResolver.Resolve(null, " "));

    [Fact]
    public void Resolve_AgreesWithCredentialResolverOnTheConfigDirectory()
    {
        var credentialsPath = ClaudeCredentialPathResolver.Resolve(null, @"C:\Users\someone");
        var transcriptsPath = ClaudeTranscriptPathResolver.Resolve(null, @"C:\Users\someone");

        Assert.Equal(Path.GetDirectoryName(credentialsPath), Path.GetDirectoryName(transcriptsPath));
    }
}
