using System.Text.Json;
using AIMonitor.Application.Settings;

namespace AIMonitor.Infrastructure.Storage;

public sealed class DpapiSecretStore : ISecretStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly IAtomicFileWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiSecretStore(string path)
        : this(path, new WindowsDpapiProtector("AIMonitor2.secrets.v1"), new AtomicFileWriter())
    {
    }

    internal DpapiSecretStore(string path, ISecretProtector protector, IAtomicFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            return values.GetValueOrDefault(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrEmpty(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            values[name] = value;
            await WriteAllAsync(values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (values.Remove(name))
            {
                await WriteAllAsync(values, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var fileInfo = new FileInfo(_path);
        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Protected secret store exceeds the supported size.");
        }

        var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var plaintext = _protector.Unprotect(protectedBytes);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                ?? new(StringComparer.Ordinal);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private async Task WriteAllAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var protectedBytes = _protector.Protect(plaintext);
            try
            {
                await _writer.WriteAsync(_path, protectedBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(protectedBytes);
            }
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256) throw new ArgumentOutOfRangeException(nameof(name));
    }
}
