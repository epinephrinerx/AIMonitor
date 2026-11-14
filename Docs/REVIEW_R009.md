# R-009 independent review and integration

Scope: response-only `artifacts/visuals/aimonitor-reset-preview.html` and `Docs/HANDOFF_R009.md`. Preserve all preexisting source changes. No credentials, live reset, registry, install or publishing.

Claude Fable 5.1 high reviewed independently in read-only mode, without reading Codex findings. Codex independently read the artifact and checked official product documentation. Both found a blocker: rendering replaced the reset badge class and lost the selector, leaving stale counts after provider/state changes. Codex integration retained the selector and verified all six states in an offline browser.

Additional triaged corrections: removed unverified reset-credit window scope, qualified Claude API availability, labelled absent-offer states simulated, preserved selected states across providers, improved muted text contrast, changed incomplete ARIA tabs to native pressed buttons, linked drawers, and persisted Tweak changes. Handoff no longer claims missing-expiry/activity rendering that the fixed sample mockup does not demonstrate.

Official sources checked by Codex:
- https://learn.chatgpt.com/docs/app-server : nullable reset summary, authoritative count, capped optional details, earned resets, activity summary, consume contract.
- https://support.claude.com/en/articles/17007452-what-is-a-limit-reset : eligible offers, scope, expiry, irreversible redemption through web/Desktop, shared effects and distinct usage credits.

Verification: `render.py` wrapper generation PASS; `python artifacts/r009_browser_check.py` PASS for six provider/state badge combinations and both layouts at 320/736/1024 widths. Isolated local Edge profile, HTTP/HTTPS blocked, browser/profile cleaned. Earlier harness attempts failed on startup/iframe context selection and were corrected; they were not application failures. No .NET tests/build required or run for the response-only mockup. Post-fix browser verification is Codex's, not a claimed second Claude review.

Remaining: owner design acceptance, actual account availability and future WPF integration; null activity/expiry examples and full assistive-technology audit not covered. Task CLI sessions closed.
