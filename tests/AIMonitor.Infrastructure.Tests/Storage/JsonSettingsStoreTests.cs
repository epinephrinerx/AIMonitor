using AIMonitor.Application.Settings;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Tests.Storage;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aimonitor-settings-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsNormalizedSettingsWithSchemaVersion()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(new AppSettings { Theme = "dark", ChartRangeDays = 14, WidgetOpacity = 0.8 });
        var loaded = await store.LoadAsync();

        Assert.True(store.Exists);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(14, loaded.ChartRangeDays);
        Assert.Equal(0.8, loaded.WidgetOpacity);
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_BacksUpAndReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "not-json");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal("system", loaded.Theme);
        Assert.Equal(180, loaded.RefreshIntervalSeconds);
        Assert.Empty(loaded.Providers);
        Assert.Empty(loaded.LegacyGeometry);
        Assert.Equal("not-json", await File.ReadAllTextAsync(path + ".bak"));
        Assert.Equal("not-json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadAsync_MissingSchemaVersion_BacksUpAndReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"theme\":\"dark\"}");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Equal("system", loaded.Theme);
        Assert.Equal(180, loaded.RefreshIntervalSeconds);
        Assert.Empty(loaded.Providers);
        Assert.Empty(loaded.LegacyGeometry);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task LoadAsync_NullCollections_NormalizesToEmptyCollections()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"providers\":null,\"legacyGeometry\":null}");

        var loaded = await new JsonSettingsStore(path).LoadAsync();

        Assert.Empty(loaded.Providers);
        Assert.Empty(loaded.LegacyGeometry);
    }

    [Fact]
    public async Task SaveAsync_WhenWriterFails_LeavesExistingFileUnchanged()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "original");
        var store = new JsonSettingsStore(path, new ThrowingWriter());

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new AppSettings()));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ThrowingWriter : IAtomicFileWriter
    {
        public Task WriteAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("synthetic failure"));
    }
}
