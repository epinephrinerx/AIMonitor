using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// Covers the file pointed at directly by a caller - the path saved in this app's settings (PAR-011
/// "manual") or the <c>GOOGLE_APPLICATION_CREDENTIALS</c> path (PAR-011 "env") - which
/// <see cref="GeminiCredentialResolver"/> feeds through the same reader for both candidates. Mirrors
/// the Python baseline's <c>SavedKeyTests</c>/<c>ServiceAccountEnvTests</c>/<c>JsonThatIsNotAnObjectTests</c>.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiServiceAccountCredentialReaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-sa-tests-" + Guid.NewGuid().ToString("N"));

    public GeminiServiceAccountCredentialReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteKey(string json)
    {
        var path = Path.Combine(_directory, "key.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string CompleteKey =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project","private_key":"-----BEGIN PRIVATE KEY----- not-a-real-key"}""";

    private const string IncompleteKey =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project"}""";

    private const string UserLogin =
        """{"type":"authorized_user","account":"someone@example.invalid","client_id":"123.apps.googleusercontent.com","refresh_token":"not-read"}""";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsync_BlankPath_ReturnsNull(string? path)
    {
        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNullNotLimited()
    {
        // Not Limited: there is no credential here to explain (matches the baseline's
        // `test_a_missing_file_is_nothing_at_all`).
        var path = Path.Combine(_directory, "nope.json");

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_CompleteKey_IsUsableWithPathAsValue()
    {
        var path = WriteKey(CompleteKey);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
        Assert.Equal(path, credential.Value);
        Assert.Equal("robot@example.iam.gserviceaccount.com", credential.Account);
        Assert.Equal("my-project", credential.Project);
        Assert.Equal(GeminiCredentialKind.ServiceAccount, credential.Kind);
    }

    [Fact]
    public async Task ReadAsync_IncompleteKey_IsLimitedAndWithholdsThePath()
    {
        var path = WriteKey(IncompleteKey);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Equal(string.Empty, credential.Value);
        Assert.Contains("missing the fields", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WrongKindOfJson_IsLimitedNotDropped()
    {
        var path = WriteKey(UserLogin);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
    }

    [Fact]
    public async Task ReadAsync_FileThatIsNotJson_SaysSoRatherThanTheGenericIncompleteReason()
    {
        var path = WriteKey("not json at all");

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Contains("JSON", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task ReadAsync_NonObjectJson_IsLimitedRatherThanThrowing(string json)
    {
        var path = WriteKey(json);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Equal(string.Empty, credential.Value);
    }

    [Fact]
    public async Task ReadAsync_UnusableKeyNeverReachesTheSigner()
    {
        // The path is withheld, so nothing downstream can try to use it.
        var path = WriteKey(IncompleteKey);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(string.Empty, credential!.Value);
    }

    [Fact]
    public async Task ReadAsync_NonUtf8Bytes_IsLimitedWithCouldNotReadAsJsonReason()
    {
        var path = Path.Combine(_directory, "key.json");
        await File.WriteAllBytesAsync(path, [0xFF, 0xFE, 0x00, 0x01]);

        var credential = await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Equal(GeminiServiceAccountCredentialReader.CouldNotReadAsJsonReason, credential.LimitedReason);
    }

    [Fact]
    public async Task ReadAsync_IsReadOnly_NeverModifiesTheFile()
    {
        var path = WriteKey(IncompleteKey);

        await GeminiServiceAccountCredentialReader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(IncompleteKey, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadAsync_AlreadyCancelledToken_ThrowsWithoutFileAccess()
    {
        var path = WriteKey(CompleteKey);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GeminiServiceAccountCredentialReader.ReadAsync(path, cts.Token));
    }
}
