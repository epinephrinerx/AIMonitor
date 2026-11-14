# R-010 — Gauge-first preview with a separate supplementary section

User approved retaining gauges and adding a new section beneath them. This is a synthetic preview revision; no WPF, GaugeControl, quota calculation, credentials or installer changes were made by this task.

## Changes

- `artifacts/visuals/aimonitor-reset-preview.html`: two 260-degree arc gauges above a separate labelled region for reset, plan, workspace credits and activity; expandable reset details retained. Smaller widths stack gauges. Claude unknown remains distinct from zero.
- `artifacts/r009_browser_check.py`: checks two gauges per variant, supplementary region follows gauges without nesting, and gauge markup remains unchanged across all six provider/reset states.
- `Requirements.md`: R-010 records the user request and waits for user acceptance.

## Review

Agy Gemini 3.8 Flash High wrote the preview and exited. Claude Fable 5.1 high reviewed independently, read-only; Codex separately reviewed the HTML and rendered screenshots. Confirmed findings fixed: Normal gauge color, title/severity placement, arc thickness, supplementary-row CSS specificity, named region/group semantics, disabled-button appearance. Also removed unsupported Claude no-pooled-credit assertion and reduced activity tiles to an inline row. Existing reset-credit terminology retained to avoid conflating earned reset credits with paid workspace credits.

## Verification

- Visualize `scripts/render.py ... --force`: PASS, browser wrapper regenerated.
- `python artifacts/r009_browser_check.py`: PASS, six provider/reset cases; both layouts at 320/736/1024px; no horizontal overflow, independent region order, stable gauge markup, drawer interaction.
- Desktop and Claude narrow screenshots inspected. Browser tests use an isolated disposable profile and block HTTP/HTTPS. Test browser closed; user-facing Edge preview opened separately and deliberately left open.
- No .NET tests/build: this task changes no application source.

## AGY follow-up input

User supplied `Docs/AGY-Usage.md` during this task. Read without modification. It describes `agy -p "/usage" --output-format json`, groups with weekly/5h buckets and remaining fractions. Treat it as supplied integration input, not a verified public contract. Future integration must validate CLI output and failures, map used percent as `(1 - remaining_fraction) * 100`, keep AGY Gemini/third-party pooled limits separate from existing Cloud Monitoring counts, and retain unknown rather than zero for missing values. No live usage command or account was accessed for this preview task.

## Limits

All displayed values are synthetic. Real data wiring, WPF behavior and complete accessibility conformance remain untested. User acceptance is pending.
