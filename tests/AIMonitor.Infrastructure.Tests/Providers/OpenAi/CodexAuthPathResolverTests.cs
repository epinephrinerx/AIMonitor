using AIMonitor.Infrastructure.Providers.OpenAI;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

[Trait("Category", "Contract")]
public class CodexAuthPathResolverTests
{
    [Fact]
    public void Resolve_NonBlankOverride_UsesOverrideDirectory()
    {
        var path = CodexAuthPathResolver.Resolve(@"C:\override\codex-home", @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\override\codex-home", "auth.json"), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverride_FallsBackToUserProfileDotCodex(string? blankOverride)
    {
        var path = CodexAuthPathResolver.Resolve(blankOverride, @"C:\Users\someone");

        Assert.Equal(Path.Combine(@"C:\Users\someone", ".codex", "auth.json"), path);
    }

    [Fact]
    public void Resolve_IsDeterministic_ForTheSameInputs()
    {
        var first = CodexAuthPathResolver.Resolve(null, @"C:\Users\someone");
        var second = CodexAuthPathResolver.Resolve(null, @"C:\Users\someone");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DoesNotExpandEnvironmentTokensOrTilde()
    {
        var path = CodexAuthPathResolver.Resolve("%CODEX_HOME%", @"C:\Users\someone");

        Assert.Equal(Path.Combine("%CODEX_HOME%", "auth.json"), path);
    }

    [Fact]
    public void Resolve_BlankUserProfileDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => CodexAuthPathResolver.Resolve(null, " "));
    }
}
