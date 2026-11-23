using AIMonitor.Application.Display;
using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests.Display;

/// <summary>R-022: the feed that keeps the ESP32 desk display supplied over USB serial.</summary>
public sealed class DisplayFeedTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private const string Pong = DisplayFeed.ExpectedPong;

    private readonly FakeClock _clock = new(Start);
    private readonly FakeSerialDisplayPort _port = new();

    private DisplayFeed CreateFeed() => new(_port, _clock);

    private static string FrameAt(DateTimeOffset now) => $"frame@{now.ToUnixTimeSeconds()}";

    [Fact]
    public void Disabled_NeverListsOrOpensAnyPort()
    {
        _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(enabled: false, "auto");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(0, _port.GetPortNamesCalls);
        Assert.Empty(_port.Opened);
        Assert.Equal(DisplayFeedState.Off, feed.State);
    }

    [Fact]
    public void NewFeed_IsOffUntilConfigured()
    {
        _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Empty(_port.Opened);
        Assert.Equal(DisplayFeedState.Off, feed.State);
    }

    [Fact]
    public void Auto_UsesOnlyThePortWhoseDeviceAnswersThePing()
    {
        var silent = _port.Add("COM3");
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Equal("COM5", feed.ConnectedPort);
        Assert.Equal([DisplayFeed.PingLine], silent.Written);
        Assert.Equal(1, silent.Disposals);
        Assert.Equal([FrameAt(Start)], display.Frames);
    }

    [Fact]
    public void Handshake_SkipsBootBannerAndOkLinesBeforeThePong()
    {
        var display = _port.Add("COM5", "hello aimonitor-cyd 1", "ok", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "COM5");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Single(display.Frames);
    }

    [Theory]
    [InlineData("pong aimonitor-cyd 2")]
    [InlineData("pong something-else 1")]
    [InlineData("pong aimonitor-cyd")]
    [InlineData("ok")]
    public void Handshake_RejectsAnyOtherAnswer(string reply)
    {
        var device = _port.Add("COM5", reply);
        using var feed = CreateFeed();
        feed.Configure(true, "COM5");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(DisplayFeedState.Searching, feed.State);
        Assert.Empty(device.Frames);
        Assert.Equal(0, device.OpenConnections);
    }

    [Fact]
    public void ExplicitPort_OpensOnlyThatPort()
    {
        _port.Add("COM3", Pong);
        var display = _port.Add("COM7", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "COM7");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(["COM7"], _port.Opened);
        Assert.Equal(0, _port.GetPortNamesCalls);
        Assert.Single(display.Frames);
    }

    [Fact]
    public void Connected_WithoutAFrameYet_SendsNothingUntilOneIsPublished()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");

        feed.Step();
        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Empty(display.Frames);

        feed.Publish(FrameAt);
        feed.Step();
        Assert.Equal([FrameAt(Start)], display.Frames);
    }

    [Fact]
    public void Heartbeat_ResendsEveryThirtySeconds_RebuiltForTheCurrentInstant()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);
        feed.Step();

        _clock.UtcNow = Start.AddSeconds(29);
        feed.Step();
        Assert.Single(display.Frames);

        _clock.UtcNow = Start.AddSeconds(30);
        feed.Step();
        Assert.Equal([FrameAt(Start), FrameAt(Start.AddSeconds(30))], display.Frames);
    }

    [Fact]
    public void NewFrame_IsSentAtOnce_WithoutWaitingForTheHeartbeat()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);
        feed.Step();

        _clock.UtcNow = Start.AddSeconds(5);
        feed.Publish(now => "second@" + now.ToUnixTimeSeconds());
        feed.Step();

        Assert.Equal([FrameAt(Start), "second@" + Start.AddSeconds(5).ToUnixTimeSeconds()], display.Frames);
    }

    [Fact]
    public void WriteFailure_DropsTheConnection_AndRetriesOnlyAfterTheRetryInterval()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);
        feed.Step();

        display.WriteFailure = new IOException("The device was removed.");
        _clock.UtcNow = Start.AddSeconds(30);
        feed.Step();

        Assert.Equal(DisplayFeedState.Searching, feed.State);
        Assert.Null(feed.ConnectedPort);
        Assert.Equal(0, display.OpenConnections);
        var opensAfterFailure = _port.Opened.Count;

        _clock.UtcNow = Start.AddSeconds(30) + DisplayFeed.RetryInterval - TimeSpan.FromSeconds(1);
        feed.Step();
        Assert.Equal(opensAfterFailure, _port.Opened.Count);

        display.WriteFailure = null;
        var back = Start.AddSeconds(30) + DisplayFeed.RetryInterval;
        _clock.UtcNow = back;
        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Equal(FrameAt(back), display.Frames.Last());
    }

    [Fact]
    public void BusyPort_IsRetriedLater_WithoutThrowing()
    {
        var display = _port.Add("COM5", Pong);
        display.OpenFailure = new UnauthorizedAccessException("Access to the port 'COM5' is denied.");
        using var feed = CreateFeed();
        feed.Configure(true, "COM5");
        feed.Publish(FrameAt);

        feed.Step();
        Assert.Equal(DisplayFeedState.Searching, feed.State);

        display.OpenFailure = null;
        _clock.UtcNow = Start + DisplayFeed.RetryInterval;
        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Equal([FrameAt(Start + DisplayFeed.RetryInterval)], display.Frames);
    }

    [Fact]
    public void NoDisplayAnywhere_StaysSearching_AndDoesNotProbeEveryPass()
    {
        _port.Add("COM3");
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);

        feed.Step();
        feed.Step();
        _clock.UtcNow = Start.AddSeconds(5);
        feed.Step();

        Assert.Equal(DisplayFeedState.Searching, feed.State);
        Assert.Equal(["COM3"], _port.Opened);
    }

    [Fact]
    public void ChangingThePort_ReconnectsAtOnce_EvenDuringTheRetryWait()
    {
        _port.Add("COM3");
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "COM3");
        feed.Publish(FrameAt);
        feed.Step();
        Assert.Equal(DisplayFeedState.Searching, feed.State);

        feed.Configure(true, "COM5");
        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Single(display.Frames);
    }

    [Fact]
    public void ChangingThePort_ClosesTheOldConnection()
    {
        var first = _port.Add("COM5", Pong);
        var second = _port.Add("COM6", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "COM5");
        feed.Publish(FrameAt);
        feed.Step();

        feed.Configure(true, "COM6");
        feed.Step();

        Assert.Equal(0, first.OpenConnections);
        Assert.Equal("COM6", feed.ConnectedPort);
        Assert.Single(second.Frames);
    }

    [Fact]
    public void SwitchingOff_ClosesThePort()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);
        feed.Step();

        feed.Configure(false, "auto");
        feed.Step();

        Assert.Equal(DisplayFeedState.Off, feed.State);
        Assert.Equal(0, display.OpenConnections);
    }

    [Fact]
    public void FrameThatFailsToBuild_IsSkipped_AndTheConnectionKept()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(_ => throw new InvalidOperationException("bug"));

        feed.Step();

        Assert.Equal(DisplayFeedState.Connected, feed.State);
        Assert.Empty(display.Frames);
        Assert.Equal(1, display.OpenConnections);
    }

    [Fact]
    public void ListingPortsFails_StaysSearching_WithoutThrowing()
    {
        using var feed = new DisplayFeed(new ThrowingPort(), _clock);
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(DisplayFeedState.Searching, feed.State);
    }

    [Fact]
    public void Dispose_ClosesTheConnection()
    {
        var display = _port.Add("COM5", Pong);
        var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Publish(FrameAt);
        feed.Step();

        feed.Dispose();

        Assert.Equal(0, display.OpenConnections);
        Assert.Equal(DisplayFeedState.Off, feed.State);
    }

    [Fact]
    public async Task Start_RunsTheLoop_AndSendsAPublishedFrame()
    {
        var display = _port.Add("COM5", Pong);
        var feed = CreateFeed();
        feed.Configure(true, "auto");
        feed.Start();
        feed.Publish(FrameAt);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!display.Frames.Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        feed.Dispose();
        Assert.Contains(FrameAt(Start), display.Frames);
    }

    [Fact]
    public void Configure_BlankPort_MeansAuto()
    {
        var display = _port.Add("COM5", Pong);
        using var feed = CreateFeed();
        feed.Configure(true, "  ");
        feed.Publish(FrameAt);

        feed.Step();

        Assert.Equal(1, _port.GetPortNamesCalls);
        Assert.Single(display.Frames);
    }

    private sealed class ThrowingPort : ISerialDisplayPort
    {
        public IReadOnlyList<string> GetPortNames() => throw new InvalidOperationException("No serial driver.");

        public ISerialDisplayConnection Open(string portName) => throw new InvalidOperationException("No serial driver.");
    }
}
