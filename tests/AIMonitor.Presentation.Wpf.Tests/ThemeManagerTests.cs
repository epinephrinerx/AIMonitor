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
}
