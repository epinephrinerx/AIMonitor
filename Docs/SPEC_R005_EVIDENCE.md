# SPEC R-005 V — synthetic screen evidence and parity table refresh

Owner request (2026-11-11): "ทำเลยทั้งสามข้อครับ" (finish the remaining R-005 criteria).
This spec covers R-005 criterion 4: "มี synthetic screen evidence และตารางเกณฑ์ parity พร้อมระบุรายการที่ยังไม่ได้ทดสอบ".
Base: `a46f391` on `agent/r002-phase11-settings`. Tooling only; no production code under `src/` changes.

## Why

- The fixture `tests/Fixtures/Parity/screens.json` switched its third provider from `gemini` to `agy` in 2.0.1 (R-012).
  `scripts/render_legacy_screens.py` now fails: `KeyError: 'gemini'` at the `ConnectDialog` loop, because 1.3.3 still
  has a Gemini provider. The legacy renders in `artifacts/verification/legacy` date from 2026-10-15.
- The WPF renders name the third tab `agy`, so `scripts/build_parity_report.py` (which pairs identical names) no longer
  pairs `dashboard-agy-*` / `connect-agy-*` with 1.3.3's `dashboard-gemini-*` / `connect-gemini-*`.
- The tabs OpenCode, Overview and All agents (R-014 to R-017, accepted 2026-11-10) have no synthetic render.
- `Docs/PARITY_ACCEPTANCE.md` names `STATUS_2026-10-14.md` and lists no 2.0-only screens or new exceptions.

## Changes

| Id | File | Requirement |
|---|---|---|
| V1 | `scripts/render_legacy_screens.py` | Treat the fixture provider `agy` as the data for 1.3.3's `gemini` provider (R-012 replaced Gemini with AGY). Build the snapshot dictionary keyed by the 1.3.3 id through one explicit mapping `{"agy": "gemini"}` (ids without a mapping stay as they are); the `ProviderSnapshot` id and the `Detection` provider id use the 1.3.3 id; meters come from the fixture's `agy` entry. Output names stay 1.3.3 names (`dashboard-gemini-*`, `connect-gemini-*`). No other behaviour changes. |
| V2 | `tools/AIMonitor.ScreenEvidence/Program.cs` | Add three 2.0-only captures per theme: `dashboard-opencode-{theme}`, `dashboard-overview-{theme}`, `dashboard-all-agents-{theme}`, added to the manifest. Render them from a **second** `MainWindowViewModel` + `MainWindow` that receives a `CachedAgentUsageSource` wrapping an in-memory `IAgentUsageSource`. The existing 40 captures keep using the current view model without agent usage, so their content does not change (the state of a machine without ccusage). |
| V2.1 | same | The in-memory source returns an `AgentUsageReport` built in code (no file, no process, no `CcUsageRunner`, no network). Dates are relative to the local date at render time so the Overview "this month" logic shows meters: daily rows for the 7 days ending today and one `Monthly` row set for the current month. Agents: `claude`, `codex`, `opencode`, `copilot`. Models are synthetic (`Model A`, `Model B`, plus one unpriced model `Model X` on `opencode` listed in `UnpricedModels`). Include a few sessions. Values are round, plausible numbers. |
| V2.2 | same | Settings for the second view model set `AgentBudgets` to `opencode = 50` and `claude = 200`; `codex` and `copilot` have no budget (gauge shows money without %). Same theme, size (1120×820) and status text as the existing dashboard captures. Select tabs through `vm.Tabs` (OpenCode, Overview, All agents). The second window is closed/exited the same way as the first. |
| V3 | `scripts/build_parity_report.py` | **Counterpart pairs.** Besides identical names, pair a WPF screen whose name contains `-agy-` with the legacy screen of the same name with `-gemini-` (only when both exist). Pair label `dashboard-agy ↔ dashboard-gemini`; the index page shows `legacy/<legacy name>.png` beside `wpf/<wpf name>.png`. Tray pairs stay as they are. |
| V3.1 | same | **2.0-only screens.** Every WPF manifest screen without a legacy counterpart is listed, not scored, in `index.html` (section "2.0 only — not in 1.3.3, not scored for parity", image shown) and in `PARITY_ACCEPTANCE.md` (table "2.0-only screens" with columns Screen · Reason). Reasons: `supplementary-*` → "R-010/R-011 supplementary details"; `dashboard-opencode-*`, `dashboard-overview-*`, `dashboard-all-agents-*` → "R-014–R-017 ccusage tabs (owner accepted 2026-11-10)"; anything else → "No 1.3.3 counterpart; owner decision pending". |
| V3.2 | same | **Missing in 2.0.** Every legacy manifest screen without a WPF counterpart appears in the visual table with score "Pending" and note "No 2.0 render". (With V1+V2 this list is expected to be empty.) |
| V3.3 | same | Replace `STATUS_2026-10-14.md` with `STATUS_2026-11-11.md` in the generated text. Add exception bullets (keep the existing ones): "Gemini tab renamed Gemini / AGY and backed by AGY CLI pools (R-012, R-013; owner accepted 2026-10-16)."; "The dashboard tab row adds OpenCode, Overview and All agents (R-014–R-017; owner accepted 2026-11-10); paired screens show the extra tab headers."; "Paired dashboard screens are rendered without ccusage; the ccusage rows that R-014 adds under existing gauges are not in the paired evidence." Keep the 36 functional areas, the five visual criteria and the owner-score guard unchanged. |
| V4 | `tests/verification/test_parity_report.py` | (Test Author) Pure-Python tests on synthetic manifests in a temporary directory under `artifacts/verification/test-fixtures` (pattern of the existing test): agy↔gemini pairing, identical-name pairing, a 2.0-only screen listed with the right reason and not counted as a pair, a legacy-only screen listed as "No 2.0 render", and the existing owner-score preservation test still passing. No PySide6, no dotnet, no network. |

## Regeneration step

`Docs/PARITY_ACCEPTANCE.md` currently holds no owner data: 144 rows, every result "Pending" (checked by the Coding Owner
at `a46f391`). Because the generated text changes, the guard would refuse; the Writing Owner deletes that file and lets
the generator recreate it. This is allowed only because no owner data exists; do not weaken the guard.

## Verification (Writing Owner, in this order, one command at a time)

1. `dotnet build tools/AIMonitor.ScreenEvidence -c Release` — **Release only**: the owner's Debug app is running and
   locks the Debug output of `AIMonitor.Presentation.Wpf`.
2. `dotnet run -c Release --no-build --project tools/AIMonitor.ScreenEvidence -- tests/Fixtures/Parity/screens.json artifacts/verification/wpf`
   → 46 screens (40 + 6).
3. `python scripts/render_legacy_screens.py D:/Dev/Apps/AIMonitor tests/Fixtures/Parity/screens.json artifacts/verification/legacy`
   → 36 screens, exit 0. Read-only use of `D:/Dev/Apps/AIMonitor`; never write there.
4. Delete `Docs/PARITY_ACCEPTANCE.md`, then `python scripts/build_parity_report.py` → 38 pairs (36 + 2 tray).
5. `python -m unittest discover -s tests/verification -v`.
6. Look at the six new PNGs and one agy↔gemini pair; report what each shows.

No real ccusage, credentials, network or startup registration. Do not touch `src/`, `Requirements.md` or other docs.

## Acceptance (R-005 criterion 4)

- Both renderers run on the current fixture; the report pairs 36 screens + 2 tray icons, lists 2.0-only screens with
  reasons and lists untested evidence.
- Owner scoring (R-004) stays pending; nothing here scores or certifies parity.
