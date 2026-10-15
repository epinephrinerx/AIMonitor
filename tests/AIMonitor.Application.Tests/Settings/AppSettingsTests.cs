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

    [Fact]
    public void Normalize_ActiveProviderGemini_NormalizesToAgy()
    {
        var settings = new AppSettings
        {
            ActiveProvider = "gemini"
        }.Normalize();

        Assert.Equal("agy", settings.ActiveProvider);
    }

    [Fact]
    public void Normalize_CopiesGeminiEnabledToAgy_WhenAgyMissing_PreservesGemini()
    {
        var settings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["gemini"] = new ProviderPreference(Enabled: false, Extra: "gcp-project-123")
            }
        }.Normalize();

        Assert.True(settings.Providers.ContainsKey("agy"));
        Assert.False(settings.Providers["agy"].Enabled);
        Assert.Equal(string.Empty, settings.Providers["agy"].Extra);

        Assert.True(settings.Providers.ContainsKey("gemini"));
        Assert.False(settings.Providers["gemini"].Enabled);
        Assert.Equal("gcp-project-123", settings.Providers["gemini"].Extra);
    }

    [Fact]
    public void Normalize_ExplicitAgyWins_WhenBothPresent()
    {
        var settings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["gemini"] = new ProviderPreference(Enabled: false, Extra: "gcp-project"),
                ["agy"] = new ProviderPreference(Enabled: true, Extra: "")
            }
        }.Normalize();

        Assert.True(settings.Providers["agy"].Enabled);
        Assert.False(settings.Providers["gemini"].Enabled);
    }
}
