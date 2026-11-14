using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

/// <summary>
/// R-016 H1.2 — "AppSettings.ActiveProvider accepts 'overview' so a restart restores the selected
/// tab", written from Docs/SPEC_R016.md only (§5.6). The tab-restore wiring on top of this value is
/// MainWindowViewModelOverviewTabTests; here the normalisation rule itself: "overview" is kept like
/// the other tab ids, unknown values still fall back to Claude, and the pre-existing values
/// (including R-015 E4's "opencode" and R-014's gemini→agy migration) must keep working.
/// </summary>
public sealed class AppSettingsOverviewTabTests
{
    // ── H1.2: ActiveProvider accepts "overview" ────────────────────────────────

    [Theory]
    [InlineData("overview", "overview")] // H1.2: "overview" is accepted in addition to the old values
    [InlineData("opencode", "opencode")] // E4 (R-015) must stand
    [InlineData("claude", "claude")]
    [InlineData("openai", "openai")]
    [InlineData("agy", "agy")]
    [InlineData("all-agents", "all-agents")]
    [InlineData("gemini", "agy")] // pre-existing rule (R-014) must stand
    [InlineData("no-such-tab", "claude")] // unknown value still falls back to Claude (E4)
    public void Normalize_ActiveProvider_H12_KeepsKnownValuesIncludingOverview(string input, string expected)
    {
        Assert.Equal(expected, new AppSettings { ActiveProvider = input }.Normalize().ActiveProvider);
    }

    [Fact]
    public void Normalize_ActiveProviderOverview_AdditiveChangeSchemaVersionStaysOne()
    {
        // Accepting a new id must not perturb the settings schema (same guard as R-015 Q1.1).
        var normalized = new AppSettings { ActiveProvider = "overview" }.Normalize();

        Assert.Equal("overview", normalized.ActiveProvider);
        Assert.Equal(1, AppSettings.CurrentSchemaVersion);
        Assert.Equal(1, normalized.SchemaVersion);
    }
}
