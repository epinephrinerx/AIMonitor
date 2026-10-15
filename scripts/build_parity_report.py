"""Generate a local paired-screen review index and the ungraded R-004 checklist."""
from html import escape
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
evidence = ROOT / "artifacts/verification"
wpf = set(json.loads((evidence / "wpf/manifest.json").read_text()))
legacy = set(json.loads((evidence / "legacy/manifest.json").read_text()))
pairs = sorted(wpf & legacy)
for theme in ("light", "dark"):
    name = f"tray-{theme}"
    if (evidence / f"wpf/{name}.png").exists() and (evidence / f"legacy/{name}.png").exists():
        pairs.append(name)
criteria = ["Row structure", "Complete elements", "Text", "Colours and signals", "Proportions and layout"]
rows = []
for name in pairs:
    scoring = "".join(f'<label>{c}<select aria-label="{escape(name)}: {c}" class="score"><option value="">Pending</option><option value="1">Pass</option><option value="0">Fail</option></select></label>' for c in criteria)
    rows.append(f'<section id="{name}"><h2>{escape(name)}</h2><div class="pair"><figure><figcaption>Python reference</figcaption><a href="legacy/{name}.png"><img loading="lazy" src="legacy/{name}.png" alt="Python {name}"></a></figure><figure><figcaption>C# WPF</figcaption><a href="wpf/{name}.png"><img loading="lazy" src="wpf/{name}.png" alt="WPF {name}"></a></figure></div><div class="scoring">{scoring}</div></section>')
html = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>AIMonitor paired screen evidence</title>
<style>body{font:15px/1.5 system-ui;margin:24px;background:#f5f5f3;color:#171717}header{position:sticky;top:0;padding:12px 20px;background:white;border:1px solid #ddd;z-index:1}section{background:white;padding:20px;margin:24px 0;border:1px solid #ddd;border-radius:8px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:16px}figure{margin:0;min-width:0}img{max-width:100%;height:auto;border:1px solid #ddd}figcaption{font-weight:600;margin-bottom:8px}.scoring{display:flex;gap:16px;flex-wrap:wrap;margin-top:16px}label{display:flex;flex-direction:column}select{padding:5px}h2{font-size:18px}@media(max-width:900px){.pair{grid-template-columns:1fr}}</style>
<header><b>R-004 paired client-area renders — owner review</b><div id="total">No owner scores yet.</div></header>
<p>Both renderers use tests/Fixtures/Parity/screens.json. These are production widgets with injected synthetic state; native title bars, tray shell/menu, printing and real DPI are not captured. Documents and version text differ by release. This page does not certify &gt;95% parity or replace the required real-app screenshots. Scores remain only in this page until you close it; no scores are automatically saved or accepted.</p>
''' + "\n".join(rows) + '''<script>for(const e of document.querySelectorAll('.score'))e.addEventListener('change',()=>{const s=[...document.querySelectorAll('.score')];const done=s.filter(x=>x.value!=='');const passed=done.reduce((a,x)=>a+Number(x.value),0);document.querySelector('#total').textContent=done.length===s.length?`Owner selections: ${passed}/${s.length} = ${(100*passed/s.length).toFixed(2)}%. ${passed/s.length>.95?'Visual threshold reached in this draft; real-app and functional acceptance still required.':'Visual threshold not reached.'}`:`${done.length}/${s.length} criteria scored; overall result pending.`;});</script></html>'''
(evidence / "index.html").write_text(html, encoding="utf-8")

# Every item is an explicit observable subcriterion; passing an entire suite does
# not automatically accept these individual items or manufacture a parity score.
checks = {
1: ("Provider contract", "Missing optional data remains absent;Snapshot carries quota, stats, history and detection"),
2: ("Detection", "Connected/Limited/Expired/Not connected shown;Source and actionable reason shown"),
3: ("Refresh", "Provider failure stays isolated;Cancellation stops outstanding work;Newest queued request wins;UI stays responsive"),
4: ("Partial success", "History failure leaves quota visible;Quota failure leaves available history visible"),
5: ("Claude quota", "Session and weekly windows use payload values;Scoped windows remain distinct;Windows ordered by semantic kind"),
6: ("Claude history", "Incremental offsets and deduplication;Local day/model/project aggregation;Malformed record does not stop file"),
7: ("Pricing", "EXACT/ESTIMATED/UNKNOWN labels;Unknown model retains tokens without invented money"),
8: ("OpenAI selection", "OAuth source has priority;Expired OAuth never silently falls back;Admin mode only without OAuth"),
9: ("Codex protocol", "Temporary process home and bounded stdio;Access token only on stdin;No refresh/login modification"),
10: ("OpenAI API", "Admin spend/budget/history;Project key shows Limited;Pagination and partial data errors preserved"),
11: ("Gemini", "CLI/service-account/ADC discovery;Cloud Monitoring request counts;No fabricated token count"),
12: ("Credential validity", "Invalid service-account shape rejected;All candidates checked before Limited"),
13: ("No invented data", "Missing denominator shows dash;Missing history days are not invented zeroes;Reported window duration used"),
14: ("Dashboard", "Connections and all provider tabs;Gauge/stat/daily/model/project sections;Setup/error/history-error states"),
15: ("Metric/range", "Total/output/equivalent value selectors;7/14/30/90 day ranges;Unavailable data remains absent"),
16: ("Severity", "75/90 local thresholds and worst server severity;Glyph and word accompany colour"),
17: ("Reset", "Countdown changes with clock;Reset clock time shown;No reset does not invent time"),
18: ("Dashboard mode", "300x220 minimum;Resize never switches mode automatically"),
19: ("Widget", "Drag and resize to at most 300x300;Gauge count follows width;Opacity/topmost;Size survives restart"),
20: ("Widget navigation", "4-second showable-service rotation;Pin and all-services choices;Chevrons work without rotation;Navigation never fetches"),
21: ("Geometry", "Separate mode/topology/DPI placements;Eight retained layouts;Restore stays on available displays"),
22: ("Tray", "Short-window tile/percentage;2-second service rotation;Last-good on failure;Live countdown menu and tooltip"),
23: ("Single instance", "Second instance activates first and exits;Crash/abandoned lock recovery;Other-user activation denied"),
24: ("Startup", "Fresh-profile default applies once;External off remains off;Enabled path refresh;Upgrade retains enabled and disabled intent"),
25: ("Close/exit", "Close parks only when tray is available;Exit closes process and cancels background work"),
26: ("Settings", "Legacy defaults and choices;Refresh interval validation;Per-service monitoring"),
27: ("Preview", "Theme/opacity/topmost/size preview;Cancel/X/Esc restore entry state;Startup changes only on Save"),
28: ("Theme", "Follow Windows/light/dark;System theme change rechecked;Severity colours shared across views"),
29: ("Secrets", "External logins read only;DPAPI user-bound saved secrets;No plaintext fallback;Atomic interrupted-write recovery"),
30: ("Reports", "Current snapshots without fetch;Failed/no-history sections;No invented missing usage"),
31: ("Export/print", "CSV/Markdown/HTML escape and date filename;Light print palette;Real print preview"),
32: ("Menus/shortcuts", "Refresh/sign-in/log/print/exit commands;Startup/theme/widget/settings/About commands;Shortcuts in dashboard and widget"),
33: ("Updates", "Release check distinguishes offline/private/rate-limit;Highest-tag fallback;Optional download/run extension needs owner exception"),
34: ("Documents", "README/license/notices readable;Developer info and links;Documents bundled with binary"),
35: ("Packaging", "One version source and versioned filenames;Legacy in-place installer identity;No duplicate tray/startup/poller on upgrade;Legacy Registry untouched"),
36: ("Lifecycle", "Subprocess/pipe/HTTP/timer cleanup;Idle tray avoids unnecessary rendering")
}
lines = ["# R-004 parity acceptance", "", "Generated by `scripts/build_parity_report.py`; owner acceptance is **pending**.", "", f"Evidence: {len(pairs)} matched client-area render pairs in `artifacts/verification/index.html`.", "These support review but do not replace real-app screenshots or certify functional/visual parity >95%.", "", "## Functional subcriteria", "", "Full automated suite results are in `STATUS_2026-10-14.md`. Each subcriterion must be", "reviewed against Python behavior; none is automatically accepted because a suite passed.", "The owner may record Pass/Fail and approve each exception here. Functional score = accepted passed", "subcriteria / all applicable subcriteria, strictly >95%. Unassessed items keep the result pending.", "", "| ID | Area | Observable acceptance | Owner result |", "|---|---|---|---|"]
total = 0
for number, (area, items) in checks.items():
    for i, item in enumerate(items.split(";"), 1):
        lines.append(f"| PAR-{number:03d}.{i} | {area} | {item} | Pending |")
        total += 1
lines += ["", f"Total: **{total}** subcriteria; accepted: **0 recorded**; result: **pending**, not 0% or 100%.", "", "## Visual screens", "", "Each pair uses the five agreed criteria: row structure, complete elements, text, colours/signals", "and proportions/layout. Owner screen score = passed criteria / 5; mean across all required screens", "must be strictly >95%. Native shell/titlebar/menu and actual DPI captures remain required.", "", "| Screen pair | Owner score /5 | Notes |", "|---|---|---|"]
lines += [f"| {name} | Pending | Client-area render; no owner score |" for name in pairs]
lines += ["", "## Exceptions and remaining evidence", "", "- Optional installer download/run differs from the original read-only update-check contract; owner approval pending.", "- App version and bundled documents intentionally differ between releases; owner approval pending.", "- Installed experimental 2.0 builds with the old separate AppId need their own migration plan; no silent removal.", "- Native tray context menu/tooltip/balloon, real printer/preview, title bars, actual multi-monitor/DPI and lock/crash smoke need a disposable Windows environment.", "- Clean install/upgrade must cover startup on/off, no duplicate processes/trays and preservation of legacy settings/credentials. Compiler success does not prove these.", "- No installer was executed and no real Run key or real credentials were used during local verification.", "- Owner sign-off and final R-004 score have not been recorded.", ""]
acceptance_path = ROOT / "Docs/PARITY_ACCEPTANCE.md"
draft = "\n".join(lines)
if acceptance_path.exists() and acceptance_path.read_text(encoding="utf-8") != draft:
    raise RuntimeError("Acceptance records differ from the generated draft; preserve owner scores/notes and update this document manually.")
acceptance_path.write_text(draft, encoding="utf-8")
print(f"Generated {len(pairs)} visual pairs and {total} ungraded functional subcriteria.")
