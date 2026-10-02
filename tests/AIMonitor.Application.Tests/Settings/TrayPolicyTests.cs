using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

public sealed class TrayPolicyTests
{
    [Fact]
    public void ShouldShowTray_WhenShowTrayIconIsTrue_ReturnsTrue()
    {
        var settings = new AppSettings { ShowTrayIcon = true };

        var result = TrayPolicy.ShouldShowTray(settings);

        Assert.True(result);
    }

    [Fact]
    public void ShouldShowTray_WhenShowTrayIconIsFalse_ReturnsFalse()
    {
        var settings = new AppSettings { ShowTrayIcon = false };

        var result = TrayPolicy.ShouldShowTray(settings);

        Assert.False(result);
    }

    [Fact]
    public void ShouldShowTray_NullSettings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TrayPolicy.ShouldShowTray(null!));
    }

    [Fact]
    public void ShouldHideOnClose_WhenMinimizeToTrayAndShowTrayIcon_ReturnsTrue()
    {
        var settings = new AppSettings { MinimizeToTray = true, ShowTrayIcon = true };

        var result = TrayPolicy.ShouldHideOnClose(settings);

        Assert.True(result);
    }

    [Fact]
    public void ShouldHideOnClose_WhenMinimizeToTrayTrue_ButShowTrayIconFalse_ReturnsFalse()
    {
        // Critical requirement: closing must not hide window into background if there is no tray icon to restore it.
        var settings = new AppSettings { MinimizeToTray = true, ShowTrayIcon = false };

        var result = TrayPolicy.ShouldHideOnClose(settings);

        Assert.False(result);
    }

    [Fact]
    public void ShouldHideOnClose_WhenMinimizeToTrayFalse_AndShowTrayIconTrue_ReturnsFalse()
    {
        var settings = new AppSettings { MinimizeToTray = false, ShowTrayIcon = true };

        var result = TrayPolicy.ShouldHideOnClose(settings);

        Assert.False(result);
    }

    [Fact]
    public void ShouldHideOnClose_WhenBothFalse_ReturnsFalse()
    {
        var settings = new AppSettings { MinimizeToTray = false, ShowTrayIcon = false };

        var result = TrayPolicy.ShouldHideOnClose(settings);

        Assert.False(result);
    }

    [Fact]
    public void ShouldHideOnClose_NullSettings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TrayPolicy.ShouldHideOnClose(null!));
    }

    [Fact]
    public void ShouldStartHidden_WhenRequestedAndShowTrayIconIsTrue_ReturnsTrue()
    {
        var settings = new AppSettings { ShowTrayIcon = true };

        var result = TrayPolicy.ShouldStartHidden(settings, startMinimizedRequested: true);

        Assert.True(result);
    }

    [Fact]
    public void ShouldStartHidden_WhenRequested_ButShowTrayIconIsFalse_ReturnsFalse()
    {
        // Critical requirement: application must not start hidden if there is no tray icon to restore it.
        var settings = new AppSettings { ShowTrayIcon = false };

        var result = TrayPolicy.ShouldStartHidden(settings, startMinimizedRequested: true);

        Assert.False(result);
    }

    [Fact]
    public void ShouldStartHidden_WhenNotRequested_AndShowTrayIconIsTrue_ReturnsFalse()
    {
        var settings = new AppSettings { ShowTrayIcon = true };

        var result = TrayPolicy.ShouldStartHidden(settings, startMinimizedRequested: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldStartHidden_WhenNotRequested_AndShowTrayIconIsFalse_ReturnsFalse()
    {
        var settings = new AppSettings { ShowTrayIcon = false };

        var result = TrayPolicy.ShouldStartHidden(settings, startMinimizedRequested: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldStartHidden_NullSettings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TrayPolicy.ShouldStartHidden(null!, startMinimizedRequested: true));
    }
}
