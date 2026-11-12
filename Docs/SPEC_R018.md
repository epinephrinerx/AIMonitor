# SPEC R-018 — gauge captions fit the card; AGY tab gets ccusage `antigravity` stats and daily chart

Owner request (2026-11-11): "แก้ส่วนที่แจ้งได้เลยครับ" — the two differences listed in `Docs/STATUS_2026-11-11.md` §3.
Requirement: `Requirements.md` R-018 (✅ 2026-11-11). Base: `8ed17af` on `agent/r002-phase11-settings`.

## Facts (Coding Owner, read at 8ed17af)

- `Controls/GaugeControl.cs` non-compact path (`OnRender`, ~:209-287) draws the caption (`FormatCaptionText(Title, …)`)
  and `Subtitle` with `FormattedText` without `MaxTextWidth`, `MaxLineCount` or `Trimming`. Long text paints into the
  neighbouring card (seen at 1120 px with four AGY meters). The compact/widget path (`RenderCompact`, ~:355-392) already
  sets `MaxTextWidth`, `MaxLineCount = 1`, `CharacterEllipsis`.
- `AgyQuotaClient` returns meters only — no `Stats`, no `History`. The only token source for AGY is the ccusage agent
  `antigravity` (R-014). `MainWindowViewModel` already passes its summary rows (`AgyTab.SetAgentUsage(… "antigravity" …)`).
- `OpenCodeHistoryBuilder` builds a `UsageHistory` from the cached ccusage report for agent `opencode` only.

## Changes

| Id | Where | Requirement |
|---|---|---|
| G1 | `GaugeControl.cs` (non-compact path only) | The caption is laid out with `MaxTextWidth = ActualWidth − 8` (never below 1), `TextAlignment = Center`, `MaxLineCount = 2`, `Trimming = CharacterEllipsis`, drawn at x = 4 (centred by the alignment). Same font, size, brush and top position (`center.Y + radius * 0.45`) as now. |
| G2 | same | The subtitle uses `MaxTextWidth = ActualWidth − 8`, `TextAlignment = Center`, `MaxLineCount = 1`, `CharacterEllipsis`, same bottom position. If the caption's bottom would overlap the subtitle's top, the caption is reduced to 1 line (still trimmed). |
| G3 | same | Put the width/line decision in a pure static helper (e.g. `ComputeDashboardText(double width, double height, double captionLineHeight, double subtitleHeight, bool hasSubtitle)` returning max text width and caption max lines) so it is unit-testable without rendering. The compact path is unchanged. A caption that fits on one line renders exactly as before (same position and size). |
| A1 | new `ViewModels/AgentHistoryBuilder.cs` | Generalise `OpenCodeHistoryBuilder.Build` to `AgentHistoryBuilder.Build(report, agentId, today, rangeDays, metric)` with identical behaviour; `OpenCodeHistoryBuilder.Build` delegates to it with `"opencode"` (public API and existing tests unchanged). |
| A2 | new `ViewModels/AgyUsageComposer.cs` (pure) | `Compose(ProviderSnapshot agy, AgentUsageReport? report, DateOnly today, int rangeDays, string metric)` returns a copy of `agy` (every field kept) with `History = AgentHistoryBuilder.Build(report, "antigravity", …)` and `Stats` = the agy stats followed by four stats over the same range: "Total tokens", "Output tokens", "Cache reads" (sum of `CacheRead`), each formatted like the existing token stats/chart labels, and "Equivalent API value" (`ChartFormat.Money`, plus " + unpriced" when any model in range has no cost). When `report` is null, or the agent has no rows in the range, return `agy` unchanged (no stats, no history — today's look). Only when `agy.History` is null; if the agy snapshot already has history, keep it. |
| A3 | `MainWindowViewModel.cs` | Keep the latest raw agy snapshot. Push `AgyUsageComposer.Compose(...)` to `AgyTab.UpdateFromSnapshot` (a) where the coordinator result is applied (`result.Snapshots["agy"]`) and (b) in `OnAgentUsageUpdated` when a raw agy snapshot exists, using the same `now/today` pair (I8). Never start a ccusage read or an agy refresh for this. Chart range/metric changes follow the existing refresh path. |
| A4 | `ProviderTabViewModel` | Check `IsSupplementaryStat` and the history/stat visibility rules so the four new stats and the chart show on the AGY tab; do not change Claude/OpenAI/OpenCode/Overview behaviour. |
| E1 | `tools/AIMonitor.ScreenEvidence/Program.cs` | Add agent `antigravity` to the V6 recipe of `SPEC_R005_EVIDENCE.md` (Model C, weight 1, priced like Model A). Render `dashboard-agy-{theme}` (and only these) from the agent-usage view model so the paired AGY screens show the stats row and chart; the V5 readiness gate expects the Overview tab to have 5 meters and the AGY tab to have history. Re-render and regenerate `Docs/PARITY_ACCEPTANCE.md` only if its generated text changes (it holds no owner scores). |

## Tests (Test Author, from this spec)

- G3 helper: wide card → 2 lines allowed; caption + subtitle that would overlap → 1 line; width ≤ 8 → max width 1;
  no subtitle → 2 lines; one-line captions unaffected.
- A1: `AgentHistoryBuilder` with `"antigravity"` ignores other agents; `OpenCodeHistoryBuilder` results unchanged.
- A2: oracles by hand — e.g. two days of `antigravity` rows (tokens and cost given in the test), range 7 → stats values,
  " + unpriced" when one model has null cost, null report → same snapshot instance or equal fields, agent missing →
  unchanged, existing agy meters/account/error/detection preserved, existing agy history kept.
- A3: view-model test with an in-memory agent-usage source and a fake coordinator: AGY tab `HasHistory` true and stat
  tiles present after both orders (agy first then ccusage, ccusage first then agy); no extra ccusage/agy calls.
- No real ccusage, agy, network or clock; dates from the test.

## Verification (Writing Owner)

`dotnet build AIMonitor.sln -c Release` (Release: the owner's Debug app may be running) · `dotnet test AIMonitor.sln -c Release --no-build` ·
`dotnet format AIMonitor.sln --verify-no-changes` · ScreenEvidence build/run per `SPEC_R005_EVIDENCE.md` → 46 screens ·
`python scripts/build_parity_report.py` · `python -m unittest discover -s tests/verification -v`. Look at
`dashboard-agy-light.png` and report what it shows.

## Not included

AGY quota source unchanged; widget/tray unchanged; no new tab; no change to Claude/OpenAI/OpenCode data.

## Coding Owner decisions after Inspector A (Codex · GPT-6-Sol, at b3d03b6; each approved by the owner 2026-11-11)

| Id | Decision | Owner |
|---|---|---|
| D1 | (IA 1) The chart range/metric setters recompose AGY synchronously in `RebuildOpenCodeTabForViewChange` (rename allowed) from the raw agy snapshot and the cached report, with one `now/today` pair; Overview stays excluded (R-016 I15). Test direct selection and settings-driven changes. | WO + TA |
| D2 | (IA 2) ScreenEvidence: the first loop skips `dashboard-agy-*`; the agent-usage view model captures `dashboard-agy-{theme}` once per theme. Before those captures the readiness gate requires OpenCode meter, Overview `Meters.Count == 5`, All agents sessions, AGY `HasHistory` and the four new AGY stat tiles; timeout and midnight checks as V5/V7/V11. Total stays 46 screens. | WO |
| D3 | (IA 3) In `scripts/build_parity_report.py` replace the exception "Paired dashboard screens are rendered without ccusage; …" with "Paired Claude and OpenAI dashboards are rendered without ccusage, so the ccusage rows that R-014 adds under existing gauges are not in the paired evidence; paired Gemini / AGY dashboards use synthetic `antigravity` ccusage data (R-018)." Before regenerating, confirm `Docs/PARITY_ACCEPTANCE.md` equals the generated baseline at the base commit (no owner scores); keep the guard. Update the matching V3.3/V12 test oracle in `tests/verification/test_parity_report.py` (Test Author). | WO + TA |
| D4 | (IA 4) Range = the inclusive local days `today − (rangeDays − 1) … today` (month boundaries do not matter). Token tiles sum the agent-level `AgentUsage.Tokens` of `antigravity` daily rows in range ("Total tokens" = Total, "Output tokens" = Output, "Cache reads" = CacheRead); the chart uses models (A1). Stats and history are added only when at least one `antigravity` daily row lies in range; otherwise the agy snapshot is returned unchanged. A row without models counts in the tiles and shows an empty chart day. | WO + TA |
| D5 | (IA 5) When `antigravity` data exists, the stats row and chart show also when the agy snapshot is unconfigured or has an error (setup card / error banner stay as they are). Quota meters, account, error, detection, setup hint are always preserved. | WO + TA |
| D6 | (IA 6) If AGY is disabled in Settings, drop the raw agy snapshot and never compose for it. An AGY-only re-detect recomposes with the cached report without starting ccusage (test: changed quota snapshot, unchanged ccusage call count). | WO + TA |
| D7 | (IA 7) "Equivalent API value" tile: no models in range → `$0.00`; all models unpriced → `Unknown price`; priced and unpriced → `$X.XX + unpriced`; priced only → `$X.XX` (`ChartFormat.Money`). `Stat.Detail` = "ccusage · equivalent API price". | WO + TA |
| D8 | (IA 8) `captionTop = center.Y + radius * 0.45` (unchanged). Overlap when `captionTop + lines × captionLineHeight > height − subtitleHeight − 4` (only when a subtitle is drawn). Two lines allowed unless they overlap; then one line; never hide caption or subtitle. A caption that fits on one line draws at exactly the current point (`center.X − width/2`, `captionTop`) — keep the old drawing call for that case. Tests use the pure helper. | WO + TA |
| D9 | (IA 9) `OnAgentUsageUpdated` work that touches tabs runs on the UI thread (post through the captured dispatcher/synchronization context when present; inline when none, as in tests). When ccusage data was composed, the AGY `ValueNote` becomes "Quota from AGY CLI · tokens and cost from ccusage"; otherwise the client's note is kept. | WO + TA |

Test oracles (Inspector A, hand-computed): today 2026-05-01; Apr 30 `antigravity` Model A 10,000 total / 500 output / 8,500 cache reads / $0.50; May 1 Model A 20,000 / 1,000 / 17,000 / $1.00 and Model X 10,000 / 500 / 8,500 / null. Range 7 → 40K / 2,000 / 34K / `$1.50 + unpriced`, seven buckets (Apr 30 = 10K, May 1 = 30K, five empty). Range 1 (May 1 only) → 30K / 1,500 / 25.5K / `$1.00 + unpriced`. E1 Model C weight 1 over the V6 factors (Σk = 28) → 280K / 14K / 238K / $14.00; Overview gains exactly one meter.

## Coding Owner decisions after the first trial merge (59231cd = 99e4d44 + 80a7b7f)

| Id | Decision | Owner |
|---|---|---|
| D10 | A2's last sentence is literal: compose (stats, history, note) only when `agy.History` is null; an agy snapshot that already has history is returned unchanged. | TA |
| D11 | Test-side fixes (the code is correct): compare `UsageHistory` contents, not record equality; update the C9 mapping oracle to the four added tiles; D1 setter test must not count the existing follow-up refresh's agy call; D1 settings test must pump the settings sync the way the existing R-017 wiring tests do; D6 must assert "no compose" by content/flag, not `Assert.Same` on rebuilt lists. | TA |

## Coding Owner decisions after Reviewer1/2 (at 59231cd)

| Id | Decision | Owner |
|---|---|---|
| D11a | Correction to D11: `D6_DisablingAgyDropsRawSnapshot…` fails because of the code (Reviewer1 M1, Reviewer2 1), not the test. It is removed from D11's test-side list; the Test Author keeps its intent and may only loosen `Assert.Same` if it still fails after D13. | CO |
| D12 | (Reviewer1 m1, approved by the owner 2026-11-11) The D8 overlap gap is 2 px instead of 4: overlap when `captionTop + lines × captionLineHeight > height − subtitleHeight − 2`, so long captions take two lines on the 190 px dashboard gauge. Everything else in D8 stays. | WO + TA |
| D13 | (Reviewer1 M1 + m3 + m5, Reviewer2 1) Check the provider id `"agy"` (not the agent id) when dropping the raw snapshot; `UpdateAgyTab` itself returns without composing when AGY is disabled in the current settings; call `UpdateAgyTab` after `UpdateOpenCodeTab`/`UpdateOverviewTab` in `OnAgentUsageUpdated` so an AGY failure cannot stop them; share the gauge geometry constants (0.48, 0.38, 0.45) between `OnRender` and `ComputeDashboardText`. m2 (extra AGY redraws) is accepted. | WO |
| D14 | (Inspector B on 90b5091, owner decisions 2026-11-12 on items 1–4; 5–6 Coding Owner defaults after "ทำต่อเลยครับ") Test Author round 3: (1) D9 — test an off-thread `AgentUsageRefresher` update under a controlled `SynchronizationContext` and assert the tab changes happen only after that context drains; if it confirms the handler runs off-thread, Writing Owner 1 posts `OnAgentUsageUpdated` work to the context captured by the view model. (2) D6 — disable AGY, re-enable without a new AGY result, trigger a cached-report update, assert the stale quota is not recomposed. (3) D13 — a controlled AGY composition failure leaves OpenCode and Overview already updated. (4) D2/E1 — no new test; the Coding Owner checks 46 manifest entries, one AGY dashboard per theme, history readiness and the Model C totals when re-rendering evidence. (5) Caption ellipsis / one-line subtitle — residual risk, checked by eye on the re-rendered evidence; no production seam. (6) Replace `DateTime.Now` and `Task.Yield()` settling in the changed view-model tests with a fixed clock and an explicit drain where the view model allows; report where it does not. | TA (+ WO1 if 1 confirms) |
| D15 | (Test Author round 3, 5b488ab, confirmed D14(1)) Writing Owner 1: `OnAgentUsageUpdated` tab work runs on the `SynchronizationContext` captured when `MainWindowViewModel` was built; when the update already arrives on that context (or none was captured) it runs inline, otherwise it is posted there and the handler's task completes only after the posted work finished, so `await vm.AgentUsageTask` still means "tab work done" and the pre-existing OpenCode/Overview/chart-wiring/budgets/All-agents tests stay green unchanged. D14(6) `DateTime.Now` stays (no clock seam in MainWindowViewModel; residual risk only across local midnight). | WO1 |
| D16 | (Reviewer1 M1–M4, Reviewer2 1–4, Inspector B round 2 findings 1–4 on 749bc6f; owner decisions 2026-11-12) Replaces the D15 mechanism. Writing Owner 3 (WO1 is over its usage limit): remove `_agentUsageEntryContext` and the posted branch in `OnAgentUsageUpdated`; when `RefreshAsync` starts the agent-usage refresh with `SynchronizationContext.Current` null and a context captured at construction, pass that context to `AgentUsageRefresher` for that call (explicit parameter, per call, no shared field), so the refresher's existing post, completion signal and handler catch apply: tab work runs on the view model's context, `AgentUsageTask` completes only after it, and handler exceptions stay contained. A refresh entered on any context keeps today's behaviour. Overlapping refreshes and `_pendingForce` re-runs must deliver on the view model's context too. Test Author: the D9 test asserts `AgentUsageTask` is not complete before `Drain()` and the tabs are updated once it completes; add a foreign-context delivery test, an overlapping bare + UI refresh test, and a posted-exception test (contained, task completes, no exception from `Drain()`); `MainWindowViewModelAllAgentsTests` line 222 must pass when run alone. D13 keeps its negative-cost seam with a comment that it deliberately bypasses the ccusage parser (the view model must not rely on parser validation). | WO3 + TA |
