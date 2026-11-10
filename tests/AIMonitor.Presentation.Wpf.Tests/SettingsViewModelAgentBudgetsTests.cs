using System.Globalization;
using System.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q1.3 — monthly budget rows in <see cref="SettingsViewModel"/>, written from
/// Docs/SPEC_R015.md only (§5.6): Q1.3 (one row per agent, parsing rules, error text, Save writes
/// only valid non-empty rows, discard restores) and decisions E5 (live preview), E6 (HasBudgetError
/// gates Save), E9 (known agents ∪ saved keys ∪ opencode), E13 (emptied row removes the key, never a
/// 0 entry) and E14 (opencode first, the rest sorted by AgentDisplayName OrdinalIgnoreCase).
/// Expected not to compile until Q1 merges (handoff T16).
/// </summary>
/// <remarks>
/// ASSUMPTION for WO2 (the spec pins the ctor param <c>knownAgents</c>, <c>HasBudgetError</c>, the
/// per-row <c>Error</c> and the error string, but not the row collection/box member names): rows are
/// read through <c>vm.AgentBudgetRows</c> with <c>DisplayName</c>, a settable <c>BudgetText</c> and
/// <c>Error</c>, mirroring the existing <c>Providers</c>/<c>ProviderToggle</c> and
/// <c>RefreshIntervalText</c>/<c>RefreshIntervalError</c> precedent.
/// </remarks>
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
        return new SettingsViewModel(session, new FakeStartupRegistrar(), applyPreview, knownAgents: knownAgents);
    }

    // ── E9 / E14: which rows appear, in what order ─────────────────────────────

    [Fact]
    public void AgentBudgetRows_E9_E14_OpenCodeFirstThenKnownAndSavedSortedByDisplayName()
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
            vm.AgentBudgetRows.Select(r => r.DisplayName));
    }

    [Fact]
    public void AgentBudgetRows_Q13_NullKnownAgents_ShowsOpenCodeAndSavedOnly()
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

        Assert.Equal(["OpenCode", "Copilot", "gemini"], vm.AgentBudgetRows.Select(r => r.DisplayName));
    }

    [Fact]
    public void AgentBudgetRows_Q13_SavedBudgetPrefillsItsBoxAndIsInitiallyValid()
    {
        var vm = Create(
            new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20.01m } },
            applyPreview: null,
            knownAgents: null);

        var row = vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode");

        Assert.Equal("", row.Error);
        Assert.False(vm.HasBudgetError);
        // The box holds the saved budget (display format is not pinned — parse it back).
        Assert.True(decimal.TryParse(row.BudgetText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal(20.01m, parsed);
    }

    // ── Q1.3: parsing rules ($ prefix, whitespace, boundaries, culture) ────────

    [Theory]
    // Q1.3: "empty, or 0 < value <= 1_000_000"; whitespace trimmed; leading $ accepted and ignored;
    // decimal.TryParse with NumberStyles.Number and CultureInfo.InvariantCulture.
    [InlineData("", true)]
    [InlineData("   ", true)]                  // whitespace only → empty → no budget
    [InlineData("20", true)]
    [InlineData(" 19.99 ", true)]
    [InlineData("$20", true)]                  // leading $ accepted and ignored
    [InlineData("$ 20.50", true)]
    [InlineData("1,000.50", true)]             // thousands separator under InvariantCulture
    [InlineData("0.01", true)]
    [InlineData("1000000", true)]              // boundary is inclusive
    [InlineData("0", false)]                   // not > 0
    [InlineData("$0", false)]
    [InlineData("-5", false)]
    [InlineData("abc", false)]
    [InlineData("20.50.50", false)]
    [InlineData("1000000.01", false)]          // > 1_000_000
    [InlineData("20,50", false)]               // comma is a thousands separator, not a decimal point
    public void BudgetText_Q13_ParsingRules_SetErrorAndGateSave(string input, bool valid)
    {
        var vm = Create(new AppSettings(), applyPreview: null, knownAgents: null);
        var row = vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode");

        row.BudgetText = input;

        Assert.Equal(valid ? "" : BudgetErrorText, row.Error);
        Assert.Equal(!valid, vm.HasBudgetError);
        Assert.Equal(valid, vm.SaveCommand.CanExecute(null)); // E6
    }

    [Fact]
    public void BudgetText_Q13_ParsingIsInvariantCulture_RegardlessOfThreadCulture()
    {
        // Q1.3: parse with CultureInfo.InvariantCulture — a thread culture that uses "," as the
        // decimal separator must not change what is accepted.
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var vm = Create(new AppSettings(), applyPreview: null, knownAgents: null);
            var row = vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode");

            row.BudgetText = "20.50"; // dot is the decimal point in InvariantCulture
            Assert.Equal("", row.Error);

            row.BudgetText = "20,50"; // German-style decimal comma is NOT accepted
            Assert.Equal(BudgetErrorText, row.Error);
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
        var row = vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode");

        // Clean dialog.
        Assert.True(vm.SaveCommand.CanExecute(null));

        // Budget error alone disables Save (E6).
        row.BudgetText = "abc";
        Assert.True(vm.HasBudgetError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Valid budget, broken interval: the existing interval gate still stands (E6 conjunction).
        row.BudgetText = "25";
        vm.RefreshIntervalText = "12"; // not 0 and not within 30..86,400 → interval error (pre-existing rule)
        Assert.False(vm.HasBudgetError);
        Assert.True(vm.HasIntervalError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Both clean again.
        vm.RefreshIntervalText = "300";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // ── E5: budgets join the live preview ──────────────────────────────────────

    [Fact]
    public void Preview_E5_BudgetBoxChange_ReRunsPreviewWithValidNonEmptyRows_InvalidRowLeftOut()
    {
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings(), previews.Add, ["codex"]);
        var opencode = vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode");
        var codex = vm.AgentBudgetRows.Single(r => r.DisplayName == "Codex");

        // Every text change of a budget box re-runs the existing preview (E5).
        opencode.BudgetText = "30";
        Assert.NotEmpty(previews);
        var afterOpencode = previews[^1].AgentBudgets;
        Assert.Equal(30m, afterOpencode["opencode"]);
        Assert.False(afterOpencode.ContainsKey("codex")); // empty row = no budget (Q1.3)

        // An invalid row is left out of the preview, as on Save (E5).
        var callsBeforeInvalid = previews.Count;
        codex.BudgetText = "abc";
        Assert.True(previews.Count > callsBeforeInvalid); // the change still re-runs the preview
        var last = previews[^1].AgentBudgets;
        Assert.Equal(30m, last["opencode"]);
        Assert.False(last.ContainsKey("codex"));
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

        vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode").BudgetText = "";   // emptied → removed
        vm.AgentBudgetRows.Single(r => r.DisplayName == "Codex").BudgetText = "$12.50"; // $ ignored
        vm.AgentBudgetRows.Single(r => r.DisplayName == "ZCode").BudgetText = "5.25";   // newly added

        await vm.SaveAsync();

        var saved = store.SavedSettings!;
        Assert.False(saved.AgentBudgets.ContainsKey("opencode")); // never a 0 entry either
        Assert.Equal(12.50m, saved.AgentBudgets["codex"]);
        Assert.Equal(5.25m, saved.AgentBudgets["zcode"]);
        Assert.Equal(2, saved.AgentBudgets.Count);
    }

    [Fact]
    public async Task SaveAsync_Q13_LeavingEveryBoxUntouched_KeepsSavedBudgets()
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

    // ── Q1.3: discard/cancel restores the entry settings ───────────────────────

    [Fact]
    public void Cancel_Q13_RestoresEntryBudgets_InTheLivePreview()
    {
        // Q1.3: "Cancel/discard restores the entry settings like the other fields" — budgets take
        // part in the same discard path.
        var previews = new List<AppSettings>();
        var vm = Create(new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 20m } },
            previews.Add, knownAgents: null);

        vm.AgentBudgetRows.Single(r => r.DisplayName == "OpenCode").BudgetText = "99";
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
