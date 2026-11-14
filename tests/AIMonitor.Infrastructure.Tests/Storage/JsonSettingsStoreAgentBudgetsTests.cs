using AIMonitor.Application.Settings;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Tests.Storage;

/// <summary>
/// R-015 Q1.1 persistence — <c>AgentBudgets</c> through the existing JSON settings store, written from
/// Docs/SPEC_R015.md only (§5.6): Q1.1 "an old settings file without the property loads as no budgets,
/// SchemaVersion stays 1" and E8 "JSON round trip of { "opencode": 20.01 } must give the same value"
/// with the comparer (lost by JSON) rebuilt by Normalize on load.
/// </summary>
public sealed class JsonSettingsStoreAgentBudgetsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aimonitor-budgets-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoadAsync_E8_AgentBudgets_RoundTripTheSameValueAndComparer()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20.01m },
        });
        var loaded = await store.LoadAsync();

        // E8: round trip gives the same value…
        Assert.Equal(20.01m, loaded.AgentBudgets["opencode"]);
        var entry = Assert.Single(loaded.AgentBudgets);
        Assert.Equal("opencode", entry.Key);

        // …and the OrdinalIgnoreCase comparer survives the JSON load (Normalize rebuilds it).
        Assert.True(loaded.AgentBudgets.ContainsKey("OpenCode"));
        Assert.Equal(20.01m, loaded.AgentBudgets["OPENCODE"]);
    }

    [Fact]
    public async Task LoadAsync_Q11_SettingsFileWithoutAgentBudgets_LoadsAsNoBudgets()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"theme\":\"dark\"}");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        // Q1.1: an old settings file without the property loads as "no budgets"; SchemaVersion stays 1.
        Assert.Equal("dark", loaded.Theme);
        Assert.Empty(loaded.AgentBudgets);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [Fact]
    public async Task LoadAsync_Q11_AgentBudgetsExplicitlyNull_LoadsAsEmptyBudgets()
    {
        // Loading a file whose agentBudgets is explicitly null must normalise to empty (Q1.1 null rule),
        // mirroring the existing LoadAsync_NullCollections_NormalizesToEmptyCollections pattern.
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"agentBudgets\":null}");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Empty(loaded.AgentBudgets);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
