using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

/// <summary>R-022: the ESP32 display settings default to off and keep only valid port names.</summary>
public sealed class AppSettingsEspDisplayTests
{
    [Fact]
    public void Defaults_AreOff_AndAutomaticPort()
    {
        var settings = new AppSettings();

        Assert.False(settings.EspDisplayEnabled);
        Assert.Equal("auto", settings.EspDisplayPort);
        Assert.False(settings.Normalize().EspDisplayEnabled);
        Assert.Equal("auto", settings.Normalize().EspDisplayPort);
    }

    [Theory]
    [InlineData("COM5", "COM5")]
    [InlineData("com5", "COM5")]
    [InlineData("  COM12 ", "COM12")]
    [InlineData("COM1", "COM1")]
    [InlineData("COM256", "COM256")]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("COM0", "auto")]
    [InlineData("COM05", "auto")]
    [InlineData("COM257", "auto")]
    [InlineData("COM", "auto")]
    [InlineData("COM-1", "auto")]
    [InlineData("COM5x", "auto")]
    [InlineData("/dev/ttyUSB0", "auto")]
    [InlineData(@"\\.\COM5", "auto")]
    public void NormalizeDisplayPort_KeepsWindowsPortNamesOnly(string? input, string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeDisplayPort(input));
        Assert.Equal(expected, new AppSettings { EspDisplayPort = input! }.Normalize().EspDisplayPort);
    }

    [Fact]
    public void Normalize_KeepsTheEnabledChoice()
    {
        var settings = new AppSettings { EspDisplayEnabled = true, EspDisplayPort = "com7" }.Normalize();

        Assert.True(settings.EspDisplayEnabled);
        Assert.Equal("COM7", settings.EspDisplayPort);
    }
}
