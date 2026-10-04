using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.Tests;

public class ThemeManagerTests
{
    [Fact]
    public void ApplyTheme_Light_SetsIsDarkFalse()
    {
        var manager = new ThemeManager();

        manager.ApplyTheme("light");

        Assert.False(manager.IsDark);
        Assert.Equal("light", manager.CurrentThemePreference);
    }

    [Fact]
    public void ApplyTheme_Dark_SetsIsDarkTrue()
    {
        var manager = new ThemeManager();

        manager.ApplyTheme("dark");

        Assert.True(manager.IsDark);
        Assert.Equal("dark", manager.CurrentThemePreference);
    }

    [Fact]
    public void ApplyTheme_System_SetsPreferenceToSystem()
    {
        var manager = new ThemeManager();

        manager.ApplyTheme("system");

        Assert.Equal("system", manager.CurrentThemePreference);
    }

    [Fact]
    public void ApplyTheme_FiresThemeChangedEvent()
    {
        var manager = new ThemeManager();
        var eventFired = false;
        manager.ThemeChanged += () => eventFired = true;

        manager.ApplyTheme("dark");

        Assert.True(eventFired);
    }

    [Theory]
    [InlineData(false, 0xf9, 0xf9, 0xf7, 0x2a, 0x78, 0xd6)]
    [InlineData(true, 0x0d, 0x0d, 0x0d, 0x39, 0x87, 0xe5)]
    public void Palette_MatchesLegacyPlaneAndAccent(bool dark, int pr, int pg, int pb, int ar, int ag, int ab)
    {
        var palette = ThemePalette.For(dark);

        Assert.Equal(System.Windows.Media.Color.FromRgb((byte)pr, (byte)pg, (byte)pb), palette.Plane);
        Assert.Equal(System.Windows.Media.Color.FromRgb((byte)ar, (byte)ag, (byte)ab), palette.Accent);
        Assert.Equal(dark, palette.IsDark);
    }

    [Theory]
    [InlineData(Severity.Normal, 0x0c, 0xa3, 0x0c)]
    [InlineData(Severity.High, 0xfa, 0xb2, 0x19)]
    [InlineData(Severity.VeryHigh, 0xec, 0x83, 0x5a)]
    [InlineData(Severity.Critical, 0xd0, 0x3b, 0x3b)]
    public void StatusFor_UsesFourDistinctTrafficLightColours(Severity severity, int r, int g, int b)
    {
        Assert.Equal(System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b), ThemePalette.StatusFor(severity));
    }

    [Fact]
    public void Series_HasEightSlotsAndFoldsPastTheEnd()
    {
        var palette = ThemePalette.Light;

        Assert.Equal(8, palette.Categorical.Count);
        Assert.Equal(palette.Categorical[7], palette.Series(20));
        Assert.Equal(palette.Categorical[0], palette.Series(-1));
    }

    [Fact]
    public void SeverityBrush_WithoutApplication_FallsBackToStatusColour()
    {
        var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(ThemeManager.SeverityBrush(Severity.VeryHigh));

        Assert.Equal(ThemePalette.StatusSerious, brush.Color);
    }
}
