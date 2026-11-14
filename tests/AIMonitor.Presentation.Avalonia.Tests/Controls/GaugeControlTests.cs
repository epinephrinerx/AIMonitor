using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.Tests.Controls;

/// <summary>
/// The rules that keep a gauge's name inside its ring: a long name such as
/// "Gemini 4 Argon · Weekly" used to run across the arc.
/// </summary>
public sealed class GaugeControlTests
{
    [Fact]
    public void TheTitleSpanStaysInsideTheRing()
    {
        const double radius = 60;
        const double thickness = 9.6;

        var span = GaugeControl.TitleSpan(radius, thickness);

        Assert.InRange(span, 1, (2 * radius) - thickness);
    }

    [Fact]
    public void ATinyGaugeHasNoRoomRatherThanANegativeOne()
    {
        Assert.Equal(0, GaugeControl.TitleSpan(2, 4));
    }

    [Fact]
    public void ALabelThatFitsKeepsItsSize()
    {
        Assert.Equal(14, GaugeControl.FittedSize(14, naturalWidth: 80, available: 90));
    }

    [Fact]
    public void ALabelSlightlyTooWideIsShrunkToFit()
    {
        Assert.Equal(13, GaugeControl.FittedSize(14, naturalWidth: 98, available: 91), precision: 6);
    }

    [Fact]
    public void ALabelFarTooWideIsShrunkOnlySoFarAndThenWraps()
    {
        Assert.Equal(14 * GaugeControl.MinimumScale, GaugeControl.FittedSize(14, naturalWidth: 160, available: 90));
    }
}
