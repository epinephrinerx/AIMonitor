# R-006 Codex independent review — 2026-10-15

Scope: provider/client/credential and Connect dialog changes in the local working tree. Reviewed independently before reading Claude findings. No live credentials or account requests.

## Findings

1. **P2 — Gemini 401 recovery advice ignores the selected credential source.** `GeminiLiveQuotaClient.UpdateDetectionForAuthError` always tells the user to sign in with `gemini` when a 401 occurs, including the saved/environment/gcloud service-account paths. Signing in to Gemini CLI does not replace the higher-priority service-account credential, so following the instruction cannot recover that connection. Use source-aware safe advice; keep the CLI command only for the CLI OAuth source. Add synthetic service-account and CLI 401 hint assertions.
2. **P2 — History authentication failures still leave detection Connected.** Both clients update detection in the main HTTP exception handlers, but their history exception handlers only assign `historyError`. A successful live reading followed by a history endpoint 401/403 therefore continues to advertise Connected, contrary to the feedback fix. Preserve successful meters and partial-success behavior, while updating detection and its actionable hint for history authentication/permission failures. Cover both providers with fake HTTP regressions; do not downgrade detection for non-auth history failures.

## Other checks

Environment fallback preserves existing explicit/credential project priority. Scope comparison is exact and read-only. Key guidance has property change notification and a visible XAML binding. No response body is exposed by the new status handling. Official OpenAI Admin API documentation was fetched during review: https://developers.openai.com/api/docs/guides/admin-apis and https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs.

Writer-reported build/test/format results are in HANDOFF_R006.md; this review did not independently rerun them.

## Post-fix check

Both original findings addressed: service-account and CLI 401 hints differ by selected source; history 401/403 updates detection without discarding successful meters. Read the fixes and synthetic regression assertions after the writer pass. Additional Claude host/source guidance findings were also addressed. Independently ran packaging pipeline: build PASS (0 warnings/errors), all 1,351 tests PASS, portable publish and installer compile PASS; audit and diff checks PASS. Live account, installer execution, and desktop visual acceptance remain pending.
