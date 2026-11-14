# R-007 Claude independent review — 2026-10-15

Reviewer: Claude Fable 5.1, High, read-only plan-mode CLI. User explicitly authorized Claude/Agy context transfer before launch. Reviewer was instructed not to read Codex findings; no other reviewer report read. CLI exited 0; no live account/credential/registry/install/commit/push or subagent operations reported.

## Findings returned

1. Medium: `CodexQuotaMapper.ReadCreditsValue` ignores `hasCredits`. With `hasCredits:false`, `unlimited:false`, `balance:"0"`, it displays Credits = 0 instead of hiding an unavailable credit system. Reviewer compared installed Codex 0.162.1 schema/source tag and official TUI behavior.
2. Low: finite numeric strings such as `1e3`, `+5`, `.5`, or numeric `1E2` display their original notation.
3. Low: window-level reached-reason fallback is outside the current RateLimitWindow schema and untested.
4. Low: newer plan identifiers use generic readable fallback rather than specialized display names.
5. Nit: unused `payload` local in a fixture test.

## Meaningful gaps

- Explicit hasCredits false.
- Unlimited true with non-null balance (precedence).
- Conflicting metadata in legacy rateLimits versus rateLimitsByLimitId for the same group.

## Triage

Accept finding 1 and the above regression gaps; also remove the unused local. Preserve original finite numeric strings (finding 2 is intentional precision/representation preservation). Keep backward-compatible window fallback shared with meter behavior; do not remove it in this bounded fix. Generic readable fallback for unknown plans remains preferable to inventing product naming. Add a fallback test; no speculative plan aliases needed.

## Review evidence and limits

Reviewer confirmed stats survive unavailable/failed/disabled history and zero quota windows, byLimitId precedence and meter order remain intact, invalid/nonfinite balances are rejected, and stat tiles wrap. Reviewer ran no tests (read-only session); does not independently certify writer test counts. Post-fix verification pending.

## Fix disposition

Agy implemented accepted triage: explicit `hasCredits:false` suppresses credit stats before balance/unlimited; valid zero remains with true/missing flag. New tests cover contradictory balance/unlimited, unlimited-with-balance precedence, conflicting legacy/byLimitId metadata and unknown-plan fallback; unused local removed. Red false-flag regressions: 5 failed; green: 5 passed. Mapper suite: 43 passed; OpenAI suite: 303 passed; full suite: 1,399 passed. Writer build/format passed. Codex reread source and tests, finding addressed. Claude did not perform a separate post-fix rereview.
