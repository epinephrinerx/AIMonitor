using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>
/// The 2.0.4 additions to the connections page: Claude's optional Admin key (R-019), the OpenCode
/// card (R-020) and the other agents ccusage found (R-021).
/// </summary>
public sealed class ConnectionsAgentsTests
{
    private static async Task<(ConnectionsViewModel Page, TestSecretStore Secrets)> BuildAsync()
    {
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore());
        var store = new TestSecretStore();
        return (new ConnectionsViewModel(session, new ProviderConnectionStore(store, session)), store);
    }

    [Theory]
    [InlineData("claude", "sk-ant-admin01-abc", "Admin API key format recognized (sk-ant-admin…).")]
    [InlineData("claude", "sk-ant-api03-abc", "API usage and cost require an organization Admin key (sk-ant-admin…). Ordinary or workspace API keys cannot read organization usage.")]
    [InlineData("openai", "sk-admin-abc", "Admin API key format recognized (sk-admin-…).")]
    [InlineData("openai", "sk-proj-abc", "API spend requires an organization Admin key (sk-admin-…). Project or user keys (e.g. sk-proj-…) cannot read organization usage or costs.")]
    public async Task ATypedKey_IsCheckedForTheAdminKeyFormat(string providerId, string key, string expected)
    {
        var (page, _) = await BuildAsync();
        var card = page.Cards.Single(c => c.ProviderId == providerId);

        card.Key = key;

        Assert.Equal(expected, card.KeyHint);
        Assert.True(card.HasKeyHint);
    }

    [Fact]
    public async Task AnEmptyKeyField_SaysNothing()
    {
        var (page, _) = await BuildAsync();

        Assert.False(page.Cards[0].HasKeyHint);
    }

    [Fact]
    public async Task AClaudeAdminKey_IsSavedForTheClaudeClientToRead()
    {
        var (page, secrets) = await BuildAsync();
        var claude = page.Cards.Single(c => c.ProviderId == "claude");
        claude.Key = "sk-ant-admin01-typed";

        await page.SaveAsync(claude);

        Assert.Equal("sk-ant-admin01-typed", secrets.Secrets["providers/claude/key"]);
        Assert.Equal(string.Empty, claude.Key);
    }

    [Fact]
    public async Task ClaudeWithOnlyTheAdminKey_IsLimited()
    {
        var (page, _) = await BuildAsync();
        var snapshot = new ProviderSnapshot("claude", configured: true, meters: [], adminOnly: true);

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = snapshot });

        Assert.Equal("Limited · Admin key connected · Claude Code not signed in", page.Cards[0].Status);
    }

    [Fact]
    public async Task TheOpenCodeCard_IsCheckingUntilTheFirstAnswer_AndIgnoresServiceSnapshots()
    {
        var (page, _) = await BuildAsync();

        page.Update(new Dictionary<string, ProviderSnapshot> { ["opencode"] = TestData.Snapshot("opencode") });

        Assert.Equal("Checking...", page.OpenCodeCard.Status);
    }

    [Fact]
    public async Task OpenCodeNotOnPath_SaysSoAndHowToInstall()
    {
        var (page, _) = await BuildAsync();

        page.UpdateOpenCode(new OpenCodeAuthStatus(false, "", [], false, ""));

        Assert.Equal("Not connected · OpenCode not found on PATH", page.OpenCodeCard.Status);
        Assert.Contains("opencode auth login", page.OpenCodeCard.SetupHint, StringComparison.Ordinal);
        Assert.False(page.OpenCodeCard.IsConnected);
    }

    [Fact]
    public async Task OpenCodeWithSignIns_ListsThemAndWhereTheyCameFrom()
    {
        var (page, _) = await BuildAsync();

        page.UpdateOpenCode(new OpenCodeAuthStatus(
            true, "auth.json", [new OpenCodeSignIn("anthropic", "OAuth"), new OpenCodeSignIn("openai", "API key")], true, ""));

        Assert.True(page.OpenCodeCard.IsConnected);
        Assert.Equal("Signed in: anthropic (OAuth), openai (API key)", page.OpenCodeCard.Account);
        Assert.StartsWith("Connected · detected from auth.json · may be out of date", page.OpenCodeCard.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenCodeReadError_IsShownAsUnavailable()
    {
        var (page, _) = await BuildAsync();

        page.UpdateOpenCode(new OpenCodeAuthStatus(true, "", [], false, "Could not read OpenCode sign-ins."));

        Assert.Equal("Unavailable · could not check", page.OpenCodeCard.Status);
        Assert.Equal("Could not read OpenCode sign-ins.", page.OpenCodeCard.SetupHint);
    }

    [Fact]
    public async Task OtherAgents_ListOnlyAgentsWithoutATabOfTheirOwn()
    {
        var (page, _) = await BuildAsync();
        var today = new DateOnly(2026, 11, 14);
        AgentUsage Agent(string id) => new(id, new TokenCounts(1, 0, 0, 0, 1), []);
        var report = new AgentUsageReport(
            [new DailyAgentUsage(today, [Agent("claude"), Agent("copilot"), Agent("opencode"), Agent("zcode")])],
            [], [], [], AgentUsagePricing.Offline, DateTimeOffset.UtcNow, 0);

        page.UpdateOtherAgents(report, null);

        Assert.Equal(["Copilot", "ZCode"], page.OtherAgents.Select(row => row.DisplayName));
        Assert.True(page.HasOtherAgents);
    }

    [Fact]
    public async Task BeforeCcusageAnswers_OtherAgentsSaysItIsChecking()
    {
        var (page, _) = await BuildAsync();

        Assert.Equal("Checking ccusage…", page.OtherAgentsStatus);
        Assert.False(page.HasOtherAgents);
    }
}
