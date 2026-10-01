# Phase 2 review findings

Reviewer: GPT-6 Sol · effort Max  
Date: 2026-09-27  
Scope: read-only review of `ARCHITECTURE.md`, `TESTING.md` and ADR-0001..0003

Writing Owner must verify and correct these evidence-backed findings without expanding scope:

1. **High — cross-version single-instance claim is false.** ADR-0003 lines 31-33 and Architecture lines 127-131 imply an AppId-derived mutex/pipe can detect the Python process. The Python version has no mutex and derives a Qt local-server name from `AIUsageMonitor|username|scope`. Define installer/process transition explicitly and remove interoperability claims that do not exist.
2. **High — preserve Windows startup intent.** ADR-0003 lines 28-29 and 52-53 must not simply delete/disable the legacy `HKCU Run` value. Snapshot whether the value exists immediately before upgrade, stop the old process, and replace its command with the C# executable only if it was enabled; keep it absent if the user disabled it in Windows Startup Apps.
3. **High — legacy saved secrets are already DPAPI-protected.** ADR-0002 lines 36-38 and 58-59 incorrectly call them unsafe to migrate. The Python implementation uses CurrentUser DPAPI and known application entropy. Specify same-user decrypt-in-memory and immediate re-seal to the new store, never writing plaintext to disk/log. Failure of one secret must be non-fatal and must not destroy the original blob.
4. **Medium — use the actual legacy QSettings path.** Import values recursively from `HKCU\Software\AIUsageMonitor\AIUsageMonitor` and its groups, not merely the organisation parent. Tests must cover nested keys.
5. **Medium — publish the approved deployment artifact.** Replace installer gates based only on `dotnet build -c Release` with an explicit self-contained `dotnet publish` for the WPF project, `Release`, `win-x64`, and make Inno consume that verified publish directory.
6. **Medium — resolve migration-policy contradiction.** The user selected automatic one-time import after `FEATURE_PARITY.md` described migration as opt-in. The later explicit decision supersedes that draft wording. Update `FEATURE_PARITY.md` narrowly to say automatic, one-time, forward-only, read-only import when new settings do not exist.
7. **Medium — IPC names are not access controls.** Require current-user-only named-pipe security and mutex ACL scoped to the current Windows user SID. Add negative integration tests for cross-user/unauthorized activation where feasible.
8. **Medium — Codex external-token authentication is experimental now.** Document `chatgptAuthTokens`/experimental App Server capability as a version-sensitive dependency and require contract/capability tests and actionable failure behavior; do not postpone the label until implementation.
9. **Consistency — atomic secret writes.** Architecture claims interrupted writes cannot corrupt `secrets.dat`; ADR-0002 only defines atomic replacement for settings. Define temp/write-through/replace (or equivalent) for the encrypted blob and test it.
10. **Accuracy — NotifyIcon does not require WindowsFormsIntegration hosting.** Reference `System.Windows.Forms.NotifyIcon` directly as a component used by the WPF process; remove claims that `WindowsFormsIntegration` is required unless an actual hosted WinForms control is introduced later.
11. **Integrator regression — experimental Codex failure must not bypass OAuth priority.** The first repair introduced a fallback from an existing Codex OAuth login to OpenAI Admin API mode. This contradicts PAR-008: if OAuth exists but is expired, rejected, or the experimental capability is missing/changed, show an actionable Codex error and do not silently or automatically switch data modes. Admin API mode remains valid only when no Codex OAuth source is present (PAR-010). Correct both architecture and contract-test wording.

## Repair constraints

- Writing Owner may edit `Docs/ARCHITECTURE.md`, `Docs/TESTING.md`, `Docs/FEATURE_PARITY.md`, and `Docs/adr/0001..0003` only.
- Do not edit Requirements/workflow/repository instructions/handoff/review report.
- Do not create code, project files, commits or pushes.
- After repair, report each finding as fixed or rejected with evidence and re-check template placeholders/internal links.
