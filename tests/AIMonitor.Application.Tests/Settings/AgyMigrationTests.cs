using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

public sealed class AgyMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacySlot_MigratesEnabledAndSelection_WithoutProject(bool enabled)
    {
        var original = new AppSettings
        {
            ActiveProvider = "gemini",
            Providers = new Dictionary<string, ProviderPreference>
            { ["gemini"] = new(enabled, "old-project") }
        };
        var result = original.Normalize();
        Assert.Equal("agy", result.ActiveProvider);
        Assert.Equal(enabled, result.Providers["agy"].Enabled);
        Assert.Empty(result.Providers["agy"].Extra);
        Assert.Equal("old-project", result.Providers["gemini"].Extra);
        Assert.False(original.Providers.ContainsKey("agy"));
        Assert.Equal(result.Providers, result.Normalize().Providers);
    }

    [Fact]
    public void ExplicitAgyPreference_WinsOverLegacy()
    {
        var result = new AppSettings
        {
            ActiveProvider = "agy",
            Providers = new Dictionary<string, ProviderPreference>
            { ["gemini"] = new(false, "old-project"), ["agy"] = new(true) }
        }.Normalize();
        Assert.True(result.Providers["agy"].Enabled);
        Assert.Equal("agy", result.ActiveProvider);
    }

    [Fact]
    public async Task AgyConnection_DoesNotReadOrWriteSecrets()
    {
        using var session = new SettingsSession(new MemoryStore(), new AppSettings());
        var store = new ProviderConnectionStore(new ForbiddenSecrets(), session);
        var connection = await store.GetAsync("agy");
        Assert.Null(connection.Key);
        Assert.Empty(connection.Extra!);
        await store.SaveAsync("agy", "", true, null);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("agy", "secret", false, null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("agy", "", false, "project"));
    }

    private sealed class ForbiddenSecrets : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Secret read forbidden");
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Secret write forbidden");
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Secret removal forbidden");
    }
    private sealed class MemoryStore : ISettingsStore
    {
        public bool Exists => true;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
