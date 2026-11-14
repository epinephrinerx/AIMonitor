using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class ConnectionsViewModelTests
{
    private const string KeyName = "providers/openai/key";

    private static async Task<(ConnectionsViewModel Page, SettingsSession Session, TestSecretStore Secrets)>
        BuildAsync(AppSettings? settings = null, IDictionary<string, string>? secrets = null)
    {
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore(settings));
        var store = new TestSecretStore(secrets);
        return (new ConnectionsViewModel(session, new ProviderConnectionStore(store, session)), session, store);
    }

    private static ConnectionCardViewModel Card(ConnectionsViewModel page, string providerId) =>
        page.Cards.Single(card => card.ProviderId == providerId);

    [Fact]
    public async Task ShowsOneCardPerService_ThenOpenCode()
    {
        var (page, _, _) = await BuildAsync();

        Assert.Equal(["claude", "openai", "agy", "opencode"], page.Cards.Select(card => card.ProviderId));
    }

    [Fact]
    public async Task OnlyTheServicesThatTakeAKeyOfferOne()
    {
        var (page, _, _) = await BuildAsync();

        // R-019: Claude takes an optional Anthropic Admin key for API usage and cost.
        Assert.True(Card(page, "claude").NeedsKey);
        Assert.True(Card(page, "openai").NeedsKey);
        Assert.False(Card(page, "agy").NeedsKey);
        Assert.False(Card(page, "opencode").NeedsKey);
    }

    [Fact]
    public async Task ACardNeverShowsTheStoredKeyBack()
    {
        var (page, _, _) = await BuildAsync(secrets: new Dictionary<string, string> { [KeyName] = "sk-admin-secret" });

        // Filling the field in would put the user's key on screen for anyone walking past.
        Assert.Equal(string.Empty, Card(page, "openai").Key);
    }

    [Fact]
    public async Task ASnapshotWithAnErrorIsShownAsThatError()
    {
        var (page, _, _) = await BuildAsync();

        page.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = TestData.Snapshot("claude", configured: false, meters: [], error: "No Claude Code login found"),
        });

        Assert.Equal("No Claude Code login found", Card(page, "claude").Status);
        Assert.False(Card(page, "claude").IsConnected);
    }

    [Fact]
    public async Task AHealthySnapshotIsShownAsConnected()
    {
        var (page, _, _) = await BuildAsync();

        page.Update(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = TestData.Snapshot("claude", account: "someone@example.com"),
        });

        Assert.Equal("Connected", Card(page, "claude").Status);
        Assert.True(Card(page, "claude").IsConnected);
        Assert.Equal("someone@example.com", Card(page, "claude").Account);
    }

    [Fact]
    public async Task AServiceMissingFromTheRefreshIsShownAsNotMonitored_NotAsAFailure()
    {
        var (page, _, _) = await BuildAsync();

        page.Update(new Dictionary<string, ProviderSnapshot>());

        Assert.Equal("Not monitored", Card(page, "openai").Status);
    }

    [Fact]
    public async Task SavingAKey_StoresItAndEmptiesTheField()
    {
        var (page, _, secrets) = await BuildAsync();
        var card = Card(page, "openai");
        card.Key = "  sk-admin-typed  ";

        await page.SaveAsync(card);

        Assert.Equal("sk-admin-typed", secrets.Secrets[KeyName]);
        Assert.Equal(string.Empty, card.Key);
    }

    [Fact]
    public async Task SavingAnEmptyField_LeavesTheStoredKeyAlone()
    {
        var (page, _, secrets) = await BuildAsync(
            secrets: new Dictionary<string, string> { [KeyName] = "sk-admin-existing" });
        var card = Card(page, "openai");

        await page.SaveAsync(card);

        // Tabbing past an empty field must not wipe a key the user set weeks ago.
        Assert.Equal("sk-admin-existing", secrets.Secrets[KeyName]);
        Assert.Equal(0, secrets.RemoveCalls);
    }

    [Fact]
    public async Task ClearingAKey_RemovesItFromTheStore()
    {
        var (page, _, secrets) = await BuildAsync(
            secrets: new Dictionary<string, string> { [KeyName] = "sk-admin-existing" });

        await page.ClearAsync(Card(page, "openai"));

        // On macOS the stored secret lives in the Keychain, so clearing has to reach it — a cleared
        // setting alone would leave the real key behind.
        Assert.DoesNotContain(KeyName, secrets.Secrets.Keys);
        Assert.Equal(1, secrets.RemoveCalls);
    }

    [Fact]
    public async Task SavingAKeyAsksForThatServiceToBeCheckedAgain()
    {
        var (page, _, _) = await BuildAsync();
        string? asked = null;
        page.RedetectRequested += id => asked = id;
        var card = Card(page, "openai");
        card.Key = "sk-admin-typed";

        await page.SaveAsync(card);

        Assert.Equal("openai", asked);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("-5")]
    public async Task ABudgetThatIsNotAPositiveNumber_IsRefusedAndNothingIsStored(string typed)
    {
        var (page, _, secrets) = await BuildAsync();
        var card = Card(page, "openai");
        card.Key = "sk-admin-typed";
        card.Extra = typed;

        await page.SaveAsync(card);

        Assert.True(page.HasMessage);
        Assert.Equal(0, secrets.SetCalls);
    }

    [Fact]
    public async Task ABudgetIsStoredBesideTheService()
    {
        var (page, session, _) = await BuildAsync();
        var card = Card(page, "openai");
        card.Key = "sk-admin-typed";
        card.Extra = "120";

        await page.SaveAsync(card);
        await session.FlushAsync();

        Assert.Equal("120", session.Current.Providers["openai"].Extra);
    }

    [Fact]
    public async Task AServiceWithNoKeyToGive_SaysSoRatherThanFailing()
    {
        var (page, _, secrets) = await BuildAsync();

        await page.SaveAsync(Card(page, "agy"));

        Assert.Contains("signed in on this machine", page.Message, StringComparison.Ordinal);
        Assert.Equal(0, secrets.SetCalls);
    }

    [Fact]
    public async Task WhenTheStoreRefusesTheKey_TheReasonIsShownInsteadOfThrowing()
    {
        var (page, _, secrets) = await BuildAsync();
        secrets.FailOnSet = new IOException("the keychain is locked");
        var card = Card(page, "openai");
        card.Key = "sk-admin-typed";

        await page.SaveAsync(card);

        Assert.Contains("the keychain is locked", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowAtStartup_IsRemembered()
    {
        var (page, session, _) = await BuildAsync(new AppSettings { ShowConnectionsAtStartup = true });

        page.ShowAtStartup = false;
        await session.FlushAsync();

        Assert.False(session.Current.ShowConnectionsAtStartup);
    }
}
