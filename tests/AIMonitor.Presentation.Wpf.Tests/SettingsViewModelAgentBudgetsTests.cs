using System.Globalization;
using System.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q1.3 — monthly budget rows in <see cref="SettingsViewModel"/>, written from
/// Docs/SPEC_R015.md (§5.6): Q1.3 (one row per agent, parsing rules, error text, Save writes only
/// valid non-empty rows, discard restores) and decisions E5 (live preview), E6 (HasBudgetError gates
/// Save), E9 (known agents ∪ saved keys ∪ opencode), E13 (emptied row removes the key, never a 0
/// entry), E14 (opencode first, the rest sorted by AgentDisplayName OrdinalIgnoreCase), E17 (a row is
/// valid only when empty or 0.01 &lt;= value &lt;= 1_000_000, as parsed before rounding), E19
/// (Discard re-applies the preview with the entry budgets), E20 (row agent is trimmed + lower-cased;
/// preview budgets follow the Normalize rules, so preview equals what Save stores).
/// Member names follow the merged Q1 code per E22: <c>BudgetRows</c> of
/// <c>AgentBudgetRowViewModel</c> with <c>Agent</c> / <c>DisplayName</c> / <c>Text</c> / <c>Error</c>
/// / <c>HasError</c>, ctor <c>SettingsViewModel(session, startupRegistrar, applyPreview, knownAgents)</c>.
/// </summary>
[Collection("Wpf")]
public sealed class SettingsViewModelAgentBudgetsTests
{
    // Q1.3: the exact error text shown under an invalid row.
    private const string BudgetErrorText = "Enter an amount in USD between 0.01 and 1,000,000, or leave it empty.";

    private static SettingsViewModel Create(
        AppSettings initial,
        Action<AppSettings>? applyPreview,
        IReadOnlyList<string>? knownAgents)
    {
        var session = new SettingsSession(new FakeSettingsStore(initial), initial);
        return new SettingsViewModel(session, new FakeStartupRegistrar(), applyPreview, knownAgents);
    }

    private static AgentBudgetRowViewModel OpenCodeRow(SettingsViewModel vm) =>
        vm.BudgetRows.Single(r => r.DisplayName == "OpenCode");

    // ── E9 / E14: which rows appear, in what order ─────────────────────────────

    [Fact]
    public void BudgetRows_E9_E14_OpenCodeFirstThenKnownAndSavedSortedByDisplayName()
    {
        // E9: opencode (always first) + known agents + saved budget keys, distinct case-insensitively.
        // E14: the rest ordered by AgentDisplayName with StringComparer.OrdinalIgnoreCase:
        // Antigravity < Claude Code < Codex < Copilot < ZCode.
        var vm = Create(
            new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["codex"] = 10m } },
            applyPreview: null,
            knownAgents: ["codex", "Claude", "antigravity", "OPENCODE", "copilot", "zcode", "CODEX"]);

        Assert.Equal(
            ["OpenCode", "Antigravity", "Claude Code", "Codex", "Copilot", "ZCode"],
            vm.BudgetRows.Select(r => r.DisplayName));
    }

    [Fact]
    public void BudgetRows_Q13_NullKnownAgents_ShowsOpenCodeAndSavedOnly()
    {
        // Q1.3: knownAgents == null → only opencode + the agents already present in AgentBudgets.
        // "gemini" has no display-name mapping (AgentDisplayName passes it through), so the sorted
        // rest is Copilot, then gemini (C < g under OrdinalIgnoreCase).
        var vm = Create(
            new AppSettings
            {
                AgentBudgets = new Dictionary<string, decimal> { ["copilot"] = 5m, ["gemini"] = 2m },
            },
            applyPreview: null,
            knownAgents: null);

        Assert.Equal(["OpenCode", "Copilot", "gemini"], vm.BudgetRows.Select(r => r.DisplayName));
    }

    [Fact]
    public void BudgetRows_E20_KnownAgentIsTrimmedAndLowerCased_AndLabelsComeFromThatName()
    {
        // E20: "Budget rows hold the agent name trimmed + lower-cased (labels from AgentDisplayName
        // of that name)" — the row that Save writes is keyed by this normalised name, so a ccusage
        // name that arrives padded (" CODEX ") must still become the key "codex".
        var vm = Create(new AppSettings(), applyPreview: null, knownAgents: [" CODEX ", "OpenCode"]);

        var codex = vm.BudgetRows.Single(r => r.DisplayName == "Codex");

        Assert.Equal("codex", codex.Agent);
        Assert.Equal(1, vm.BudgetRows.Count(r => string.Equals(r.Agent, "codex", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("opencode", OpenCodeRow(vm).Agent); // the always-first row is normalised too
    }

    // ── Q1.3: the box pre-fills from the saved budget ──────────────────────────

    [Fact]
    public void BudgetRows_Q13_SavedBudgetPrefillsItsRowAndIsInitiallyValid()
    {
        var vm = Create(
            new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20.01m } },
            applyPreview: null,
            knownAgents: null);

        var row = OpenCodeRow(vm);

        Assert.Equal("", row.Error);
        Assert.False(row.HasError);
        Assert.False(vm.HasBudgetError);
        // The box holds the saved budget (the display format is not pinned — parse it back).
        Assert.True(decimal.TryParse(row.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal(20.01m, parsed);
    }

    // ── Q1.3 / E17: parsing rules ($ prefix, whitespace, bounds, culture) ──────

    [Theory]
    // Q1.3: empty, or a parseable amount; E17: valid only when 0.01 <= value <= 1_000_000 as PARSED
    // (before rounding), so 0, 0.001, 0.004 and $0.0049 are invalid even though they would round to
    // 0.00; the 0.01 and 1,000,000 boundaries are inclusive. Whitespace trimmed, leading $ ignored,
    // decimal.TryParse with NumberStyles.Number and CultureInfo.InvariantCulture.
    [InlineData("", true)]
    [InlineData("   ", true)]                  // whitespace only → empty → no budget
    [InlineData("20", true)]
    [InlineData(" 19.99 ", true)]
    [InlineData("$20", true)]                  // leading $ accepted and ignored
    [InlineData("$ 20.50", true)]
    [InlineData("1,000.50", true)]             // thousands separator under InvariantCulture
    [InlineData("0.01", true)]                 // E17: the lower bound is inclusive
    [InlineData("1000000", true)]              // E17: the upper bound is inclusive
    [InlineData("0", false)]                   // E17: below 0.01
    [InlineData("$0", false)]
    [InlineData("0.004", false)]               // E17: would round to 0.00 → invalid, not "rounds to nothing"
    [InlineData("0.001", false)]               // E17
    [InlineData("$0.0049", false)]             // E17: the $ is stripped before the bound check
    [InlineData("0.009", false)]               // E17: still below 0.01 as parsed
    [InlineData("-5", false)]
    [InlineData("abc", false)]
    [InlineData("20.50.50", false)]
    [InlineData("1000000.01", false)]          // E17: > 1_000_000
    public void Text_Q13_E17_ParsingRules_SetRowErrorAndGateSave(string input, bool valid)
    {
        var vm = Create(new AppSettings(), applyPreview: null, knownAgents: null);
        var row = OpenCodeRow(vm);

        row.Text = "7"; // a real change for every input, including the empty one
        row.Text = input;

        Assert.Equal(valid ? "" : BudgetErrorText, row.Error);
        Assert.Equal(!valid, row.HasError);
        Assert.Equal(!valid, vm.HasBudgetError);
        Assert.Equal(valid, vm.SaveCommand.CanExecute(null)); // E6
    }

    [Fact]
    public void Text_Q13_CommaIsAThousandsSeparator_So_2050_IsWhatTheRowParses()
    {
        // Spec decision (kept as specified, after the review round): NumberStyles.Number with
        // CultureInfo.InvariantCulture reads "20,50" as 2050 — the comma is a thousands separator,
        // not a decimal point. 2050 is inside the E17 bounds, so the row is VALID with that value.
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings(), previews.Add, knownAgents: null);
        var row = OpenCodeRow(vm);

        row.Text = "20,50";

        Assert.Equal("", row.Error);
        Assert.False(row.HasError);
        Assert.False(vm.HasBudgetError);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(2050m, previews[^1].AgentBudgets["opencode"]); // hand-derived: 2050, not 20.50
    }

    [Fact]
    public void Text_Q13_ParsingIsInvariantCulture_RegardlessOfThreadCulture()
    {
        // Q1.3: parse with CultureInfo.InvariantCulture — a thread culture that uses "," as the
        // decimal separator must not change what is parsed. "20.50" stays twenty-and-a-half (a
        // German parse would give 2050) and the German spelling "1.234,56" is not accepted at all.
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var previews = new List<AppSettings>();
            var vm = Create(new AppSettings(), previews.Add, knownAgents: null);
            var row = OpenCodeRow(vm);

            row.Text = "20.50"; // dot is the decimal point in InvariantCulture
            Assert.Equal("", row.Error);
            Assert.Equal(20.50m, previews[^1].AgentBudgets["opencode"]);

            row.Text = "1.234,56"; // German for 1234.56 — two separators, invalid for InvariantCulture
            Assert.Equal(BudgetErrorText, row.Error);
            Assert.True(vm.HasBudgetError);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    // ── E6: Save CanExecute = !HasIntervalError && !HasBudgetError ─────────────

    [Fact]
    public void SaveCommand_E6_DisabledByIntervalErrorOrBudgetError_EnabledWhenBothClean()
    {
        var vm = Create(new AppSettings(), applyPreview: null, knownAgents: null);
        var row = OpenCodeRow(vm);

        // Clean dialog.
        Assert.True(vm.SaveCommand.CanExecute(null));

        // Budget error alone disables Save (E6).
        row.Text = "abc";
        Assert.True(vm.HasBudgetError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Valid budget, broken interval: the existing interval gate still stands (E6 conjunction).
        row.Text = "25";
        vm.RefreshIntervalText = "12"; // not 0 and not within 30..86,400 → interval error (pre-existing rule)
        Assert.False(vm.HasBudgetError);
        Assert.True(vm.HasIntervalError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Both clean again.
        vm.RefreshIntervalText = "300";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // ── E5 / E20: budgets join the live preview ────────────────────────────────

    [Fact]
    public void Preview_E5_BudgetRowChange_ReRunsPreviewWithValidNonEmptyRows_InvalidRowLeftOut()
    {
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings(), previews.Add, ["codex"]);
        var opencode = OpenCodeRow(vm);
        var codex = vm.BudgetRows.Single(r => r.DisplayName == "Codex");

        // Every text change of a budget box re-runs the existing preview (E5).
        opencode.Text = "30";
        Assert.NotEmpty(previews);
        var afterOpencode = previews[^1].AgentBudgets;
        Assert.Equal(30m, afterOpencode["opencode"]);
        Assert.False(afterOpencode.ContainsKey("codex")); // empty row = no budget (Q1.3)

        // An invalid row is left out of the preview, as on Save (E5).
        var callsBeforeInvalid = previews.Count;
        codex.Text = "abc";
        Assert.True(previews.Count > callsBeforeInvalid); // the change still re-runs the preview
        var last = previews[^1].AgentBudgets;
        Assert.Equal(30m, last["opencode"]);
        Assert.False(last.ContainsKey("codex"));
    }

    [Theory]
    // E20: "Preview budgets use the same rules as Normalize (key normalised, value rounded 2 dp
    // AwayFromZero), so preview equals what Save stores". Hand-derived: 20.005 → 20.01 (the midpoint
    // goes away from zero, matching Q1.1/O6); 2.675 → 2.68; 19.994 → 19.99.
    // An E17-invalid amount is never previewed at all (0.004 is rejected before it can round to 0.00).
    [InlineData("20.005", "20.01")]
    [InlineData("2.675", "2.68")]
    [InlineData("19.994", "19.99")]
    [InlineData("$0.004", null)]
    [InlineData("", null)]
    public void Preview_E20_E17_RowValueIsRoundedLikeNormalizeOrLeftOut(string input, string? expected)
    {
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings(), previews.Add, knownAgents: null);
        var row = OpenCodeRow(vm);

        row.Text = "5"; // start from a real budget so clearing the row is also a change
        row.Text = input;

        var previewed = previews[^1].AgentBudgets;
        if (expected is null)
        {
            Assert.Empty(previewed);
        }
        else
        {
            Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), previewed["opencode"]);
        }
    }

    [Fact]
    public async Task Save_E20_StoredBudgetsEqualTheLastPreview()
    {
        // E20: "preview equals what Save stores" — the same rows drive both, so the last preview
        // before Save and the dictionary that lands in the store must agree.
        var initial = new AppSettings();
        var store = new FakeSettingsStore(initial);
        using var session = new SettingsSession(store, initial);
        var previews = new List<AppSettings>();
        var vm = new SettingsViewModel(session, new FakeStartupRegistrar(), previews.Add, ["codex"]);

        OpenCodeRow(vm).Text = "20.005";                                  // → 20.01 (2 dp, away from zero)
        vm.BudgetRows.Single(r => r.DisplayName == "Codex").Text = "$12.5"; // $ ignored → 12.5

        var previewed = previews[^1].AgentBudgets;
        await vm.SaveAsync();
        var saved = store.SavedSettings!.AgentBudgets;

        Assert.Equal(previewed.Count, saved.Count);
        Assert.Equal(previewed["opencode"], saved["opencode"]);
        Assert.Equal(previewed["codex"], saved["codex"]);
        Assert.Equal(20.01m, saved["opencode"]);
        Assert.Equal(12.5m, saved["codex"]);
    }

    // ── E13 / Q1.3: Save writes only valid, non-empty rows ────────────────────

    [Fact]
    public async Task SaveAsync_E13_EmptiedRowRemovesKeyAndWritesValidRowsOnly_NoZeroEntry()
    {
        // E13: an emptied row removes the key (never a 0 entry); other saved keys survive; a new
        // valid row is added with the $ prefix stripped.
        var initial = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m, ["codex"] = 10m },
        };
        var store = new FakeSettingsStore(initial);
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, new FakeStartupRegistrar(), knownAgents: ["zcode"]);

        OpenCodeRow(vm).Text = "";                                       // emptied → removed
        vm.BudgetRows.Single(r => r.DisplayName == "Codex").Text = "$12.50"; // $ ignored
        vm.BudgetRows.Single(r => r.DisplayName == "ZCode").Text = "5.25";    // newly added

        await vm.SaveAsync();

        var saved = store.SavedSettings!;
        Assert.False(saved.AgentBudgets.ContainsKey("opencode")); // never a 0 entry either
        Assert.Equal(12.50m, saved.AgentBudgets["codex"]);
        Assert.Equal(5.25m, saved.AgentBudgets["zcode"]);
        Assert.Equal(["codex", "zcode"], saved.AgentBudgets.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_Q13_LeavingEveryRowUntouched_KeepsSavedBudgets()
    {
        // The rows pre-fill from the saved dictionary; touching nothing must change nothing.
        var initial = new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m },
        };
        var store = new FakeSettingsStore(initial);
        using var session = new SettingsSession(store, initial);
        var vm = new SettingsViewModel(session, new FakeStartupRegistrar());

        await vm.SaveAsync();

        Assert.Equal(20m, store.SavedSettings!.AgentBudgets["opencode"]);
    }

    // ── Q1.3 / E19: discard restores the entry settings ────────────────────────

    [Fact]
    public void Discard_E19_ReAppliesPreviewWithEntryBudgets_LikeTheOtherPreviewedFields()
    {
        // E19: "Discard() re-applies the preview with AgentBudgets taken from _entrySettings, like
        // the other previewed fields" — the live gauge/preview returns to the settings that were in
        // place when the dialog was opened, whatever was typed since.
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m } },
            previews.Add, knownAgents: ["codex"]);

        OpenCodeRow(vm).Text = "99";
        vm.BudgetRows.Single(r => r.DisplayName == "Codex").Text = "5";
        Assert.Equal(99m, previews[^1].AgentBudgets["opencode"]);
        Assert.True(previews[^1].AgentBudgets.ContainsKey("codex"));

        vm.Discard();

        var discarded = previews[^1].AgentBudgets;
        Assert.Equal(20m, discarded["opencode"]);  // back to the entry settings
        Assert.False(discarded.ContainsKey("codex")); // a row typed after entry is gone again
    }

    [Fact]
    public void Cancel_Q13_RestoresEntryBudgets_InTheLivePreview()
    {
        // Q1.3: "Cancel/discard restores the entry settings like the other fields" — budgets take
        // part in the same discard path (Cancel goes through Discard, E19).
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m } },
            previews.Add, knownAgents: null);

        OpenCodeRow(vm).Text = "99";
        Assert.Equal(99m, previews[^1].AgentBudgets["opencode"]);

        vm.Cancel();

        Assert.Equal(20m, previews[^1].AgentBudgets["opencode"]); // preview back to the entry settings
    }

    // ── Fakes (same hand-written pattern as SettingsViewModelTests) ────────────

    private sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
    {
        public bool Exists => true;
        public AppSettings? SavedSettings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered() => false;

        public void Register(string executablePath, string arguments = "")
        {
        }

        public void Unregister()
        {
        }
    }
}
