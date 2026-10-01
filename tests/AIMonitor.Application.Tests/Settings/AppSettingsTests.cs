using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Normalize_InvalidValues_UsesSafeSupportedValues()
    {
        var result = new AppSettings
        {
            SchemaVersion = 99,
            Theme = "neon",
            DashboardWidth = 1,
            DashboardHeight = 1,
            RefreshIntervalSeconds = -1,
            WidgetOpacity = 0.01,
            ChartRangeDays = 365,
            ChartMetric = "invented",
        }.Normalize();

        Assert.Equal(AppSettings.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Equal("system", result.Theme);
        Assert.Equal(760, result.DashboardWidth);
        Assert.Equal(560, result.DashboardHeight);
        Assert.Equal(30, result.RefreshIntervalSeconds);
        Assert.Equal(0.25, result.WidgetOpacity);
        Assert.Equal(14, result.ChartRangeDays);
        Assert.Equal("Total tokens", result.ChartMetric);
    }
}
