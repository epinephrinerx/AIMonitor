using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class ProviderSnapshotTests
{
    [Fact]
    public void Ok_RequiresConfiguredAndNoError()
    {
        var configuredNoError = new ProviderSnapshot("claude", configured: true);
        var configuredWithError = new ProviderSnapshot("claude", configured: true, error: "boom");
        var notConfigured = new ProviderSnapshot("claude", configured: false);

        Assert.True(configuredNoError.Ok);
        Assert.False(configuredWithError.Ok);
        Assert.False(notConfigured.Ok);
    }

    [Fact]
    public void HistoryError_DoesNotAffectOk()
    {
        var snapshot = new ProviderSnapshot("claude", configured: true, historyError: "history failed");

        Assert.True(snapshot.Ok);
        Assert.Equal("history failed", snapshot.HistoryError);
    }

    [Fact]
    public void Meters_DefaultsToEmpty_NotNull()
    {
        var snapshot = new ProviderSnapshot("claude");

        Assert.Empty(snapshot.Meters);
        Assert.Empty(snapshot.Stats);
    }

    [Fact]
    public void Constructor_RejectsBlankProviderId()
    {
        Assert.Throws<ArgumentException>(() => new ProviderSnapshot(""));
    }

    [Fact]
    public void Meters_MutatingTheSourceListAfterConstruction_DoesNotAlterTheSnapshot()
    {
        var meters = new List<Meter> { new("session", "session", "Session", "5-hour window", 10.0) };
        var snapshot = new ProviderSnapshot("claude", configured: true, meters: meters);

        meters.Add(new Meter("weekly_all", "weekly_all", "Weekly", "All models", 20.0));
        meters.Clear();

        Assert.Single(snapshot.Meters);
        Assert.Equal("session", snapshot.Meters[0].Kind);
    }

    [Fact]
    public void Stats_MutatingTheSourceListAfterConstruction_DoesNotAlterTheSnapshot()
    {
        var stats = new List<Stat> { new("Requests", "42") };
        var snapshot = new ProviderSnapshot("claude", configured: true, stats: stats);

        stats.Clear();

        Assert.Single(snapshot.Stats);
    }

    [Fact]
    public void Meters_ExposedPropertyIsGenuinelyReadOnly()
    {
        var snapshot = new ProviderSnapshot(
            "claude", configured: true, meters: [new Meter("session", "session", "Session", "5-hour window", 10.0)]);

        var asList = Assert.IsAssignableFrom<IList<Meter>>(snapshot.Meters);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(
            () => asList[0] = new Meter("weekly_all", "weekly_all", "Weekly", "All models", 1.0));
        Assert.Throws<NotSupportedException>(
            () => asList.Add(new Meter("weekly_all", "weekly_all", "Weekly", "All models", 1.0)));
        Assert.Equal("session", snapshot.Meters[0].Kind);
    }

    [Fact]
    public void Stats_ExposedPropertyIsGenuinelyReadOnly()
    {
        var snapshot = new ProviderSnapshot("claude", configured: true, stats: [new Stat("Requests", "42")]);

        var asList = Assert.IsAssignableFrom<IList<Stat>>(snapshot.Stats);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList[0] = new Stat("Requests", "0"));
        Assert.Equal("42", snapshot.Stats[0].Value);
    }

    [Fact]
    public void Constructor_RejectsDuplicateMeterKeys()
    {
        var meters = new List<Meter>
        {
            new("weekly_scoped", "weekly_scoped_fable", "Weekly", "Fable only", 5.0),
            new("weekly_scoped", "weekly_scoped_fable", "Weekly", "Fable only", 9.0),
        };

        Assert.Throws<ArgumentException>(() => new ProviderSnapshot("claude", configured: true, meters: meters));
    }
}
