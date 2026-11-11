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
