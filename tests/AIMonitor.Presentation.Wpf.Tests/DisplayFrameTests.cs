using System.Text.Json;
using AIMonitor.Application.Display;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Display;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>R-022: the line sent to the ESP32 desk display (protocol v1).</summary>
public sealed class DisplayFrameTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    private static MeterDisplayItem Window(
        string title,
        string subtitle = "",
        double? percent = 42.0,
        Severity severity = Severity.Normal,
        DateTimeOffset? resetsAt = null,
        string? locked = null) => new()
        {
            Title = title,
            Subtitle = subtitle,
            Value = percent ?? 0.0,
            HasPercent = percent.HasValue,
            Severity = severity,
            ResetsAt = resetsAt,
            LockedReason = locked,
        };

    private static TrayReading Reading(string id, string name, IReadOnlyList<MeterDisplayItem> windows, bool stale = false, string detail = "") =>
        new(id, name, "XX", 0.0, Severity.Normal, detail, windows.Count > 0) { Windows = windows, IsStale = stale };

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public void Frame_CarriesVersionClockAndOffset()
    {
        var root = Parse(DisplayFrame.Build([], Now, Bangkok));

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(Now.ToUnixTimeSeconds(), root.GetProperty("now").GetInt64());
        Assert.Equal(420, root.GetProperty("tz").GetInt32());
        Assert.Equal(0, root.GetProperty("p").GetArrayLength());
    }

    [Fact]
    public void Frame_IsOneLine()
    {
        var line = DisplayFrame.Build([Reading("claude", "Claude", [Window("Session", "5-hour window")])], Now, Bangkok);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void Window_CarriesPercentSeverityResetAndLock()
    {
        var reset = Now.AddHours(2);
        var reading = Reading("claude", "Claude",
        [
            Window("Session", "5-hour window", 81.26, Severity.High, reset),
            Window("Weekly", "Fable only", 100, Severity.Critical, locked: "limit reached"),
        ]);

        var provider = Parse(DisplayFrame.Build([reading], Now, Bangkok)).GetProperty("p")[0];
        var windows = provider.GetProperty("w");

        Assert.Equal("claude", provider.GetProperty("id").GetString());
        Assert.Equal("Claude", provider.GetProperty("name").GetString());
        Assert.False(provider.GetProperty("stale").GetBoolean());
        Assert.Equal("", provider.GetProperty("err").GetString());

        Assert.Equal("Session", windows[0].GetProperty("t").GetString());
        Assert.Equal(81.3, windows[0].GetProperty("pct").GetDouble());
        Assert.Equal(1, windows[0].GetProperty("sev").GetInt32());
        Assert.Equal(reset.ToUnixTimeSeconds(), windows[0].GetProperty("reset").GetInt64());
        Assert.Equal("", windows[0].GetProperty("lock").GetString());

        Assert.Equal("Weekly Fable", windows[1].GetProperty("t").GetString());
        Assert.Equal(3, windows[1].GetProperty("sev").GetInt32());
        Assert.Equal(0, windows[1].GetProperty("reset").GetInt64());
        Assert.Equal("limit reached", windows[1].GetProperty("lock").GetString());
    }

    [Fact]
    public void WindowWithoutPercent_SendsNull_NotZero()
    {
        var reading = Reading("openai", "OpenAI / Codex", [Window("Weekly", "Codex quota", percent: null)]);

        var window = Parse(DisplayFrame.Build([reading], Now, Bangkok)).GetProperty("p")[0].GetProperty("w")[0];

        Assert.Equal(JsonValueKind.Null, window.GetProperty("pct").ValueKind);
    }

    [Fact]
    public void StaleService_IsMarked()
    {
        var reading = Reading("openai", "OpenAI / Codex", [Window("Session", "5-hour window")], stale: true);

        var provider = Parse(DisplayFrame.Build([reading], Now, Bangkok)).GetProperty("p")[0];

        Assert.True(provider.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public void ServiceWithoutWindows_SaysWhy()
    {
        var reading = Reading("agy", "Gemini / AGY", [], detail: "Not connected");

        var provider = Parse(DisplayFrame.Build([reading], Now, Bangkok)).GetProperty("p")[0];

        Assert.Equal("Not connected", provider.GetProperty("err").GetString());
        Assert.Equal(0, provider.GetProperty("w").GetArrayLength());
    }

    [Theory]
    // Claude (ClaudeQuotaParser)
    [InlineData("Session", "5-hour window", "Session")]
    [InlineData("Weekly", "All models", "Weekly")]
    [InlineData("Weekly", "Fable only", "Weekly Fable")]
    [InlineData("Weekly", "Opus only", "Weekly Opus")]
    [InlineData("Weekly", "API apps", "Weekly API apps")]
    // Codex (CodexQuotaMapper)
    [InlineData("Session", "Codex quota", "Session")]
    [InlineData("Weekly", "168-hour window", "Weekly")]
    [InlineData("GPT-5 · Session", "5-hour window", "GPT-5 Session")]
    // AGY (AgyUsageMapper)
    [InlineData("Gemini · Session", "5-hour window", "Gemini Session")]
    [InlineData("Claude / GPT · Weekly", "Weekly window", "Claude / GPT Weekly")]
    // No subtitle
    [InlineData("Monthly spend", "", "Monthly spend")]
    public void Label_TellsWindowsApart(string title, string subtitle, string expected)
    {
        Assert.Equal(expected, DisplayFrame.Label(title, subtitle));
    }

    [Fact]
    public void ClaudeWeeklyWindows_GetDistinctLabels()
    {
        var labels = new[]
        {
            DisplayFrame.Label("Weekly", "All models"),
            DisplayFrame.Label("Weekly", "Fable only"),
            DisplayFrame.Label("Weekly", "Opus only"),
        };

        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    [Fact]
    public void Label_IsCutToTheFirmwareBuffer()
    {
        var label = DisplayFrame.Label("Weekly", "Some very long model name only");

        Assert.Equal(DisplayFrame.MaxLabelLength, label.Length);
        Assert.Equal("Weekly Some very long m", label);
    }

    [Theory]
    [InlineData("Gemini · Session", "Gemini - Session")]
    [InlineData("limit — reached", "limit - reached")]
    [InlineData("it’s “done”…", "it's \"done\".")]
    [InlineData("ไทย", "???")]
    [InlineData("tab\there", "tab here")]
    public void Fit_MapsToAscii(string input, string expected)
    {
        Assert.Equal(expected, DisplayFrame.Fit(input, 40));
    }

    [Fact]
    public void Frame_IsPlainAscii_WhateverTheServiceNames()
    {
        var reading = Reading("claude", "Claude ✓ ไทย", [Window("Weekly · ทดสอบ", "Fable only", locked: "ล็อก")]);

        var line = DisplayFrame.Build([reading], Now, Bangkok);

        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Frame_IsCappedAtSixServicesAndEightWindows()
    {
        var windows = Enumerable.Range(0, 12).Select(i => Window("W" + i)).ToList();
        var readings = Enumerable.Range(0, 9).Select(i => Reading("p" + i, "P" + i, windows)).ToList();

        var providers = Parse(DisplayFrame.Build(readings, Now, Bangkok)).GetProperty("p");

        Assert.Equal(DisplayFrame.MaxProviders, providers.GetArrayLength());
        Assert.Equal("p5", providers[5].GetProperty("id").GetString());
        Assert.All(providers.EnumerateArray(), p => Assert.Equal(DisplayFrame.MaxWindows, p.GetProperty("w").GetArrayLength()));
        Assert.Equal("W7", providers[0].GetProperty("w")[7].GetProperty("t").GetString());
    }

    [Fact]
    public void OversizedFrame_DropsLaterWindows_UntilItFitsTheFirmwareLine()
    {
        var longName = new string('x', 60);
        var windows = Enumerable.Range(0, 12)
            .Select(i => Window(longName, longName + " only", 99.99, Severity.Critical, Now.AddDays(6), longName))
            .ToList();
        var readings = Enumerable.Range(0, 9)
            .Select(i => Reading(longName + i, longName, windows, detail: longName))
            .ToList();

        var line = DisplayFrame.Build(readings, Now, Bangkok);
        var providers = Parse(line).GetProperty("p");

        // The firmware reads lines of at most 4096 bytes including the newline.
        Assert.True(line.Length <= DisplayFrame.MaxLineLength, $"frame is {line.Length} bytes");
        Assert.Equal(DisplayFrame.MaxProviders, providers.GetArrayLength());
        var kept = providers[0].GetProperty("w").GetArrayLength();
        Assert.InRange(kept, 1, DisplayFrame.MaxWindows - 1);
        Assert.All(providers.EnumerateArray(), p =>
        {
            Assert.Equal(kept, p.GetProperty("w").GetArrayLength());
            Assert.True(p.GetProperty("id").GetString()!.Length <= DisplayFrame.MaxIdLength);
            Assert.True(p.GetProperty("name").GetString()!.Length <= DisplayFrame.MaxNameLength);
        });
    }

    [Fact]
    public void NegativeTimeZone_IsCarriedInMinutes()
    {
        var root = Parse(DisplayFrame.Build([], Now, TimeSpan.FromMinutes(-210)));

        Assert.Equal(-210, root.GetProperty("tz").GetInt32());
    }

    [Fact]
    public void Sink_PublishesToTheFeed_AndRebuildsTheClockOnEveryHeartbeat()
    {
        var clock = new FakeClock(Now);
        var port = new FakeSerialDisplayPort();
        var device = port.Add("COM5", DisplayFeed.ExpectedPong);
        using var feed = new DisplayFeed(port, clock);
        feed.Configure(true, "auto");
        var sink = new DisplayReadingsSink(feed, _ => Bangkok);

        sink.UpdateReadings([Reading("claude", "Claude", [Window("Session", "5-hour window")])]);
        feed.Step();
        clock.UtcNow = Now + DisplayFeed.HeartbeatInterval;
        feed.Step();

        var frames = device.Frames.Select(Parse).ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal(Now.ToUnixTimeSeconds(), frames[0].GetProperty("now").GetInt64());
        Assert.Equal((Now + DisplayFeed.HeartbeatInterval).ToUnixTimeSeconds(), frames[1].GetProperty("now").GetInt64());
        Assert.Equal("Session", frames[1].GetProperty("p")[0].GetProperty("w")[0].GetProperty("t").GetString());
    }

    [Fact]
    public void Sink_CopiesTheReadings_SoLaterChangesToTheSourceListDoNotLeak()
    {
        var clock = new FakeClock(Now);
        var port = new FakeSerialDisplayPort();
        var device = port.Add("COM5", DisplayFeed.ExpectedPong);
        using var feed = new DisplayFeed(port, clock);
        feed.Configure(true, "auto");
        var sink = new DisplayReadingsSink(feed, _ => Bangkok);
        var source = new List<TrayReading> { Reading("claude", "Claude", [Window("Session")]) };

        sink.UpdateReadings(source);
        source.Clear();
        feed.Step();

        Assert.Equal(1, Parse(device.Frames.Single()).GetProperty("p").GetArrayLength());
    }
}
