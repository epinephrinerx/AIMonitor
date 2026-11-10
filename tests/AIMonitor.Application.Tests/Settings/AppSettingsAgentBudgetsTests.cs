using System.Globalization;
using AIMonitor.Application.Settings;

namespace AIMonitor.Application.Tests.Settings;

/// <summary>
/// R-015 Q1.1 — <see cref="AppSettings.AgentBudgets"/> normalisation, written from Docs/SPEC_R015.md
/// only (§5.6): oracle O6, decisions E4, E8, E17 (round first, then drop values &lt;= 0 or
/// &gt; 1_000_000, so a stored 0.00 can never appear) and E18 (invalid duplicates are skipped and
/// never remove or replace an entry; among the valid ones the last in enumeration order wins), plus
/// the Q1.1 rules (trim + lower-case keys, drop blanks, OrdinalIgnoreCase result).
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

    // ── E18: duplicate keys after normalising ──────────────────────────────────

    [Fact]
    public void Normalize_AgentBudgets_E18_AmongValidDuplicates_TheLastInEnumerationOrderWins()
    {
        // E18: among the VALID entries the last in enumeration order wins.
        // Oracle: { "opencode": 20, "OpenCode": 30 } → { "opencode": 30 }.
        var lastValidWins = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m, ["OpenCode"] = 30m },
        }.Normalize();

        var entry = Assert.Single(lastValidWins.AgentBudgets);
        Assert.Equal("opencode", entry.Key);
        Assert.Equal(30m, entry.Value);

        // …and it really is the enumeration order that decides, not the case of the key.
        var otherOrder = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["OpenCode"] = 30m, ["opencode"] = 20m },
        }.Normalize();
        Assert.Equal(20m, otherOrder.AgentBudgets["opencode"]);
    }

    [Fact]
    public void Normalize_AgentBudgets_E18_InvalidDuplicateIsSkippedAndNeverRemovesOrReplaces()
    {
        // E18 (replaces the old "last wins for everything" reading of Q1.1): an INVALID value is
        // skipped and never removes or replaces an entry.
        // Oracle: { "opencode": 20, "OpenCode ": 0 } → { "opencode": 20 }.
        var kept = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m, ["OpenCode "] = 0m },
        }.Normalize();

        var entry = Assert.Single(kept.AgentBudgets);
        Assert.Equal("opencode", entry.Key);
        Assert.Equal(20m, entry.Value);

        // A blank key and an out-of-range value must not clear an earlier valid entry either.
        var blankKey = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal>
            {
                ["opencode"] = 20m,
                [" OpenCode"] = -5m,
                ["OPENCODE "] = 2_000_000m,
            },
        }.Normalize();

        var survivor = Assert.Single(blankKey.AgentBudgets);
        Assert.Equal("opencode", survivor.Key);
        Assert.Equal(20m, survivor.Value);
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
    // E17: Normalize rounds FIRST (2 dp, AwayFromZero) and only then drops values <= 0 or > 1_000_000,
    // so a stored 0.00 can never appear. Hand-derived: 0.004 → 0.00 → dropped; 0.001 → 0.00 → dropped;
    // 0.0049 → 0.00 → dropped; 0.005 → 0.01 (midpoint away from zero) → kept; 999_999.996 → 1_000_000.00
    // → kept (it is the rounded value that is compared with the bound, not the raw one).
    [InlineData("0.004", null)]
    [InlineData("0.001", null)]
    [InlineData("0.0049", null)]
    [InlineData("0.005", "0.01")]
    [InlineData("999999.996", "1000000")]
    public void Normalize_AgentBudgetValue_E17_RoundsBeforeTheRangeCheckSoZeroIsNeverStored(
        string value,
        string? expected)
    {
        var normalized = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = decimal.Parse(value, CultureInfo.InvariantCulture) },
        }.Normalize();

        if (expected is null)
        {
            // Oracle: { "opencode": 0.004 } → {} — dropped, and never stored as 0.00.
            Assert.Empty(normalized.AgentBudgets);
        }
        else
        {
            Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), normalized.AgentBudgets["opencode"]);
        }
    }

    [Fact]
    public void Normalize_AgentBudgetValue_E17_StoredValuesAreNeverZeroOrNegative()
    {
        // E17: "so a stored 0.00 can never appear" — sweep the sub-cent band plus zero and negatives.
        foreach (var value in new[] { "0", "-0.004", "0.0001", "0.004", "0.0049" })
        {
            var normalized = new AppSettings
            {
                AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = decimal.Parse(value, CultureInfo.InvariantCulture) },
            }.Normalize();

            Assert.DoesNotContain(normalized.AgentBudgets.Values, static v => v <= 0m);
        }
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

        Assert.True(normalized.AgentBudgets.ContainsKey("OpEnCoDe"));
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
