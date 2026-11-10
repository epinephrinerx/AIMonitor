# SPEC R-017 — OpenCode per-model token chart with a cost line (Coding Owner, 2026-11-10)

Requirement: `Requirements.md` R-017 (✅ 2026-11-10). Builds on R-015 (OpenCode tab, `OpenCodeSnapshotBuilder`) and
R-014 (ccusage report). Data comes only from the ccusage report already held by `MainWindowViewModel` (stored
`lastGood`, decision E2 of SPEC_R015). No new ccusage run, no OpenCode log reading, no network.

## Phase and owners

One phase **G1**. Writing Owner 3 (opencode, deepseek-v4.1-flash), the R-015 Q2 author. Test Author (qwen3.8-flash) in
parallel. File ownership:
`src/AIMonitor.Domain/UsageHistory.cs` (additive), new `src/AIMonitor.Presentation.Wpf/ViewModels/OpenCodeHistoryBuilder.cs`,
`OpenCodeSnapshotBuilder.cs`, `MainWindowViewModel.cs`, `ProviderTabViewModel.cs`,
`src/AIMonitor.Presentation.Wpf/Controls/StackedColumnChartControl.cs`, `MainWindow.xaml` (one binding).

## G1.1 Domain — optional daily costs on `UsageHistory`

- New `public sealed record DailyCost(DateOnly Day, double PricedUsd, bool HasUnpricedModels)`; `PricedUsd` must be finite
  and `>= 0` (same guard as bucket values).
- `UsageHistory` gets an optional constructor parameter `IReadOnlyList<DailyCost>? dailyCosts = null` (last parameter) and a
  property `IReadOnlyList<DailyCost> DailyCosts` (empty when null). Existing callers are unchanged.

## G1.2 `OpenCodeHistoryBuilder.Build(report, today, rangeDays, metric)` → `UsageHistory?` (Presentation, pure)

- `report == null` → `null`.
- Days: the `rangeDays` local dates ending at `today` inclusive, oldest first (`today.AddDays(-(rangeDays-1))` … `today`).
  One bucket per day, also for days without data (empty `PerModel`).
- Source rows: `report.Daily` entries whose `Date` is in that window; inside each, the `AgentUsage` with `Agent == "opencode"`
  (case-insensitive); its `Models`.
- Value per model per day by `metric`:
  - `"Total tokens"` → `ModelUsage.Tokens.Total`;
  - `"Output tokens"` → `ModelUsage.Tokens.Output`;
  - `"Equivalent value"` → `(double)ModelUsage.CostUsd` when priced; an unpriced model contributes nothing.
  - Any other metric string → treated as `"Total tokens"`.
  A model with value 0 on a day is left out of that day's `PerModel`.
- `Series` = models with a total `> 0` over the window, ordered by that total descending, ties by `StringComparer.Ordinal`.
- `ByModel` = the same models and totals, same order, as `UsageHistoryBreakdown(model, total)`.
- `ByProject` = empty. `Days = rangeDays`. `Metric = metric` (as normalised above). `ProjectLabel` default.
- `DailyCosts` = one `DailyCost` per window day, oldest first: `PricedUsd` = sum of `(double)CostUsd` of priced opencode
  models that day (0 when none); `HasUnpricedModels` = any opencode model that day has `CostUsd == null`.
- No usage in the window → buckets all empty, `Series`/`ByModel` empty, `DailyCosts` all zero (the history is still returned).

## G1.3 Wiring

- `OpenCodeSnapshotBuilder.Build(...)` gains parameters `int rangeDays, string metric` and, when `lastGood != null`, sets
  `history: OpenCodeHistoryBuilder.Build(lastGood, today, rangeDays, metric)` on the snapshot. Error-only and "no result"
  states (E10/E23) are unchanged (no history).
- `MainWindowViewModel` passes `ChartRangeDays` and the selected metric value, and rebuilds the OpenCode tab (as in E2,
  without running ccusage) whenever `SelectedRange` or `SelectedMetric` changes, in addition to the existing triggers.
  E29's equal-budgets short-cut stays for budget changes only.
- `ProviderTabViewModel.UpdateFromSnapshot` maps `history.DailyCosts` to a new property
  `IReadOnlyList<DailyCost> ChartCosts` (empty when no history), aligned with `ChartBuckets` by `Day` (only the days present
  in `ChartBuckets`, same order). Other providers get an empty list.
- `MainWindow.xaml`: the existing "Usage per day" `StackedColumnChartControl` binds `Costs="{Binding SelectedTab.ChartCosts}"`.
  No other layout change: the OpenCode tab uses the existing chart card and "By model" card (shown by the existing
  `HasHistory` / `HasModelRows` rules); the "By project" card stays hidden because `ByProject` is empty.

## G1.4 Chart control — optional cost line

- New dependency property `Costs` (`IReadOnlyList<DailyCost>?`, `AffectsRender`). Null or empty → the control renders
  **exactly as today** (no line, no right axis, no extra legend entry, same layout and tooltip).
- When non-empty and at least one `PricedUsd > 0`:
  - a polyline through the centre of each day's column at `PricedUsd`, scaled to its own right-hand axis whose maximum is
    `ChartFormat.NiceAxisMax(max PricedUsd, divisions)` with the same number of divisions as the left axis; right axis tick
    labels use `ChartFormat.Money`; a dot at each point;
  - a days with `HasUnpricedModels` draws its dot hollow;
  - legend gets one extra entry `"Cost ($)"` after the model series;
  - the hover tooltip for a day adds a line `Cost: {ChartFormat.Money(PricedUsd)}` plus `" + unpriced"` when
    `HasUnpricedModels`.
- When all `PricedUsd == 0` → no line and no right axis (but tooltip still shows `Cost: $0.00` for days with costs entries).
- Line colour: a theme brush that is not one of the series colours (e.g. the text-primary brush); works in light and dark.

## Out of scope
Cost line for Claude/Codex/AGY/All agents; "By project" for OpenCode; tray/widget; reading OpenCode logs.

## Oracles

Fixture (today 2026-11-10, rangeDays 7 → window 2026-11-04 … 2026-11-10). `report.Daily`:
- 2026-11-01 opencode: `kimi-k2.7-code` total 9999, output 99, cost 9.99 (outside the window).
- 2026-11-08 opencode: `kimi-k2.7-code` (input 100, output 50, cacheCreation 0, cacheRead 850 → total 1000) cost 0.50;
  `glm-5.3-flash` total 400, output 40, cost 0.10.
- 2026-11-09 claude only: `claude-opus-5-5` total 5000 (must be ignored).
- 2026-11-10 opencode: `kimi-k2.7-code` total 2000, output 100, cost 1.25; `deepseek-v4.1-flash` total 300, output 30,
  cost null (unpriced).

| Id | Metric | Expected |
|---|---|---|
| H1 | Total tokens | 7 buckets 11-04…11-10; 11-08 `{kimi:1000, glm:400}`; 11-10 `{kimi:2000, deepseek:300}`; others empty. Series `[kimi, glm, deepseek]`; ByModel `kimi 3000, glm 400, deepseek 300` |
| H2 | Output tokens | 11-08 `{kimi:50, glm:40}`; 11-10 `{kimi:100, deepseek:30}`; Series `[kimi, glm, deepseek]` (150, 40, 30) |
| H3 | Equivalent value | 11-08 `{kimi:0.50, glm:0.10}`; 11-10 `{kimi:1.25}` (deepseek left out); Series `[kimi, glm]`; ByModel `kimi 1.75, glm 0.10` |
| H4 | any | DailyCosts: 11-04…11-07 and 11-09 `(0, false)`; 11-08 `(0.60, false)`; 11-10 `(1.25, true)` |
| H5 | `"Bogus"` | same as H1, `Metric == "Total tokens"` |
| H6 | report with no opencode rows in the window | 7 empty buckets, empty Series/ByModel, DailyCosts all `(0, false)` |
| H7 | report null | `null` |
| H8 | rangeDays 1 | one bucket 11-10 `{kimi:2000, deepseek:300}` |
| H9 | chart control with `Costs` null/empty | rendering identical to before (no right axis, no "Cost ($)" legend) |
