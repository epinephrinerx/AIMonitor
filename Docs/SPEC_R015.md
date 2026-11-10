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
