using System.Text;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Tests.Storage;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aimonitor-secrets-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAndGetAsync_WritesOnlyProtectedBytesToDisk()
    {
        var path = Path.Combine(_directory, "secrets.dat");
        var store = new DpapiSecretStore(path, new ReversingProtector(), new AtomicFileWriter());

        await store.SetAsync("providers/openai/key", "synthetic-admin-secret");

        Assert.Equal("synthetic-admin-secret", await store.GetAsync("providers/openai/key"));
        Assert.DoesNotContain("synthetic-admin-secret", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_WhenAtomicWriteFails_KeepsPreviousProtectedFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "secrets.dat");
        var goodStore = new DpapiSecretStore(path, new ReversingProtector(), new AtomicFileWriter());
        await goodStore.SetAsync("key", "old-value");
        var before = await File.ReadAllBytesAsync(path);
        var failingStore = new DpapiSecretStore(path, new ReversingProtector(), new ThrowingWriter());

        await Assert.ThrowsAsync<IOException>(() => failingStore.SetAsync("key", "new-value"));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("old-value", await goodStore.GetAsync("key"));
    }

    [Fact]
    public async Task RemoveAsync_ExistingSecret_PersistsRemoval()
    {
        var path = Path.Combine(_directory, "secrets.dat");
        var store = new DpapiSecretStore(path, new ReversingProtector(), new AtomicFileWriter());
        await store.SetAsync("key", "value");

        await store.RemoveAsync("key");

        Assert.Null(await store.GetAsync("key"));
    }

    [Fact]
    public async Task WindowsDpapi_SetAndGetAsync_RoundTripsSyntheticSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_directory, "secrets.dat");
        var store = new DpapiSecretStore(path);

        await store.SetAsync("synthetic/key", "not-a-real-credential");

        Assert.Equal("not-a-real-credential", await store.GetAsync("synthetic/key"));
        Assert.DoesNotContain("not-a-real-credential", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyDpapiSecretUnsealer_CurrentAndRenamedEntropy_ReadSyntheticBlobs()
    {
        if (!OperatingSystem.IsWindows()) return;
        var unsealer = new LegacyDpapiSecretUnsealer();
        foreach (var entropy in new[] { "AIUsageMonitor.providerKeys.v1", "ClaudeUsageMonitor.providerKeys.v1" })
        {
            var plaintext = Encoding.UTF8.GetBytes("synthetic-legacy-secret");
            var protectedBytes = new WindowsDpapiProtector(entropy).Protect(plaintext);
            Array.Clear(plaintext);
            var stored = Convert.ToBase64String(protectedBytes);
            Array.Clear(protectedBytes);

            Assert.True(unsealer.TryUnseal(stored, out var result));
            Assert.Equal("synthetic-legacy-secret", result);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Reverse(plaintext);
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Reverse(protectedData);
        private static byte[] Reverse(ReadOnlySpan<byte> input)
        {
            var result = input.ToArray();
            Array.Reverse(result);
            return result;
        }
    }

    private sealed class ThrowingWriter : IAtomicFileWriter
    {
        public Task WriteAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("synthetic interrupted write"));
    }
}
