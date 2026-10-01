using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

[Trait("Category", "Contract")]
public sealed class OpenAiCodexAuthPathResolverTests
{
    [Fact]
    public void Resolve_UsesDefaultDotCodexUnderUserProfile_WhenOverrideIsNull()
    {
        var path = OpenAiCodexAuthPathResolver.Resolve(null, @"C:\Users\test");

        Assert.Equal(Path.Combine(@"C:\Users\test", ".codex", "auth.json"), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_UsesDefaultDotCodex_WhenOverrideIsBlank(string blankOverride)
    {
        var path = OpenAiCodexAuthPathResolver.Resolve(blankOverride, @"C:\Users\test");

        Assert.Equal(Path.Combine(@"C:\Users\test", ".codex", "auth.json"), path);
    }

    [Fact]
    public void Resolve_UsesOverride_WhenNonBlank()
    {
        var path = OpenAiCodexAuthPathResolver.Resolve(@"D:\custom\codex-home", @"C:\Users\test");

        Assert.Equal(Path.Combine(@"D:\custom\codex-home", "auth.json"), path);
    }

    [Fact]
    public void Resolve_RequiresNonBlankUserProfileDirectory()
    {
        Assert.Throws<ArgumentException>(() => OpenAiCodexAuthPathResolver.Resolve(null, ""));
    }
}
