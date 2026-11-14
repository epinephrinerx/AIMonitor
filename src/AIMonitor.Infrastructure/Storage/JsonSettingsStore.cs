using System.Text.Json;
using AIMonitor.Application.Settings;

namespace AIMonitor.Infrastructure.Storage;

public sealed class JsonSettingsStore : ISettingsStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IAtomicFileWriter _writer;

    public JsonSettingsStore(string path)
        : this(path, new AtomicFileWriter())
    {
    }

    internal JsonSettingsStore(string path, IAtomicFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public bool Exists => File.Exists(_path);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists)
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumFileBytes)
            {
                throw new JsonException("Settings file exceeds the supported size.");
            }

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version is < 1 or > AppSettings.CurrentSchemaVersion)
            {
                throw new JsonException("Unsupported or missing settings schema version.");
            }

            stream.Position = 0;
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                throw new JsonException("Settings document was empty.");
            }

            return settings.Normalize();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            BackupCorruptFile();
            return new AppSettings();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(), SerializerOptions);
        return _writer.WriteAsync(_path, bytes, cancellationToken);
    }

    private void BackupCorruptFile()
    {
        try
        {
            var backupPath = _path + ".bak";
            if (File.Exists(backupPath))
            {
                backupPath = $"{backupPath}.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            }

            File.Copy(_path, backupPath, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
