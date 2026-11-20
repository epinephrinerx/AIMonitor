using AIMonitor.Application.Time;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

public sealed class ChainedClaudeCredentialReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubSource(ClaudeCredentialReadResult result) : IClaudeCredentialReader
    {
        public int Calls { get; private set; }

        public Task<ClaudeCredentialReadResult> ReadAsync(string path, IClock clock, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static ClaudeCredentials Token(DateTimeOffset expiresAt) =>
        new("synthetic-not-a-real-token", expiresAt, subscriptionType: "max", rateLimitTier: null);

    private static Task<ClaudeCredentialReadResult> Read(params IClaudeCredentialReader[] sources) =>
        new ChainedClaudeCredentialReader(sources).ReadAsync("path", new FakeClock(Now), CancellationToken.None);

    [Fact]
    public async Task TheFirstSourceWins_WhenItHasALogin()
    {
        var file = new StubSource(ClaudeCredentialReadResult.Found(Token(Now.AddHours(1)), "file"));
        var keychain = new StubSource(ClaudeCredentialReadResult.Found(Token(Now.AddHours(2)), "keychain"));

        var result = await Read(file, keychain);

        Assert.Equal("file", result.Path);
        Assert.Equal(0, keychain.Calls);
    }

    [Fact]
    public async Task AMissingFirstSource_FallsThroughToTheNext()
    {
        var file = new StubSource(ClaudeCredentialReadResult.Missing("file"));
        var keychain = new StubSource(ClaudeCredentialReadResult.Found(Token(Now.AddHours(1)), "keychain"));

        var result = await Read(file, keychain);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Equal("keychain", result.Path);
    }

    [Theory]
    [InlineData(ClaudeCredentialStatus.Invalid)]
    [InlineData(ClaudeCredentialStatus.Expired)]
    public async Task AFirstSourceThatIsBrokenOrStale_StopsTheChain(ClaudeCredentialStatus status)
    {
        var first = status == ClaudeCredentialStatus.Invalid
            ? ClaudeCredentialReadResult.Invalid("file")
            : ClaudeCredentialReadResult.Expired(Token(Now.AddHours(-1)), "file");
        var file = new StubSource(first);
        var keychain = new StubSource(ClaudeCredentialReadResult.Found(Token(Now.AddHours(1)), "keychain"));

        var result = await Read(file, keychain);

        // Reaching past a login that exists but is unusable would hide the very thing the user has
        // to act on.
        Assert.Equal(status, result.Status);
        Assert.Equal(0, keychain.Calls);
    }

    [Fact]
    public async Task WhenNoSourceHasAnything_ReportsTheLastOneSoTheMessagePointsAtThisPlatform()
    {
        var file = new StubSource(ClaudeCredentialReadResult.Missing("file"));
        var keychain = new StubSource(ClaudeCredentialReadResult.Missing("the login Keychain"));

        var result = await Read(file, keychain);

        Assert.Equal(ClaudeCredentialStatus.Missing, result.Status);
        Assert.Equal("the login Keychain", result.Path);
        Assert.Equal(1, keychain.Calls);
    }

    [Fact]
    public void WithoutASource_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ChainedClaudeCredentialReader());
    }

    [Fact]
    public void WithANullSource_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new ChainedClaudeCredentialReader(new ClaudeCredentialFileReader(), null!));
    }
}
