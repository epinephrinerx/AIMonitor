using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

public class ClaudeDetectionTests
{
    private static ClaudeCredentials Credentials(string? subscriptionType = "pro") =>
        new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1), subscriptionType, "default_claude_pro");

    [Fact]
    public void Detect_Found_MapsToConnectedWithFixedSourceIdentity()
    {
        var result = ClaudeCredentialReadResult.Found(Credentials(), "C:\\fake\\.credentials.json");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal("claude", detection.ProviderId);
        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("claude_code", detection.SourceId);
        Assert.Equal("Claude Code login", detection.SourceLabel);
        Assert.Equal("Pro plan", detection.Account);
        Assert.Equal(string.Empty, detection.Hint);
        Assert.Single(detection.Candidates);
        Assert.True(detection.Usable);
    }

    [Fact]
    public void Detect_FoundWithNoSubscriptionType_LeavesAccountEmpty()
    {
        var result = ClaudeCredentialReadResult.Found(Credentials(subscriptionType: null), "path");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(string.Empty, detection.Account);
    }

    [Fact]
    public void Detect_UnknownSubscriptionType_LeavesAccountEmptyRatherThanSurfacingArbitraryMetadata()
    {
        var result = ClaudeCredentialReadResult.Found(Credentials(subscriptionType: "some-future-plan"), "path");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(string.Empty, detection.Account);
    }

    [Fact]
    public void Detect_SubscriptionTypeDuplicatingTheAccessToken_NeverSurfacesInAccount()
    {
        const string secretLikeValue = "SENTINEL_ACCESS_TOKEN_UNSAFE_7f2a";
        var result = ClaudeCredentialReadResult.Found(Credentials(subscriptionType: secretLikeValue), "path");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(string.Empty, detection.Account);
        Assert.DoesNotContain(secretLikeValue, detection.Account, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_Expired_MapsToExpiredWithRefreshHint()
    {
        var result = ClaudeCredentialReadResult.Expired(Credentials(), "C:\\fake\\.credentials.json");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(DetectionState.Expired, detection.State);
        Assert.Equal("Run `claude` in a terminal to refresh the login.", detection.Hint);
        Assert.False(detection.Usable);
        Assert.Single(detection.Candidates);
    }

    [Fact]
    public void Detect_Missing_MapsToNotConnectedWithNoCandidates()
    {
        var result = ClaudeCredentialReadResult.Missing("C:\\fake\\.credentials.json");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(DetectionState.NotConnected, detection.State);
        Assert.Empty(detection.Candidates);
        Assert.False(detection.Usable);
    }

    [Fact]
    public void Detect_Invalid_MapsToNotConnectedWithNoCandidates()
    {
        var result = ClaudeCredentialReadResult.Invalid("C:\\fake\\.credentials.json");

        var detection = ClaudeDetection.Detect(result);

        Assert.Equal(DetectionState.NotConnected, detection.State);
        Assert.Empty(detection.Candidates);
    }
}
