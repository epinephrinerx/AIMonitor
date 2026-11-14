# SPEC R-016 — Overview tab: one budget gauge per ccusage agent

Requirement: `Requirements.md` R-016 (approved 2026-11-10). Builds on R-014 (ccusage source), R-015 (per-agent budgets,
`AgentBudget`, OpenCode tab), R-017 (OpenCode chart). One phase, **H1**.

## Scope

- New tab **"Overview"** between OpenCode and All agents. It shows one gauge per agent that ccusage reports for the
  **current local month**, computed exactly like the OpenCode gauge (R-015 Q2: month cost ÷ budget).
- Agents are data-driven from the ccusage report. **No agent names in code** for this tab.
- Same data, same refresh cycle as R-014/R-015 (the cached `lastGood` / `lastError` pair; no extra ccusage read).
- Budgets come from Settings → "Monthly budgets (ccusage)" (R-015 Q1). That list is already built from ccusage agents
  (`App.CollectKnownAgentNames`), so requirement item 5 needs no new Settings work. Do not change Settings.

Not in scope: tabs for copilot/zcode/others; changes to All agents, tray, widget, notifications; real provider quotas;
any chart or history on the Overview tab.

## H1.1 — `OverviewSnapshotBuilder` (Presentation.Wpf/ViewModels, new)

`public static ProviderSnapshot? Build(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError,
IReadOnlyDictionary<string, decimal> budgets, DateOnly today, DateTimeOffset now, TimeZoneInfo zone)`

`public const string ProviderId = "overview";`

States (same as R-015 E10/E23):
1. `lastGood == null && lastError == null` → `null` (the tab keeps its initial "Checking..." state).
2. `lastGood == null && lastError != null` → `configured: true`, `error: AgentUsagePresenter.ErrorText(kind, message)`,
   no meters, `fetchedAt: now`. For NotInstalled this is the R-014 install guidance.
3. `lastGood != null` → `configured: true`, `fetchedAt: lastGood.RetrievedAt`, `history: null`, meters as below.
   `lastError` is ignored when `lastGood` exists (stale-but-good wins, as for OpenCode).

Meters (state 3):
- Agents = the `Agents` of the `report.Monthly` entry whose `Year`/`Month` equal `today`'s. No such month, or no agents in
  it → zero meters.
- One meter per agent, reading = `AgentBudget.Compute(lastGood, agent, budgets, today)`.
- Order: `MonthCostUsd` descending; ties by agent name `StringComparer.Ordinal` ascending.
- `kind: "month_budget"`, `key: "agent:" + agent` (agent as normalised by `AgentUsage`, i.e. lower-case),
  `title: agent` (the ccusage name as-is), `percent: reading.Percent` (null when no budget → card shows no %),
  `resetsAt`: first day of next local month 00:00 in `zone` (same helper/semantics as R-015 E1),
  `detail: spent`, `subtitle`: identical texts to R-015 E26 —
  `"{spent} of {Money(budget)} budget"` or `"{spent} · No budget set — add one in Settings"`, where
  `spent = ChartFormat.Money((double)MonthCostUsd)` plus `" + unpriced"` when `HasUnpricedModels`.
- Agents in the report but **not** in the current month (e.g. last month only) get no meter.

## H1.2 — Overview tab view model

- `MainWindowViewModel.OverviewTab = new ProviderTabViewModel(OverviewSnapshotBuilder.ProviderId, "Overview")`.
- `Tabs = [ClaudeTab, OpenAiTab, AgyTab, OpenCodeTab, OverviewTab, AllAgentsTab]`. `ProviderTabs` (tray) unchanged.
- Rebuilt at every point where the OpenCode tab is rebuilt from the cached pair (ccusage update, `ApplyAgentBudgets`
  including the E29 short-cut semantics, view rebuilds are NOT needed since Overview has no chart).
- `ProviderTabViewModel` for `"overview"`: no setup card (like OpenCode, E24 — extend that exemption to "overview"),
  no ccusage supplementary section (`SupportsAgentUsage` stays false for it), no chart.
- Empty state: state 3 with zero meters shows the text **"No agent usage recorded this month"** (an existing empty-meter
  message mechanism if there is one; otherwise a minimal one scoped to this tab). It must not look stuck.
- `AppSettings.ActiveProvider` accepts `"overview"` so a restart restores the selected tab; `ActiveProviderTabId` (widget)
  keeps returning a `ProviderTabs` provider (unchanged E28 behaviour).

## H1.3 — XAML

The Overview tab uses the existing provider-tab template (gauge cards). No new styles beyond what the empty-state text
needs. Other tabs, tray and widget look the same.

## Oracles (Test Author derives expected values from these, not from code)

Fixture month = today's month 2026-11, `today = 2026-11-10`, zone UTC. Monthly agents for 2026-11:
`claude` priced $12.50; `codex` priced $3.20 + unpriced model; `copilot` $0.00 (usage, zero cost); `newagent` $3.20.
2026-10 has `zcode` $9.99 only. Budgets: `{ "claude": 50, "NewAgent": 10 }`.

- O1 order: claude ($12.50), codex ($3.20), newagent ($3.20; tie → ordinal "codex" < "newagent"), copilot ($0.00).
  zcode absent (not this month).
- O2 claude: percent 25 (12.50/50×100), subtitle `"$12.50 of $50.00 budget"`.
- O3 codex: percent null, subtitle `"$3.20 + unpriced · No budget set — add one in Settings"`.
- O4 newagent: budget found case-insensitively ("NewAgent"), percent 32, subtitle `"$3.20 of $10.00 budget"` —
  proves a brand-new agent name gets a gauge with no code change.
- O5 copilot: percent null, subtitle `"$0.00 · No budget set — add one in Settings"`.
- O6 `resetsAt` = 2026-12-01T00:00:00+00:00 for every meter; `fetchedAt` = `lastGood.RetrievedAt`.
- O7 states 1/2 as above; NotInstalled error text equals `AgentUsagePresenter.ErrorText(NotInstalled, msg)`.
- O8 report with no 2026-11 month → zero meters, tab shows "No agent usage recorded this month".
- O9 `Tabs` order is Claude, OpenAI, AGY, OpenCode, Overview, All agents; `ProviderTabs` unchanged; existing tab tests
  still pass.
- O10 `ApplyAgentBudgets` with a new budget for codex moves the codex gauge to a percent without a ccusage read.
- O11 (I6) budgets for `zcode` (2026-10 only) and `ghost` (absent) create no meter.

## Coding Owner decisions after Inspector A (Codex GPT-6-Sol, 2026-11-10)

| Id | Decision | Owner |
|---|---|---|
| I1 | (IA blocker 1) The **Monthly section is authoritative**, as for the R-015 OpenCode gauge. It comes from the same `ccusage daily --json --by-agent --sections daily,monthly,session` run, and ccusage builds it from the same daily rows. The acceptance check compares with that output's `monthly` entry for this month. Daily rows are not read by the Overview. | WO + TA |
| I2 | (IA major 2) Duplicate agent names within the month (any case) produce **one** meter. Its name is the normalised lower-case name and its values are those of the first row, i.e. exactly what `AgentBudget.Compute` returns. Never throw. Oracle: rows `Codex` $1.00 then `codex` $2.00 → one meter `codex`, subtitle `"$1.00 · No budget set — add one in Settings"`. | WO + TA |
| I3 | (IA major 3) `ProviderTabViewModel` gets a nullable `EmptyMessage` (plus a visibility flag), set **only for the Overview tab**. With no snapshot yet (state 1) it is `"Checking ccusage..."`. In state 3 with zero meters it is `"No agent usage recorded this month"` (or I4's text). In state 2 it is null and only the error banner shows. Other tabs keep `EmptyMessage` null and look the same. XAML: one text element in the provider template, collapsed when null. | WO + TA |
| I4 | (IA major 4) Staleness: `LastUpdated` already shows `lastGood.RetrievedAt` (F11 style), and a failed refresh keeps the old gauges; that stays. Rollover: if the month of `lastGood.RetrievedAt` (converted to `zone`) differs from `today`'s month and the current month has no entry, the empty message is `"Waiting for this month's ccusage data"` instead of I3's. The display changes only at a rebuild (ccusage update, budget change), not at midnight by itself. | WO + TA |
| I5 | (IA major 5) Overview layout: at most **3 gauge columns**, wrapping to more rows. Other tabs keep their current column rule unchanged. Expose this as a view-model property (e.g. `MeterColumns = min(count, 3)` for Overview, the existing value for other tabs) so it is testable without rendering. | WO + TA |
| I6 | (IA minor 6) Oracle O11: a budget for `zcode` (present only in 2026-10) and for `ghost` (never in the report) creates no meter. | TA |
| I7 | (IA minor 7) O7 concrete: `lastError = AgentUsageUnavailableException(NotInstalled, "ccusage was not found on PATH.")` and `lastGood = null` → `error` equals `AgentUsagePresenter.ErrorText(NotInstalled, "ccusage was not found on PATH.")`, which is that same message per the current presenter, and zero meters. O9 stays as a test requirement. | TA |
| I8 | (IA minor 8) Each rebuild in `MainWindowViewModel` captures `now` once and derives `today` from it in the local zone. That one pair is passed to both the OpenCode and Overview builders. No new clock seam is added to the view model, so the residual midnight risk in wiring tests is accepted (same as R-017 F21): tests capture one day per test. | WO + TA |
| I9 | (CO, after WO2 5167681) Two existing tests pin the old 5-tab order and now fail as O9 requires: `MainWindowViewModelOpenCodeTabTests.Tabs_Q21_OpenCodeTabSitsBetweenAgyAndAllAgents_ProviderTabsUnchanged` and `MainWindowViewModelAllAgentsTests.Tabs_ExposesThreeProviderTabsThenAllAgentsTab_ProviderTabsUnchanged`. The Test Author updates them to the 6-tab order (Overview at index 4) and keeps their `ProviderTabs` assertions. | TA |
| I10 | (CO) Empty-message texts are exactly as written in this spec, ASCII: `"Checking ccusage..."` (three dots, not U+2026), `"No agent usage recorded this month"`, `"Waiting for this month's ccusage data"` (ASCII apostrophe). WO2's 5167681 uses U+2026 for the first one; fix it. | WO2 + TA |

## Coding Owner decisions after Reviewer1 (Claude Code · Fable 5.1, 2026-11-10, at 5167681, verdict approve with minors)

| Id | Decision | Owner |
|---|---|---|
| I11 | (R1 M1) When Overview gauges wrap to more than one row, the rows get a vertical gap equal to the existing 12 px horizontal gap. Other tabs (always one row) must look pixel-identical, so do not change their margins: scope the change to the wrapped/Overview case. | WO2 |
| I12 | (R1 m1) The I4 rollover condition is computed in `OverviewSnapshotBuilder` from its own `today` and `zone`. It applies only in state 3, and its trigger is exactly "the report has no `Monthly` entry for today's month AND the month of `lastGood.RetrievedAt` converted to `zone` differs from today's month". A current-month entry with zero agents shows "No agent usage recorded this month". The builder exposes the resulting message, for example `public static string? EmptyMessage(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError, DateOnly today, TimeZoneInfo zone)` returning the I3/I4/I10 text or null. `MainWindowViewModel` hands it to the Overview tab after `UpdateFromSnapshot`. `ProviderTabViewModel` no longer reads `TimeZoneInfo.Local` or the clock for this. | WO2 + TA |
| I13 | (R1 m2) The Overview tab's behaviour derives from `ProviderId == "overview"` inside `ProviderTabViewModel`: initial `EmptyMessage = "Checking ccusage..."` and a `MeterColumns` cap of 3, so a directly constructed `new ProviderTabViewModel("overview", "Overview")` behaves the same. `EmptyMessage` gets a non-public setter; the I12 hand-off goes through a method that ignores non-Overview tabs. | WO2 + TA |
| I14 | (R1 m3) The Overview tab caption is always plain `"Overview"`, never `"Overview NN%"`. | WO2 + TA |
| I15 | (R1 m4) Do not rebuild Overview on chart range/metric changes; it rebuilds only on a ccusage update and on budget changes (H1.2, I4). | WO2 + TA |
| I16 | (R1 m5) Comments use ASCII "..." too (tidy with I10). | WO2 |

## Coding Owner decisions after Reviewer2 (Codex GPT-6.1-Sol High, 2026-11-10, at c02da4e, verdict request changes)

| Id | Decision | Owner |
|---|---|---|
| I17 | (R2 major 1; spec gap in I12) `OverviewSnapshotBuilder.EmptyMessage` returns **null whenever state 3 produces at least one meter**. Full table: state 1 → `"Checking ccusage..."`; state 2 → null; state 3 with ≥1 meter → null; state 3 with a current-month entry and zero agents → `"No agent usage recorded this month"`; state 3 with no current-month entry → `"Waiting for this month's ccusage data"` when the month of `RetrievedAt` (in `zone`) differs from today's month, otherwise `"No agent usage recorded this month"`. Oracle: the O1 November fixture → null. | WO2 + TA |
| I18 | (R2 minor 2) The wrapped-row gap must actually render. Move the card's default `Margin="0,0,12,0"` from the local attribute into the style (a `Setter`), so the trigger can override it. When wrapping, use `0,0,12,12` (a gap *below* each card, so the first row does not move down). Single-row tabs keep `0,0,12,0` exactly. | WO2 |

## Coding Owner decisions after Inspector B (Codex GPT-6-Sol, 2026-11-10, at f0dac34)

| Id | Decision | Owner |
|---|---|---|
| I19 | (IB major 1) The two I17 regression tests must run un-skipped before acceptance; this is already the Test Author's round 3. | TA |
| I20 | (IB major 2) Add a negative I1 test: a report whose `Monthly` has no current-month entry but whose `Daily` has current-month rows for some agent → zero meters (and the I17 "Waiting.../No agent usage" text as the table says). This kills a builder that also reads Daily. | TA |
| I21 | (IB minor 3) I18's rendered gap is not unit-tested. It becomes a user acceptance check on the running app: Overview with at least 4 agents shows a visible gap between the gauge rows, and the other tabs' single-row cards look unchanged. | user acceptance |
| — | Inspector B could not execute mutants (the Codex sandbox cannot restore NuGet). Mutant evidence for R-016 is the Test Author's 3 executed mutants plus Inspector B's 19 read-only predictions; I20 closes the one predicted survivor. | — |

