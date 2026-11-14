using System.Diagnostics;
using System.Text.Json;
using AIMonitor.Infrastructure.MacOS;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.MacOS;

/// <summary>
/// <c>security</c> is replaced by a recording fake, so these run on any host. The token values are
/// synthetic throughout — no test ever reads the real Keychain.
/// </summary>
public sealed class ClaudeKeychainCredentialReaderTests
{
    private const string Service = "Claude Code-credentials";

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSecurity
    {
        public List<(IReadOnlyList<string> Arguments, TimeSpan Timeout)> Invocations { get; } = [];

        public int ExitCode { get; set; }

        public string Output { get; set; } = string.Empty;

        public Exception? Throws { get; set; }

        public CommandResult Run(ProcessStartInfo startInfo, string? standardInput, TimeSpan timeout)
        {
            Invocations.Add((startInfo.ArgumentList.ToArray(), timeout));
            if (Throws is not null) throw Throws;
            return new CommandResult(ExitCode, Output, string.Empty);
        }
    }

    private static string CredentialJson(DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = "synthetic-not-a-real-token",
                expiresAt = expiresAt.ToUnixTimeMilliseconds(),
                subscriptionType = "max",
            },
        });

    private static ClaudeKeychainCredentialReader Reader(FakeSecurity security) =>
        new(Service, security.Run);

    [Fact]
    public async Task ReadAsync_AsksTheKeychainForClaudesOwnItem()
    {
        var security = new FakeSecurity { Output = CredentialJson(Now.AddHours(1)) };

        await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        var invocation = Assert.Single(security.Invocations);
        Assert.Equal(["find-generic-password", "-s", Service, "-w"], invocation.Arguments);
    }

    [Fact]
    public async Task ReadAsync_AllowsTimeForTheAccessPromptTheUserHasToAnswer()
    {
        var security = new FakeSecurity { Output = CredentialJson(Now.AddHours(1)) };

        await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        // The item's ACL names Claude Code, so the first read raises a dialog. A 15-second default
        // would kill the read while the user is still looking at it.
        Assert.True(
            Assert.Single(security.Invocations).Timeout >= TimeSpan.FromMinutes(1),
            "the read deadline must outlast a human answering a Keychain prompt");
    }

    [MacOSOnlyFact("the reader refuses to speak to a Keychain that is not there")]
    public async Task ReadAsync_WithAValidItem_FindsTheLogin()
    {
        var security = new FakeSecurity { Output = CredentialJson(Now.AddHours(1)) };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Equal("synthetic-not-a-real-token", result.Credentials!.AccessToken);
    }

    [MacOSOnlyFact("the reader refuses to speak to a Keychain that is not there")]
    public async Task ReadAsync_TrimsTheNewlineSecurityAddsOfItsOwn()
    {
        var security = new FakeSecurity { Output = CredentialJson(Now.AddHours(1)) + "\n" };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
    }

    [MacOSOnlyFact("the reader refuses to speak to a Keychain that is not there")]
    public async Task ReadAsync_WithAnExpiredToken_SaysExpiredRatherThanMissing()
    {
        var security = new FakeSecurity { Output = CredentialJson(Now.AddHours(-1)) };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Expired, result.Status);
    }

    [MacOSOnlyFact("the reader refuses to speak to a Keychain that is not there")]
    public async Task ReadAsync_WhenTheItemIsNotJson_SaysInvalid()
    {
        var security = new FakeSecurity { Output = "this is not json" };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(44)]
    [InlineData(51)]
    [InlineData(1)]
    public async Task ReadAsync_WhenSecurityFails_SaysThereIsNoLogin(int exitCode)
    {
        var security = new FakeSecurity { ExitCode = exitCode };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Missing, result.Status);
    }

    [Fact]
    public async Task ReadAsync_WhenSecurityCannotBeRunAtAll_DoesNotThrowAtTheCaller()
    {
        var security = new FakeSecurity { Throws = new TimeoutException("took too long") };

        var result = await Reader(security).ReadAsync("ignored", new FakeClock(Now), CancellationToken.None);

        Assert.True(result.Status is ClaudeCredentialStatus.Invalid or ClaudeCredentialStatus.Missing);
    }

    [Fact]
    public async Task ReadAsync_WhenAlreadyCancelled_TouchesNothing()
    {
        var security = new FakeSecurity();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Reader(security).ReadAsync("ignored", new FakeClock(Now), cancelled.Token));

        Assert.Empty(security.Invocations);
    }

    [Fact]
    public void Source_NamesTheKeychainServiceSoAnErrorCanPointAtIt()
    {
        Assert.Contains(Service, Reader(new FakeSecurity()).Source, StringComparison.Ordinal);
    }
}
