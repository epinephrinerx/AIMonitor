using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

[Trait("Category", "Contract")]
public class ClaudeCredentialPathResolverTests
{
    [Fact]
    public void Resolve_NonBlankOverride_UsesOverrideDirectory()
    {
        var path = ClaudeCredentialPathResolver.Resolve(@"C:\override\claude-config", @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\override\claude-config", ".credentials.json"), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverride_FallsBackToUserProfileDotClaude(string? blankOverride)
    {
        var path = ClaudeCredentialPathResolver.Resolve(blankOverride, @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\Users\someone", ".claude", ".credentials.json"), path);
    }

    [Fact]
    public void Resolve_IsDeterministic_ForTheSameInputs()
    {
        var first = ClaudeCredentialPathResolver.Resolve(null, @"C:\Users\someone");
        var second = ClaudeCredentialPathResolver.Resolve(null, @"C:\Users\someone");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DoesNotExpandEnvironmentTokensOrTilde()
    {
        var path = ClaudeCredentialPathResolver.Resolve("%CLAUDE_CONFIG_DIR%", @"C:\Users\someone");

        Assert.Equal(Path.Combine("%CLAUDE_CONFIG_DIR%", ".credentials.json"), path);
    }

    [Fact]
    public void Resolve_BlankUserProfileDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => ClaudeCredentialPathResolver.Resolve(null, " "));
    }
}
