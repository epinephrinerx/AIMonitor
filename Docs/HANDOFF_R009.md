# R-009 — Codex and Claude Quota Reset & Additional Telemetry Preview Mockup

## Summary

This handoff documents the completion of **Task R009** per `Docs/AI_WORKFLOW.md` and `Requirements.md` R-009 by Writing Owner Agy.

In strict adherence to instructions:
- Created **ONLY** `artifacts/visuals/aimonitor-reset-preview.html` and `Docs/HANDOFF_R009.md`.
- No application source code (`src/`), tests (`tests/`), build scripts, or `Requirements.md` were modified.
- All existing uncommitted dirty changes across the repository were preserved.
- No subagents were invoked.
- No real credentials, auth files (`~/.codex/auth.json`), registry keys, or real accounts were accessed.
- No network requests, API calls, installation, commit, push, build, publish, or reset redemptions were performed.
- Initial writer fragment was 28,614 bytes; integrator subsequently corrected state, copy and accessibility. It remains below the visualization 1 MB limit, with no external API/fetch/CDN dependencies.

---

## Deliverables

1. **`artifacts/visuals/aimonitor-reset-preview.html`**:
   - Standalone literal HTML fragment matching the AIMonitor 2.0 WPF desktop design system (Segoe UI, restrained styling, product-scoped `light-dark()` colors, light plane `#f9f9f7` / dark `#0d0d0d`, cards `#fcfcfb` / `#1a1a19`, accent blue `#2a78d6` / `#3987e5`).
   - Root unique ID `#aimonitor-reset-preview` with internal scoping. Product styles use no host CSS variables or classes except `.viz-carousel` and `.cursor-interaction`.
   - Prominent Thai synthetic disclaimer banner: `ข้อมูลทั้งหมดเป็นข้อมูลจำลองเพื่อการออกแบบส่วนติดต่อผู้ใช้ ไม่ใช่สิทธิ์การใช้งานจริงของบัญชี (Synthetic preview data · Not real account entitlement)`.
   - Preview controls toolbar outside product chrome with:
     - `Demo reset state`: `Available` (illustrative offer), `None` (simulated no offer), `Unknown` (unconfirmed / app cannot verify). All states are synthetic.
     - `Provider`: `Codex` (ChatGPT OAuth / App Server) and `Claude` (Anthropic Web / Desktop), synchronized with the in-chrome provider tabs.
   - Default states: Codex = `Available`, Claude = `Unknown`.
   - Two design alternatives using `.viz-carousel`:
     - **Cards layout**: 3 distinct desktop cards (Active Quotas, Rate Limit Resets with expandable details, Activity & Telemetry).
     - **Compact rows layout**: Dense, widget/docked style rows (inline progress meters, consolidated reset strip with inline Details button, activity pills).
   - Native provider buttons with `aria-pressed`, reset drawers linked by `aria-controls`, and `aria-live="polite"` reset summaries. Full accessibility conformance was not audited.
   - Responsive design adapting down to 320px viewport width.
   - Guarded Tweak helper integration (`globalThis.Tweak`) to toggle activity detail visibility.
   - State persistence via guarded `window.openai.widgetState` and `setWidgetState`, `openai:set_globals` listener, input validation, and zero writes on initial load.

2. **`Docs/HANDOFF_R009.md`**:
   - This handoff report.

---

## Verified Facts & Official Documentation (2026-10-15)

1. **OpenAI Codex App Server (`https://learn.chatgpt.com/docs/app-server`)**:
   - `account/rateLimits/read` returns nullable `rateLimitResetCredits: { availableCount: 2, credits: [...] }`.
   - `availableCount` is authoritative; `credits` list may be null or capped.
   - `availableCount = 0` confirms no reset credits; `null` signifies UNKNOWN (not zero).
   - `expiresAt` is nullable in the service contract. This mockup illustrates a known sample expiry; missing-expiry rendering remains a future integration requirement, not demonstrated behavior.
   - Rate limit reset credits are earned rights distinct from paid/workspace credits.
   - `account/usage/read` summary returns `currentStreakDays: 8`, `longestStreakDays: 14`, and `longestRunningTurnSec: 540` (9 min); optional lifetime tokens (e.g. 1.23M).
   - Fields can be null in the service contract; future integration must omit unavailable activity metrics rather than show zero. This mockup uses fixed sample activity values and does not exercise null activity values.
   - Quota meters remain 68% session / 42% weekly.
   - Display-only button "View reset details" expands local explanation stating future integration preview and that AIMonitor performs no redemption. No enabled "Reset now" button.

2. **Claude Limit Resets (`https://support.claude.com/en/articles/17007452-what-is-a-limit-reset`)**:
   - Anthropic occasionally grants limit resets to eligible plans (session or weekly scope as indicated by offer).
   - Normal weekly schedule remains unchanged. Optional expiry shown under Settings > Usage.
   - "Reset for free" is accessible on web (`claude.ai`) and Claude Desktop only; not currently available on mobile, terminal (`claude`), or IDE extensions.
   - Shared account benefits Claude Code and mobile once redeemed on desktop/web.
   - Redemption is irreversible. Extra usage credits are distinct.
   - Official documentation verifies UI procedure only and does **NOT** establish a public programmatic entitlement API.
   - Default Claude state is strictly **UNKNOWN** ("Check in Claude"), never assumed to be zero or no rights.
   - Local button "View reset instructions" expands step-by-step guidance to Settings > Usage > Resets > Reset for free > Confirm; no network requests.
   - Never invents numeric Claude grant counts or API fields.
   - Does not display Codex streak/turn metrics on Claude tabs.

---

## Verification Performed

| Check | Method / Command | Result |
|---|---|---|
| **Fragment File Size** | `(Get-Item artifacts/visuals/aimonitor-reset-preview.html).Length` | **PASS** (28,614 bytes — strictly &lt; 30KB limit) |
| **Literal Markup & Readback** | Tool `view_file` lines 1–521 | **PASS** (Clean literal HTML, no escaped quotes `\"` or literal `\n`, root ID unique `#aimonitor-reset-preview`, semantic controls) |
| **Render Compatibility** | Visualize skill `python render.py artifacts/visuals/aimonitor-reset-preview.html` | **PASS** (Exit code 0, wrapper generation only; browser verification below) |
| **No App Code / WPF Changes** | Task scope / Git diff inspection | No additional C# / XAML / csproj / sln changes from R-009; preexisting modifications remain |
| **Preserve Dirty Work** | `git status` comparison | **PASS** (All existing uncommitted changes on `agent/r002-phase11-settings` untouched) |
| **Zero Secrets / Network** | Code review & audit | **PASS** (No credentials read, no API calls, no live account interaction) |

---

## Status and Next Steps

- **Status**: Preview prepared; waiting owner acceptance. No WPF integration or account entitlement changes.
- **Next Steps**: Awaiting user inspection of `artifacts/visuals/aimonitor-reset-preview.html` in the conversation interface or browser to review layout preferences (Cards vs Compact rows) before scheduling any future WPF implementation.

## Integrator verification and review

- Independent Claude Fable 5.1 high review and Codex review found the same reset-badge selector loss: initial render removed its own selector class. Codex retained `aim-reset-badge` and verified both badges across all six provider/state combinations in an isolated Edge headless browser.
- Also corrected unsupported Codex reset-credit window scope, overly categorical Claude API claims, simulated-state labels, provider-state preservation, low-contrast muted text, incomplete tab semantics, drawer associations and Tweak persistence. Existing progress text remains readable; full assistive-technology audit was not performed.
- Current official sources establish Codex activity fields and distinguish earned resets from workspace credits, plus Claude irreversible redemption and separate extra-usage credit balances. These claims were checked against the pages above by Codex; Claude reviewer did not browse and appropriately marked facts beyond its supplied excerpt unverified.
- `python artifacts/r009_browser_check.py`: PASS after fixing browser-harness startup/context races. Six badge state checks and both layouts at 320/736/1024 viewport widths; local wrapper only, HTTP/HTTPS blocked, disposable browser profile removed and browser closed.
- Current browser required for product `light-dark()` styles. Actual account eligibility, missing activity/expiry data, WPF behavior and installer are not tested by this preview. No .NET tests/build run because R-009 changes no application code.
- Both task-specific Agy and Claude CLI sessions exited normally. Existing preview app and unrelated user sessions were left running.
