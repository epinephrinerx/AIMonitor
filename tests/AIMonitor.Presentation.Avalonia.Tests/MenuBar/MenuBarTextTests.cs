using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.MenuBar;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.MenuBar;

public sealed class MenuBarTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 45, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static MeterViewModel Meter(
        double? percent = 12,
        string title = "Session",
        string subtitle = "5-hour window",
        Severity severity = Severity.Normal,
        DateTimeOffset? resetsAt = null,
        string detail = "",
        string? lockedReason = null) =>
        new(new Meter(
            kind: "session",
            key: "session",
            title: title,
            subtitle: subtitle,
            percent: percent,
            serverSeverity: severity,
            resetsAt: resetsAt,
            detail: detail,
            lockedReason: lockedReason));

    private static MenuBarReading Reading(MeterViewModel? icon, bool stale = false, string name = "Claude") =>
        new("claude", name, icon, icon is null ? [] : [icon], stale);

    [Fact]
    public void QuotaLine_ShowsTheWindowItsValueAndWhenItResets()
    {
        var line = MenuBarText.QuotaLine(Meter(resetsAt: Now.AddHours(2).AddMinutes(5)), Now, Utc);

        Assert.StartsWith("Session · 5-hour window — 12% · resets in ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaLine_WithoutASubtitle_DoesNotLeaveADanglingSeparator()
    {
        var line = MenuBarText.QuotaLine(Meter(subtitle: ""), Now, Utc);

        Assert.StartsWith("Session — 12%", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" ·  ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaLine_WithoutAPercentage_SaysWhatTheServiceDidReport_NotAnInventedNumber()
    {
        var line = MenuBarText.QuotaLine(Meter(percent: null, detail: "128k tokens"), Now, Utc);

        Assert.Contains("— 128k tokens ·", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0%", line, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaLine_WithNeitherPercentageNorDetail_ShowsADash()
    {
        Assert.Contains("— — ·", MenuBarText.QuotaLine(Meter(percent: null), Now, Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaLine_WithNoResetScheduled_SaysSo()
    {
        Assert.EndsWith("no reset scheduled", MenuBarText.QuotaLine(Meter(), Now, Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaLine_WhenTheWindowIsLocked_GivesTheReasonInsteadOfAReset()
    {
        var line = MenuBarText.QuotaLine(Meter(lockedReason: "Upgrade to unlock"), Now, Utc);

        Assert.EndsWith("Upgrade to unlock", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_IsJustTheServiceName_WhenTheReadingIsCurrent()
    {
        Assert.Equal("Claude", MenuBarText.Header(Reading(Meter())));
    }

    [Fact]
    public void Header_SaysWhenTheReadingIsLeftOverFromAFailedRefresh()
    {
        Assert.Equal("Claude  (last refresh failed)", MenuBarText.Header(Reading(Meter(), stale: true)));
    }

    [Fact]
    public void Tooltip_WithNoReadingAtAll_SaysThereIsNoneYet()
    {
        Assert.Equal("AI Usage Monitor — no quota data yet", MenuBarText.Tooltip(Reading(null), 0, 1, Now, Utc));
    }

    [Fact]
    public void Tooltip_NamesTheServiceTheWindowAndTheSeverityInWords()
    {
        var tooltip = MenuBarText.Tooltip(Reading(Meter(percent: 91, severity: Severity.Critical)), 0, 1, Now, Utc);
        var lines = tooltip.Split('\n');

        Assert.Equal("Claude — Session (5-hour window)", lines[0]);
        // Spelled out, not just coloured: the icon's colour is never the only cue.
        Assert.Equal($"91% · {SeverityLabels.Describe(Severity.Critical).Label}", lines[1]);
    }

    [Fact]
    public void Tooltip_SaysWhichServiceOfHowMany_OnlyWhenThereIsMoreThanOne()
    {
        var one = MenuBarText.Tooltip(Reading(Meter()), 0, 1, Now, Utc);
        var many = MenuBarText.Tooltip(Reading(Meter()), 1, 3, Now, Utc);

        Assert.DoesNotContain("of 1 services", one, StringComparison.Ordinal);
        Assert.Contains("2 of 3 services", many, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_SaysWhenItIsShowingAPreviousReading()
    {
        var tooltip = MenuBarText.Tooltip(Reading(Meter(), stale: true), 0, 1, Now, Utc);

        Assert.Contains("last refresh failed — showing the previous reading", tooltip, StringComparison.Ordinal);
    }
}
