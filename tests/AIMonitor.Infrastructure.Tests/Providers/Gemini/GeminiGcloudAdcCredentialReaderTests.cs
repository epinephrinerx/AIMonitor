using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// A gcloud user login is explained, not silently skipped: <c>gcloud auth application-default login</c>
/// writes an <c>authorized_user</c> credential, which this app cannot use (reading usage with it would
/// mean refreshing another tool's OAuth token). Both the Windows and POSIX application-default
/// locations are scanned before concluding Limited/nothing, so a usable service account at either one
/// is never hidden by an unusable file found at the other. Ports the Python baseline's
/// <c>test_gcloud_adc.py</c> <c>GcloudAdcTests</c> one-for-one onto <see cref="GeminiGcloudAdcCredentialReader"/>.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiGcloudAdcCredentialReaderTests : IDisposable
{
    private const string ServiceAccountJson =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project","private_key":"-----BEGIN PRIVATE KEY----- not-a-real-key"}""";

    private const string IncompleteServiceAccountJson =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project"}""";

    private const string UserLoginJson =
        """{"type":"authorized_user","account":"someone@example.invalid","client_id":"123.apps.googleusercontent.com","refresh_token":"not-read"}""";

    private readonly string _profileDirectory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-gcloud-tests-" + Guid.NewGuid().ToString("N"));

    public GeminiGcloudAdcCredentialReaderTests() => Directory.CreateDirectory(_profileDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_profileDirectory))
        {
            Directory.Delete(_profileDirectory, recursive: true);
        }
    }

    private string WindowsPath => Path.Combine(_profileDirectory, "AppData", "Roaming", "gcloud", "application_default_credentials.json");

    private string PosixPath => Path.Combine(_profileDirectory, ".config", "gcloud", "application_default_credentials.json");

    private void WriteWindows(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WindowsPath)!);
        File.WriteAllText(WindowsPath, json);
    }

    private void WritePosix(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PosixPath)!);
        File.WriteAllText(PosixPath, json);
    }

    private Task<GeminiCredential?> ReadAsync(CancellationToken cancellationToken = default) =>
        GeminiGcloudAdcCredentialReader.ReadAsync(_profileDirectory, cancellationToken);

    [Fact]
    public async Task ReadAsync_ServiceAccountAtWindowsLocation_IsUsable()
    {
        WriteWindows(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
        Assert.Equal("robot@example.iam.gserviceaccount.com", credential.Account);
        Assert.Equal("my-project", credential.Project);
    }

    [Fact]
    public async Task ReadAsync_UserLoginIsReportedAsLimitedNotDropped()
    {
        WriteWindows(UserLoginJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Contains("user login", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReasonTellsTheUserWhatToDoInstead()
    {
        WriteWindows(UserLoginJson);

        var credential = await ReadAsync();

        Assert.Contains("gemini", credential!.LimitedReason, StringComparison.Ordinal);
        Assert.Contains("service-account", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RefreshTokenIsNeverRead()
    {
        WriteWindows(UserLoginJson);

        var credential = await ReadAsync();

        Assert.Equal(string.Empty, credential!.Value);
        Assert.DoesNotContain("not-read", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_PosixLocationIsReadToo()
    {
        WritePosix(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
    }

    [Fact]
    public async Task ReadAsync_UsableAccountWinsOverAUserLoginFoundFirst()
    {
        // Review point from the baseline: the first file read used to end the search, so a user login
        // in the Windows path hid a perfectly good service account sitting in the POSIX one.
        WriteWindows(UserLoginJson);
        WritePosix(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.True(credential!.UsageCapable, "a usable service account was hidden by a user login found first");
        Assert.Equal("robot@example.iam.gserviceaccount.com", credential.Account);
    }

    [Fact]
    public async Task ReadAsync_TwoUserLogins_AreStillJustLimited()
    {
        WriteWindows(UserLoginJson);
        WritePosix("""{"type":"authorized_user","account":"other@example.invalid"}""");

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Equal("someone@example.invalid", credential.Account);
    }

    [Fact]
    public async Task ReadAsync_IncompleteKey_IsLimitedNotConnected()
    {
        WriteWindows(IncompleteServiceAccountJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.False(credential!.UsageCapable);
        Assert.Contains("missing the fields", credential.LimitedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_IncompleteKeyDoesNotHideACompleteOne()
    {
        WriteWindows(IncompleteServiceAccountJson);
        WritePosix(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.True(credential!.UsageCapable, "a usable key was hidden by an unusable one found first");
        Assert.Equal(PosixPath, credential.Value);
    }

    [Fact]
    public async Task ReadAsync_NoFileAnywhere_ReturnsNull()
    {
        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_NonObjectJsonAtOneLocationDoesNotHideAUsableFileAtTheOther()
    {
        WriteWindows("[]");
        WritePosix(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
    }

    [Fact]
    public async Task ReadAsync_CorruptJsonAtOneLocationDoesNotHideAUsableFileAtTheOther()
    {
        WriteWindows("{not-json");
        WritePosix(ServiceAccountJson);

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
    }

    [Fact]
    public async Task ReadAsync_IsReadOnly_NeverModifiesEitherFile()
    {
        WriteWindows(UserLoginJson);
        WritePosix(IncompleteServiceAccountJson);

        await ReadAsync();

        Assert.Equal(UserLoginJson, await File.ReadAllTextAsync(WindowsPath));
        Assert.Equal(IncompleteServiceAccountJson, await File.ReadAllTextAsync(PosixPath));
    }

    [Fact]
    public async Task ReadAsync_AlreadyCancelledToken_ThrowsWithoutFileAccess()
    {
        WriteWindows(ServiceAccountJson);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(cts.Token));
    }

    [Fact]
    public async Task ReadAsync_BlankUserProfileDirectory_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => GeminiGcloudAdcCredentialReader.ReadAsync(" ", CancellationToken.None));
    }
}
