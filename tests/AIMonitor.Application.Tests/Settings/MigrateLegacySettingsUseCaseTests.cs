using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

public sealed class MigrateLegacySettingsUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_NestedValuesAndSecret_ImportsWithoutMutatingLegacySource()
    {
        var source = new Dictionary<string, object?>
        {
            ["theme"] = "dark",
            ["widget/opacity"] = "0.72",
            ["range_days"] = 14,
            ["providers/openai/key"] = "sealed-admin-key",
        };
        var original = source.ToDictionary();
        var settingsStore = new FakeSettingsStore();
        var secretStore = new FakeSecretStore();
        var useCase = new MigrateLegacySettingsUseCase(
            settingsStore,
            secretStore,
            new FakeLegacyReader(source),
            new FakeUnsealer("sealed-admin-key", "synthetic-secret"));

        var result = await useCase.ExecuteAsync();

        Assert.True(result.Migrated);
        Assert.Equal(3, result.ImportedSettings);
        Assert.Equal(1, result.ImportedSecrets);
        Assert.Equal("dark", settingsStore.Value!.Theme);
        Assert.Equal(0.72, settingsStore.Value.WidgetOpacity);
        Assert.Equal(14, settingsStore.Value.ChartRangeDays);
        Assert.Equal("synthetic-secret", secretStore.Values["providers/openai/key"]);
        Assert.Equal(original, source);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSettingsExist_DoesNotReadLegacyAgain()
    {
        var reader = new FakeLegacyReader(new Dictionary<string, object?>()) { ThrowOnRead = true };
        var result = await new MigrateLegacySettingsUseCase(
            new FakeSettingsStore { Exists = true },
            new FakeSecretStore(),
            reader,
            new FakeUnsealer(string.Empty, string.Empty)).ExecuteAsync();

        Assert.False(result.Migrated);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_OneSecretFails_ImportsOthersAndKeepsSourceUntouched()
    {
        var source = new Dictionary<string, object?>
        {
            ["providers/openai/key"] = "bad",
            ["providers/gemini/key"] = "good",
        };
        var secretStore = new FakeSecretStore();
        var result = await new MigrateLegacySettingsUseCase(
            new FakeSettingsStore(), secretStore, new FakeLegacyReader(source), new FakeUnsealer("good", "value"))
            .ExecuteAsync();

        Assert.Equal(1, result.ImportedSecrets);
        Assert.Single(result.Warnings);
        Assert.Equal("bad", source["providers/openai/key"]);
        Assert.Equal("good", source["providers/gemini/key"]);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesProviderWidgetAndBoundedGeometryPreferences()
    {
        var source = new Dictionary<string, object?>
        {
            ["widget/rotate"] = false,
            ["showConnectionsAtStartup"] = false,
            ["activeProvider"] = "gemini",
            ["providers/gemini/enabled"] = false,
            ["providers/gemini/extra"] = "synthetic-project",
            ["geometry/dashboard"] = new byte[] { 1, 2, 3 },
        };
        for (var index = 0; index < 10; index++)
        {
            var layout = $"d{index:x10}";
            source[$"geometry/{layout}/usedAt"] = $"UTC:2026-09-{index + 1:00}";
            source[$"geometry/{layout}/dashboard"] = $"rect-{index}";
        }
        var store = new FakeSettingsStore();

        await new MigrateLegacySettingsUseCase(
            store, new FakeSecretStore(), new FakeLegacyReader(source), new FakeUnsealer("", "")).ExecuteAsync();

        var settings = Assert.IsType<AppSettings>(store.Value);
        Assert.False(settings.WidgetRotationEnabled);
        Assert.False(settings.ShowConnectionsAtStartup);
        Assert.Equal("gemini", settings.ActiveProvider);
        Assert.Equal(new ProviderPreference(false, "synthetic-project"), settings.Providers["gemini"]);
        Assert.Equal("base64:AQID", settings.LegacyGeometry["dashboard"]);
        Assert.Equal(17, settings.LegacyGeometry.Count); // unscoped + two values for each of 8 layouts
        Assert.DoesNotContain(settings.LegacyGeometry.Keys, key => key.StartsWith("d0000000000/", StringComparison.Ordinal));
        Assert.DoesNotContain(settings.LegacyGeometry.Keys, key => key.StartsWith("d0000000001/", StringComparison.Ordinal));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public bool Exists { get; set; }
        public AppSettings? Value { get; private set; }
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value ?? new());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Value = settings;
            Exists = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(name));
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            Values[name] = value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLegacyReader(IReadOnlyDictionary<string, object?> values) : ILegacySettingsReader
    {
        public bool ThrowOnRead { get; init; }
        public int ReadCount { get; private set; }
        public IReadOnlyDictionary<string, object?> ReadAll()
        {
            ReadCount++;
            if (ThrowOnRead) throw new InvalidOperationException();
            return values;
        }
    }

    private sealed class FakeUnsealer(string accepted, string plaintext) : ILegacySecretUnsealer
    {
        public bool TryUnseal(string protectedValue, out string value)
        {
            value = protectedValue == accepted ? plaintext : string.Empty;
            return protectedValue == accepted;
        }
    }
}
