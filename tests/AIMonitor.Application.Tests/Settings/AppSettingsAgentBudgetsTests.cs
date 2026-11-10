using System.Globalization;
using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

/// <summary>
/// R-015 Q1.1 — <see cref="AppSettings.AgentBudgets"/> normalisation, written from Docs/SPEC_R015.md
/// only (§5.6): oracles O6, decisions E4 and E8, Q1.1 rules. The property does not exist until the
/// Q1 branch merges — these tests are expected not to compile before that (handoff R-015/T16 note).
/// </summary>
public sealed class AppSettingsAgentBudgetsTests
{
    // ── E4: ActiveProvider accepts "opencode" ─────────────────────────────────

    [Theory]
    [InlineData("opencode", "opencode")]   // E4 / Q1.1: "opencode" is accepted in addition to the old values
    [InlineData("claude", "claude")]
    [InlineData("openai", "openai")]
    [InlineData("agy", "agy")]
    [InlineData("all-agents", "all-agents")]
    [InlineData("gemini", "agy")]          // pre-existing rule (R-014) must stand
    [InlineData("no-such-tab", "claude")]  // unknown value still falls back to Claude (E4)
    public void Normalize_ActiveProvider_E4_KeepsKnownValuesIncludingOpenCode(string input, string expected)
    {
        Assert.Equal(expected, new AppSettings { ActiveProvider = input }.Normalize().ActiveProvider);
    }

    // ── Q1.1: default and null handling ───────────────────────────────────────

    [Fact]
    public void AgentBudgets_Q11_DefaultIs_Empty_AndSchemaVersionStaysOne()
    {
        var settings = new AppSettings();

        Assert.Empty(settings.AgentBudgets);
        Assert.Equal(1, AppSettings.CurrentSchemaVersion); // Q1.1: additive field — SchemaVersion stays 1
        Assert.Equal(1, settings.Normalize().SchemaVersion);
    }

    [Fact]
    public void Normalize_NullAgentBudgets_Q11_ReturnsEmptyBudgets()
    {
        var normalized = new AppSettings { AgentBudgets = null! }.Normalize();

        Assert.Empty(normalized.AgentBudgets);
    }

    // ── O6: trim + lower-case keys, drop blanks, last duplicate wins, value range, rounding ──

    [Fact]
    public void Normalize_AgentBudgets_O6_TrimsLowersDropsInvalidAndRoundsMidpoints()
    {
        // O6 input: {" OpenCode ": 20.005, "codex": 0, "": 5, "x": 2000000}
        // O6 result: {"opencode": 20.01}
        var budgets = new Dictionary<string, decimal>
        {
            [" OpenCode "] = 20.005m, // kept: trimmed + ToLowerInvariant, rounded away from zero
            ["codex"] = 0m,           // dropped: value <= 0
            [""] = 5m,                // dropped: blank key
            ["x"] = 2_000_000m,       // dropped: value > 1_000_000
        };

        var normalized = new AppSettings { AgentBudgets = budgets }.Normalize();

        var entry = Assert.Single(normalized.AgentBudgets);
        Assert.Equal("opencode", entry.Key);
        Assert.Equal(20.01m, entry.Value);
    }

    [Fact]
    public void Normalize_AgentBudgets_Q11_DuplicateKeysAfterNormalising_LastEntryWins()
    {
        // Q1.1: on duplicate keys after normalising, the LAST entry in enumeration order wins.
        var lastWins = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["Opencode"] = 10m, ["OPENCODE"] = 20m },
        }.Normalize();
        Assert.Equal(20m, lastWins.AgentBudgets["opencode"]);

        var otherOrder = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["OPENCODE"] = 20m, ["Opencode"] = 10m },
        }.Normalize();
        Assert.Equal(10m, otherOrder.AgentBudgets["opencode"]);
    }

    [Theory]
    // Q1.1: values <= 0 or > 1_000_000 dropped; the boundaries stay and others round to 2 decimals.
    // (decimal cannot be an attribute argument — inputs parsed from invariant strings.)
    [InlineData("0", false)]
    [InlineData("-0.01", false)]
    [InlineData("0.01", true)]
    [InlineData("1000000", true)]
    [InlineData("1000000.01", false)]
    public void Normalize_AgentBudgetValue_Q11_DropsOutOfRangeKeepsBoundaries(string value, bool kept)
    {
        var normalized = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = decimal.Parse(value, CultureInfo.InvariantCulture) },
        }.Normalize();

        Assert.Equal(kept, normalized.AgentBudgets.ContainsKey("opencode"));
    }

    [Theory]
    // Q1.1: rounding to 2 decimals with MidpointRounding.AwayFromZero (O6 pins 20.005 → 20.01;
    // these add independent midpoints so a banker's-rounding implementation fails).
    [InlineData("2.675", "2.68")]
    [InlineData("2.685", "2.69")]
    [InlineData("10.005", "10.01")]
    public void Normalize_AgentBudgetValue_Q11_RoundsMidpointsAwayFromZero(string value, string expected)
    {
        var normalized = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = decimal.Parse(value, CultureInfo.InvariantCulture) },
        }.Normalize();

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), normalized.AgentBudgets["opencode"]);
    }

    // ── E8: new dictionary with OrdinalIgnoreCase comparer, always ─────────────

    [Fact]
    public void Normalize_AgentBudgets_E8_ResultLookupsAreCaseInsensitive()
    {
        // E8: the resulting dictionary uses StringComparer.OrdinalIgnoreCase.
        var normalized = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["OpenCode"] = 12.34m },
        }.Normalize();

        Assert.True(normalized.AgentBudgets.ContainsKey("oPeNdOdE"));
        Assert.Equal(12.34m, normalized.AgentBudgets["OPENCODE"]);
    }

    [Fact]
    public void Normalize_AgentBudgets_E8_AlwaysReturnsANewDictionary_NotTheSourceInstance()
    {
        var source = new Dictionary<string, decimal> { ["opencode"] = 20m };

        var normalized = new AppSettings { AgentBudgets = source }.Normalize();
        source["codex"] = 5m; // E8: normalisation must not keep (or alias) the source dictionary

        Assert.False(normalized.AgentBudgets.ContainsKey("codex"));
        Assert.Equal(20m, normalized.AgentBudgets["opencode"]);
    }
}
