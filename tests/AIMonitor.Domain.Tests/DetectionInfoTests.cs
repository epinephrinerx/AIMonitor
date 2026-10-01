using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class DetectionInfoTests
{
    [Fact]
    public void Defaults_AreNotConnectedAndEmpty()
    {
        var detection = new DetectionInfo("claude");

        Assert.Equal(DetectionState.NotConnected, detection.State);
        Assert.False(detection.Usable);
        Assert.Empty(detection.Candidates);
    }

    [Theory]
    [InlineData(DetectionState.Connected, true)]
    [InlineData(DetectionState.Limited, false)]
    [InlineData(DetectionState.Expired, false)]
    [InlineData(DetectionState.NotConnected, false)]
    public void Usable_IsTrueOnlyWhenConnected(DetectionState state, bool expected)
    {
        var detection = new DetectionInfo("claude", state);

        Assert.Equal(expected, detection.Usable);
    }

    [Fact]
    public void Constructor_RejectsBlankProviderId()
    {
        Assert.Throws<ArgumentException>(() => new DetectionInfo(""));
    }

    [Fact]
    public void Constructor_RejectsUndefinedState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DetectionInfo("claude", (DetectionState)999));
    }

    [Fact]
    public void Candidates_MutatingTheSourceListAfterConstruction_DoesNotAlterTheDetection()
    {
        var candidates = new List<DetectionCandidate> { new("claude_code", "Claude Code login") };
        var detection = new DetectionInfo("claude", candidates: candidates);

        candidates.Clear();

        Assert.Single(detection.Candidates);
    }

    [Fact]
    public void Candidates_ExposedPropertyIsGenuinelyReadOnly()
    {
        var detection = new DetectionInfo(
            "claude", candidates: [new DetectionCandidate("claude_code", "Claude Code login")]);

        var asList = Assert.IsAssignableFrom<IList<DetectionCandidate>>(detection.Candidates);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Add(new DetectionCandidate("other", "Other")));
        Assert.Single(detection.Candidates);
    }

    [Fact]
    public void Candidate_Constructor_RejectsBlankSourceId()
    {
        Assert.Throws<ArgumentException>(() => new DetectionCandidate("", "label"));
    }

    [Fact]
    public void HintAndAccount_CarryTheReasonAndIdentity()
    {
        var detection = new DetectionInfo(
            "claude",
            DetectionState.Expired,
            sourceId: "claude_code",
            sourceLabel: "Claude Code login",
            account: "someone@example.invalid",
            hint: "Sign in again to refresh.");

        Assert.Equal("someone@example.invalid", detection.Account);
        Assert.Equal("Sign in again to refresh.", detection.Hint);
        Assert.Equal("claude_code", detection.SourceId);
        Assert.Equal("Claude Code login", detection.SourceLabel);
    }
}
