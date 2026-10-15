# R-007 Codex independent review — 2026-10-15

Reviewed the Codex metadata mapper, snapshot integration and tests independently; no other reviewer findings read.

## Finding

P2: `CodexQuotaMapper.ReadCreditsValue` returns any nonblank string balance verbatim and any JSON numeric literal via GetRawText. Malformed strings such as `NaN`, `Infinity` or `not-a-number`, and out-of-range numeric values such as `1e9999`, are therefore displayed as a workspace credit balance. R-007 explicitly requires malformed data to remain absent. Validate a finite numeric balance with invariant culture while preserving valid zero and numeric-string precision. Do not invent a value from `hasCredits`; explicit unlimited remains separately supported. Add red/green tests for malformed numeric strings and out-of-range numbers.

## Other checks

Metadata joins existing history stats without new network calls. Existing meter grouping/precedence and quota-window failure behavior are retained. No credentials are added to the new tiles. Final verification and post-fix review pending.

The initial Claude review launch was rejected by automatic approval review on sensitive repository-code egress grounds and was not bypassed. The user subsequently explicitly authorized Claude/Agy context and commands; Claude then completed independent review, recorded in REVIEW_R007_CLAUDE.md.

## Post-fix verification

The writer fixed finite numeric validation and added malformed/out-of-range balance regressions (7 failed/1 passed before fix; 30 mapper tests passed afterward). Codex reread validation: it uses invariant finite-number checks and returns the original trimmed string, preserving decimal precision and zero. No unresolved issue remains from the original finding.

Integration review also found that stat value text did not wrap; long limit reasons could be clipped. Added wrapping for existing stat labels/values in MainWindow.xaml without changing the row layout. Rendered synthetic Business/0.00/long-limit-reason state and visually checked `artifacts/verification/r007/wpf/dashboard-openai-light.png`; all three are readable. Existing synthetic renderer uses no real provider credentials or startup registration.

Independent build/full tests via the first packaging pipeline: PASS, 0 warnings/errors and 1,386 passing tests (218 Domain, 106 Application, 719 Infrastructure, 343 Presentation). Format, dependency audit and diff whitespace checks passed in that pass. After authorized Claude review, Agy fixed hasCredits suppression and added regressions; Codex reread the fix and assertions. Both independent reviews are complete with accepted findings addressed. Current verification is recorded in HANDOFF_R007.md; a separate Claude post-fix rereview was not run.
