# SPEC R-015 — OpenCode tab and monthly budget gauge (Coding Owner, 2026-11-10)

Requirement: `Requirements.md` R-015 (✅ 2026-11-10). Data comes only from the ccusage report of R-014
(`AgentUsageRefresher` / `CachedAgentUsageSource`: app start, every 15 min, manual refresh). No new ccusage run, no
OpenCode log reading, no network. Layering per `Docs/ARCHITECTURE.md`. R-016 (Overview tab) is **not** in scope, but
Q1 is written so R-016 can reuse it.

## Phases and owners

| Phase | Scope | Writing Owner | Tests |
|---|---|---|---|
| Q1 | Budget setting + budget calculator + Settings UI | WO2 (opencode, kimi-k2.7-code) | Test Author (qwen3.8-flash) |
| Q2 | OpenCode tab, gauge, ccusage rows, tab order, ActiveProvider, live budget apply | WO3 (opencode, deepseek-v4.1-flash), starts after Q1 merges | Test Author |

Q1 file ownership: `src/AIMonitor.Application/Settings/AppSettings.cs`, new `src/AIMonitor.Application/AgentUsage/AgentBudget.cs`,
`src/AIMonitor.Presentation.Wpf/ViewModels/SettingsViewModel.cs`, `src/AIMonitor.Presentation.Wpf/SettingsDialog.xaml(.cs)`,
the `OpenSettingsDialog` call site in `App.xaml.cs` (passing known agents only).
Q2 file ownership: `MainWindowViewModel.cs`, `ProviderTabViewModel.cs`, `MainWindow.xaml` (only if needed),
`LiveSettingsApplier.cs`, new `src/AIMonitor.Presentation.Wpf/ViewModels/OpenCodeSnapshotBuilder.cs`.

## Q1.1 Setting — `AppSettings.AgentBudgets`

- New property `IReadOnlyDictionary<string, decimal> AgentBudgets` — monthly budget in USD per **ccusage agent name**
  (e.g. `opencode`, `claude`, `codex`, `antigravity`, `copilot`). Default: empty dictionary (no budgets).
- `Normalize()`:
  - `null` → empty dictionary;
  - keys trimmed and lower-cased (`ToLowerInvariant`); blank keys dropped; on duplicate keys after normalising, the
    **last** entry in enumeration order wins;
  - values `<= 0` or `> 1_000_000` dropped; others rounded to 2 decimals (`MidpointRounding.AwayFromZero`);
  - resulting dictionary uses `StringComparer.OrdinalIgnoreCase`.
- `ActiveProvider` accepts `"opencode"` in addition to the existing values (`claude`, `openai`, `agy`, `all-agents`).
- Persistence: the existing JSON settings store; an old settings file without the property loads as "no budgets".
  `SchemaVersion` stays 1 (additive field).

## Q1.2 Calculator — `AIMonitor.Application.AgentUsage.AgentBudget`

```csharp
public sealed record AgentBudgetReading(
    string Agent,              // normalised lower-case agent name
    decimal MonthCostUsd,      // priced cost of this agent in the calendar month of `today` (0 when absent)
    bool HasUnpricedModels,    // true when that month row has a model without a price
    decimal? BudgetUsd,        // null when no budget is set
    double? Percent,           // null when BudgetUsd is null; else (double)(MonthCostUsd / BudgetUsd * 100), NOT capped
    bool HasUsageThisMonth);   // true when the report has a monthly row for this agent in that month

public static class AgentBudget
{
    public static AgentBudgetReading? Compute(
        AgentUsageReport? report, string agent, IReadOnlyDictionary<string, decimal> budgets, DateOnly today);
}
```

- `report == null` → returns `null` (nothing known yet).
- Month = `today.Year`/`today.Month`; `today` is the **local** date supplied by the caller (same as
  `AgentUsagePresenter.SummaryFor`). Only `report.Monthly` is read (not the sum of daily rows).
- Agent match is case-insensitive on the normalised name. Missing month or agent → `MonthCostUsd = 0`,
  `HasUnpricedModels = false`, `HasUsageThisMonth = false`.
- `MonthCostUsd = AgentUsage.PricedCostUsd` of that row (unpriced models are not counted as $0 cost; they set the flag).
- Budget lookup by agent (case-insensitive); a budget `<= 0` is treated as absent.
- Pure, no clock, no I/O. Never throws for a valid report; `agent` blank → `ArgumentException`; `budgets` null →
  `ArgumentNullException`.

## Q1.3 Settings UI

- New group **"Monthly budgets (ccusage)"** in `SettingsDialog`, one row per agent: label = display name
  (`AgentUsagePresenter.AgentDisplayName`), a text box for the budget in USD, empty = no budget.
- Agents listed = `opencode` (always first) + the agents in the last good ccusage report (any month/day/session)
  + the agents already present in `AgentBudgets`; distinct (case-insensitive), then the rest ordered by display name
  (`StringComparer.OrdinalIgnoreCase`). `SettingsViewModel` receives the known agent names through a new optional
  constructor parameter `IReadOnlyList<string>? knownAgents = null` (null → only `opencode` + saved ones);
  `App.OpenSettingsDialog` passes the agents of the main view model's last good report.
- Parsing: `decimal.TryParse` with `NumberStyles.Number` and `CultureInfo.InvariantCulture`; whitespace trimmed;
  a leading `$` is accepted and ignored. Valid: empty, or `0 < value <= 1_000_000`. Invalid input shows an error text
  under that row (`"Enter an amount in USD between 0.01 and 1,000,000, or leave it empty."`) and disables Save, exactly
  like the existing refresh-interval error.
- Save writes `AgentBudgets` with only the non-empty valid rows (an emptied row removes the budget). Cancel/discard
  restores the entry settings like the other fields. Budgets take part in the existing live preview
  (`applyPreview`).
- No change to the existing OpenAI "Monthly budget" field on the Connections page (that is the OpenAI API spend
  target, not ccusage).

## Q2.1 OpenCode tab

- `MainWindowViewModel`: `OpenCodeTab = new ProviderTabViewModel("opencode", "OpenCode")`.
  `Tabs = [ClaudeTab, OpenAiTab, AgyTab, OpenCodeTab, AllAgentsTab]`.
  `ProviderTabs` stays `[ClaudeTab, OpenAiTab, AgyTab]` — OpenCode is **not** in the tray, widget or provider refresh.
- Selecting the tab saves `ActiveProvider = "opencode"`; startup with `ActiveProvider == "opencode"` selects it
  (same mechanism as `all-agents`, decisions C13–C15 of SPEC_R014).
- `ProviderMeta` is **not** extended (no Settings toggle or Connections card for OpenCode).

## Q2.2 Gauge and rows

On every `AgentUsageRefresher.Updated` (lastGood, lastError) and on every live settings apply, the view model builds a
`ProviderSnapshot` with `OpenCodeSnapshotBuilder.Build(lastGood, lastError, budgets, today, now)` and calls
`OpenCodeTab.UpdateFromSnapshot(snapshot, now)`, then `OpenCodeTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "opencode", today))`.
`today` = local date of `now` (the view model's clock / `TimeProvider` already used for SummaryFor).

`OpenCodeSnapshotBuilder.Build` (Presentation, pure):

| State | Snapshot |
|---|---|
| `lastGood == null && lastError == null` (first run still going) | no snapshot is pushed; the tab keeps its initial "Checking..." state |
| `lastGood == null && lastError != null` | `configured: false`, `error: AgentUsagePresenter.ErrorText(lastError.Kind, lastError.Message)`, no meters |
| `lastGood != null` | `configured: true`, one meter (below), no stats, `error: null` (a later `lastError` while `lastGood` exists is ignored here, matching the All agents tab) |

Meter (`lastGood != null`), from `AgentBudget.Compute(lastGood, "opencode", budgets, today)`:
- `kind: "month_budget"`, `key: "month_budget"`, `title: "Month to date"`;
- budget set → `subtitle: "of your {money(budget)} budget"`, `percent: reading.Percent` (not capped; e.g. 120);
  no budget → `subtitle: "No budget set — add one in Settings"`, `percent: null`;
- `detail: money(MonthCostUsd)` plus `" + unpriced"` when `HasUnpricedModels`;
- `resetsAt`: first day of the next local month at 00:00 local, as `DateTimeOffset`;
- `serverSeverity: Normal` (colour comes from the existing local thresholds via `EffectiveSeverity`).
- `money(x)` = the same text `AgentUsagePresenter` uses for a priced cost (e.g. `$12.34`, `<$0.01`, `$0.00`).

Rows: `ProviderTabViewModel` treats `"opencode"` exactly like `"agy"` for ccusage rows (C8/C9/C10/C19/C21 of SPEC_R014):
a ccusage-only section with "Today (ccusage)" / "This month (ccusage)", no reset part.

Live budget apply: `LiveSettingsApplier.Apply(settings)` calls a new
`MainWindowViewModel.ApplyAgentBudgets(IReadOnlyDictionary<string, decimal> budgets)` which stores the budgets and
rebuilds the OpenCode snapshot from the stored lastGood/lastError **without** running ccusage. Runs on the UI thread
(same as the rest of `Apply`).

## Out of scope (R-015 "ไม่รวม")
Real OpenCode / opencode-go plan quota; reading OpenCode logs; tray/widget; notifications; changing the Claude, Codex or
AGY gauges; the Overview tab (R-016).

## Oracles (for tests)

| Case | Input | Expected |
|---|---|---|
| O1 | opencode month row priced cost 12.345, budget 50 | MonthCostUsd 12.345, Percent 24.69, subtitle `of your $50.00 budget` |
| O2 | cost 60, budget 50 | Percent 120 (not capped), gauge text `120%` |
| O3 | cost 7.5, no budget | Percent null, gauge text `--`, subtitle `No budget set — add one in Settings`, detail `$7.50` |
| O4 | report has no opencode row this month (only last month) | MonthCostUsd 0, HasUsageThisMonth false, budget 10 → Percent 0 |
| O5 | opencode row: model A cost 3, model B unpriced | MonthCostUsd 3, HasUnpricedModels true, detail `$3.00 + unpriced` |
| O6 | settings `{" OpenCode ": 20.005, "codex": 0, "": 5, "x": 2000000}` | Normalize → `{"opencode": 20.01}` |
| O7 | today 2026-11-30 local | resetsAt 2026-12-01T00:00 local |
| O8 | lastGood null, lastError NotInstalled | configured false, error = `ErrorText(NotInstalled, …)`, no meters |

## Coding Owner decisions after Inspector A (2026-11-10) — override anything above

| Id | Decision | Owner |
|---|---|---|
| E1 | `OpenCodeSnapshotBuilder.Build(lastGood, lastError, budgets, today, now, TimeZoneInfo zone)`. `resetsAt` = `today.AddMonths(1)` → day 1 at 00:00 in `zone`, offset = `zone.GetUtcOffset(that local DateTime)`. The view model passes the same zone it uses for SummaryFor/sessions. Oracle O7 holds; also 2026-01-31 → 2026-02-01, 2024-02-29 → 2024-03-01. | WO3 + TA |
| E2 | `MainWindowViewModel` keeps the last `(lastGood, lastError)` received in `OnAgentUsageUpdated` and the last budgets; `ApplyAgentBudgets(budgets)` stores the budgets and rebuilds the OpenCode snapshot from the stored pair (no ccusage run). Before the first `Updated`, `ApplyAgentBudgets` only stores. | WO3 + TA |
| E3 | `ProviderTabViewModel.SupportsAgentUsage` = `AppendsAgentUsage || id is "agy" or "opencode"` (case-insensitive). Other ids (e.g. `gemini`) still ignore a summary (C25a). | WO3 + TA |
| E4 | `AppSettings.Normalize` keeps `ActiveProvider == "opencode"`. Startup selection looks up `ActiveProvider` among all `ProviderTabViewModel`s in `Tabs` (so `opencode` selects `OpenCodeTab`); `ProviderTabs` itself stays the three provider tabs. Unknown value → Claude as now. | WO2 (Normalize) + WO3 (selection) + TA |
| E5 | Budgets join the live preview: every text change of a budget box re-runs the existing preview with `AgentBudgets` built from the currently **valid, non-empty** rows (invalid rows are left out of the preview, as on Save). | WO2 + TA |
| E6 | `SettingsViewModel.HasBudgetError` (true when any row is invalid); Save's CanExecute = `!HasIntervalError && !HasBudgetError`; per-row `Error` text as in Q1.3. | WO2 + TA |
| E7 | `AgentBudget.Compute`: blank `agent` → `ArgumentException` with `ParamName == "agent"`; month row = `m.Year == today.Year && m.Month == today.Month`; `reading.Agent` = `agent.Trim().ToLowerInvariant()`. | WO2 + TA |
| E8 | `Normalize` always returns a **new** dictionary with `StringComparer.OrdinalIgnoreCase` (also after JSON load, where the comparer is lost). JSON round trip of `{ "opencode": 20.01 }` must give the same value. | WO2 + TA |
| E9 | Known agents for Settings = distinct agent names over `report.Daily`, `report.Monthly` and `report.Sessions` of the last good report (`CachedAgentUsageSource.LastGood` or the equivalent already reachable from `App`), plus saved budget keys, plus `opencode`. | WO2 + TA |
| E10 | `lastGood == null && lastError != null` → snapshot `configured: true`, `error: ErrorText(kind, message)`, no meters, no stats — so the tab shows the error banner (not the setup card, which would have an empty hint). WO3 verifies no empty gauge card is drawn when there are no meters. | WO3 + TA |
| E11 | Rejected D11: `Meter.EffectiveSeverity` already applies the local thresholds from the percent (`Meter.cs:76`, `SeverityRules.Combine`), so 120 % is Critical without new code. Test: O2 gauge severity is Critical. | TA |
| E12 | `money(x)` = `ChartFormat.Money((double)x)`, the formatter `AgentUsagePresenter` uses for a priced cost; the budget in the subtitle uses it too. | WO3 + TA |
| E13 | Save writes only non-empty valid rows; an emptied row removes the key (never a 0 entry). | WO2 + TA |
| E14 | Settings agent order: `opencode` first, then the others distinct (case-insensitive) sorted by `AgentDisplayName` with `StringComparer.OrdinalIgnoreCase`. | WO2 + TA |
| E15 | The OpenAI "Monthly budget (USD)" field lives on the Connections page (`ProviderMeta.OpenAi.ExtraLabel`); it is unrelated and unchanged. | — |
| E16 | Test Author: put Q1 tests and Q2 tests in **separate commits** (Q1 tests must compile against Q1 alone; Q2 tests may not compile until Q2 merges). Assert final state, not the number of internal `ApplyAgentUsage` calls. | TA |

## Coding Owner decisions after Q1 code review (Reviewer1 + Reviewer2, 2026-11-10)

| Id | Decision | Owner |
|---|---|---|
| E17 | (MAJOR, both reviewers) A row is valid only when empty or `0.01 <= value <= 1_000_000` (value as parsed, before rounding); `0.004`, `0.001`, `$0.0049` are invalid with the Q1.3 error text. `Normalize` rounds first (2 dp, AwayFromZero) and then drops values `<= 0` or `> 1_000_000`, so a stored `0.00` can never appear. Oracle: `{ "opencode": 0.004 }` → `{}`; row `"0.004"` → error, Save disabled. | WO2 + TA |
| E18 | Duplicate keys after normalising: invalid values are skipped and never remove or replace an entry; among the **valid** ones the last in enumeration order wins. Oracles: `{ "opencode": 20, "OpenCode ": 0 }` → `{ "opencode": 20 }`; `{ "opencode": 20, "OpenCode": 30 }` → `{ "opencode": 30 }`. | WO2 + TA |
| E19 | `Discard()` re-applies the preview with `AgentBudgets` taken from `_entrySettings`, like the other previewed fields. | WO2 + TA |
| E20 | Budget rows hold the agent name trimmed + lower-cased (labels from `AgentDisplayName` of that name). Preview budgets use the same rules as `Normalize` (key normalised, value rounded 2 dp AwayFromZero), so preview equals what Save stores. Oracle: typing `20.005` previews `20.01`; knownAgents `" Codex "` → row agent `codex`. | WO2 + TA |
| E21 | `AgentBudget.Compute` finds the budget case-insensitively whatever comparer the caller's dictionary has (exact `TryGetValue` first, then an `OrdinalIgnoreCase` scan over trimmed keys). Oracle: default-comparer dictionary `{ "OpenCode": 10 }` → `BudgetUsd == 10`. | WO2 + TA |
| — | Kept as specified: `NumberStyles.Number` + invariant culture reads `20,50` as `2050` (Q1.3). | — |

## Coding Owner decisions after the first R-015 test round (2026-11-10)

| Id | Decision | Owner |
|---|---|---|
| E22 | Names the spec left open follow the Q1 code: `SettingsViewModel.BudgetRows` (`IReadOnlyList<AgentBudgetRowViewModel>`), row `Agent` / `DisplayName` / `Text` / `Error` / `HasError`; constructor `SettingsViewModel(session, startupRegistrar, applyPreview = null, knownAgents = null)`. | TA |
| E23 | `OpenCodeSnapshotBuilder.Build(...)` returns `ProviderSnapshot?`: `null` for "no ccusage result yet" (lastGood and lastError both null), and the view model then pushes nothing. The snapshot `ProviderId` is `"opencode"`. O8 is superseded by E10 (`configured: true`). | WO3 + TA |

## Coding Owner decisions after the Q2 code (2026-11-10)

| Id | Decision | Owner |
|---|---|---|
| E24 | Keep the existing setup-card rule for every provider: `ShowSetupCard = !configuredNow && meterList.Count == 0` (the WO3 change to `!snapshot.Configured` altered Claude/OpenAI/AGY when Detection is null and a configured snapshot carries an error — not allowed by R-015 "tab อื่นหน้าตาเดิม" / R-004). Only the `opencode` tab never shows the setup card (`&& !IsOpenCode`), so E10 still shows the error banner there. Test: a Claude/OpenAI/AGY snapshot with `configured: true`, `detection: null`, `error: "x"`, no meters still shows the setup card as before. | WO3 + TA |
| E25 | R-015 changes the tab list, so the existing R-014 test `MainWindowViewModelAllAgentsTests.Tabs_ExposesThreeProviderTabsThenAllAgentsTab_ProviderTabsUnchanged` is updated by the Test Author to `[Claude, OpenAI, Agy, OpenCode, AllAgents]` (ProviderTabs unchanged). | TA |

## Coding Owner decisions after Q2 code review (Reviewer1 + Reviewer2, 2026-11-10)

| Id | Decision | Owner |
|---|---|---|
| E26 | (MAJOR, Reviewer1) The gauge card shows no `Detail`, so the money goes into the OpenCode meter **subtitle** (the shared card template stays unchanged). Let `spent = money(MonthCostUsd) + (HasUnpricedModels ? " + unpriced" : "")`. Budget set → `subtitle: "{spent} of {money(budget)} budget"` (O1: `$12.35 of $50.00 budget`; O2: `$60.00 of $50.00 budget`). No budget → `subtitle: "{spent} · No budget set — add one in Settings"` (O3: `$7.50 · No budget set — add one in Settings`; O4 with no budget: `$0.00 · No budget set — add one in Settings`; O5 with no budget: `$3.00 + unpriced · No budget set — add one in Settings`). `detail` keeps `spent`. Overrides the subtitle texts in Q2.2/O1/O3. | WO3 + TA |
| E27 | Make the E5 budget preview visible: in `App.OpenSettingsDialog` the preview callback is `s => { applier.Apply(s); _mainViewModel.ApplyAgentBudgets(s.AgentBudgets); }`, so typing moves the gauge and Discard (E19) restores it. `App.xaml.cs` at that call site is a WO3 file for this decision. | WO3 + TA |
| E28 | The widget keeps the last selected **provider** tab: `ActiveProviderSource` returns `SelectedTab.ProviderId` only when that tab is in `ProviderTabs`; otherwise the last such id (initially the startup provider tab, Claude when startup is `opencode`/`all-agents`). Expose it from `MainWindowViewModel` as `ActiveProviderTabId` and use it at `App.xaml.cs:174`. | WO3 + TA |
| E29 | `ApplyAgentBudgets` does nothing when the new budgets equal the stored ones (same keys case-insensitively and same values); unrelated settings changes therefore do not rebuild the OpenCode tab. | WO3 + TA |
| E30 | Accepted as is: midnight-DST edge of E1 (Reviewer2 M1); OpenCode `Status` text (not bound in the UI). | — |
| C27 | (R-014 test, flaky 3×) `MainWindowViewModelAllAgentsTests.RefreshCommand_PassesManualTrue_ForceOnlyAppliesWhenAllAgentsSelected` races: read 2 is signalled inside `RefreshAsync` before `_agentUsageTask` is replaced, so `await vm.AgentUsageTask` can await the old task. Fix in the test only: after the read-2 signal, wait (bounded, pump-safe like the existing wait) until `vm.AllAgentsTab.PricingText == "Prices: offline (built-in)"`, then assert `ReadCount == 2`. | TA |
