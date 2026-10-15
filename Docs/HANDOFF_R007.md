# R-007 — Codex ChatGPT Plan, Remaining Workspace Credits and Reached-Limit Reasons

Latest integrator verification: implemented accepted triage from `Docs/REVIEW_R007_CLAUDE.md` and verified schema compliance with installed Codex 0.162.1. Explicit `hasCredits: false` suppresses the credits tile (including contradictory balance or unlimited values); valid numeric zero is preserved when `hasCredits: true`; existing optional/missing-flag compatibility is preserved. Added red/green regressions for `hasCredits: false`, `unlimited: true` with non-null balance precedence, conflicting legacy vs `rateLimitsByLimitId` metadata priority, and unknown plan readable fallback. Removed unused fixture-test local. Preserved original finite numeric strings, no speculative aliases, and kept window reason fallback. Full build (0 warnings/errors), full test suite (1,399 passed, 0 failed), format verification (0 violations), and git diff check passed. Codex and Claude independent reviews are triaged and documented. All changes remain uncommitted.

## Summary

Final integrator verification after the Claude finding fix: `./build_installer.ps1 -IsccPath ./artifacts/tools/inno/compiler/ISCC.exe` exited 0; build had 0 warnings/errors and all 1,399 tests passed (218 Domain, 106 Application, 732 Infrastructure, 343 Presentation). Portable publish and Inno installer compilation succeeded. Current artifacts are `publish/win-x64/AIUsageMonitor 2.0.0.exe` and `installer_out/AIUsageMonitor Setup 2.0.0.exe`. `git diff --check` passed with line-ending warnings only. Confirmed the Claude reviewer process and Agy writer processes from this pass are no longer running; tool sessions exited 0. No changes committed/pushed, no actual installation, no live account verification. User's Claude/Agy authorization is recorded verbatim under R-007 in Requirements.md. Both independent reviews and accepted fixes are complete; a separate Claude post-fix rereview was not run. Owner acceptance remains pending.

Authorized R-007 has been implemented and verified across both reviewer passes (`Docs/REVIEW_R007_CODEX.md` and `Docs/REVIEW_R007_CLAUDE.md`). User explicitly authorized Claude/Agy commands and source context transfer. The implementation displays ChatGPT plan, remaining workspace credits, and reached-limit reasons from Codex App Server's `account/rateLimits/read` within AIMonitor.

All changes are strictly scoped to `CodexQuotaMapper`, `OpenAiLiveQuotaClient` Codex snapshot path, directly needed tests/docs/synthetic fixtures, preserving all existing dirty changes without touching real credentials, registry, or making external network/account calls.

## Official Sources and Schema Validation

- **Documentation**: https://learn.chatgpt.com/docs/app-server (`account/rateLimits/read`)
- **Protocol Schema Validation**: Generated and verified against installed `codex` CLI 0.162.1 (`codex app-server generate-json-schema`):
  - `RateLimitSnapshot`:
    - `planType`: string enum (`free`, `go`, `plus`, `pro`, `prolite`, `promax`, `team`, `business`, `enterprise`, `edu`, `unknown`, etc.)
    - `credits`: `CreditsSnapshot` (`hasCredits`: bool, `unlimited`: bool, `balance`: string | null)
    - `rateLimitReachedType`: string enum (`rate_limit_reached`, `workspace_owner_credits_depleted`, `workspace_member_credits_depleted`, `workspace_owner_usage_limit_reached`, `workspace_member_usage_limit_reached`)
    - `primary` / `secondary`: `RateLimitWindow` (`usedPercent`, `windowDurationMins`, `resetsAt`)
  - No credentials read and no live account services called.

## Review Dispositions (Claude & Codex)

| Reviewer / Item | Severity | Disposition | Detail / Action Taken |
|---|---|---|---|
| **Claude Finding 1** | Medium | **Accepted & Implemented** | `ReadCreditsValue` now explicitly checks `hasCredits`. When `hasCredits` is `false`, the credits tile is suppressed, including contradictory balance or unlimited flags. Valid numeric zero is preserved when `hasCredits: true` or when the flag is omitted (preserving backward compatibility). |
| **Claude Finding 2** | Low | **Accepted (Preserved)** | Finite numeric strings (e.g. `1e3`, `+5`, `.5`, `1E2`) preserve their original representation and precision without normalization. |
| **Claude Finding 3** | Low | **Accepted (Preserved)** | Window-level reached-reason fallback is kept intact for backward-compatible fallback shared with meter behavior. |
| **Claude Finding 4** | Low | **Accepted (Preserved)** | Newer/unknown plan identifiers use generic readable fallback (`FormatWords`) rather than speculative product aliases. Added regression test for unknown plan readable fallback. |
| **Claude Finding 5** | Nit | **Accepted & Fixed** | Removed unused `payload` duplicate parse in `RateLimitStats_GoldenCodexRateLimitsR007Fixture_MapsGroupedMetadataInServerOrder`. |
| **Claude Gap: False flag** | - | **Accepted & Implemented** | Added red/green regression tests verifying `hasCredits: false` suppresses credits tile even with contradictory balance or unlimited flags. |
| **Claude Gap: Unlimited precedence** | - | **Accepted & Implemented** | Added regression test verifying `unlimited: true` takes precedence over non-null finite balance (`"Unlimited"` returned). |
| **Claude Gap: Conflicting metadata** | - | **Accepted & Implemented** | Added regression test verifying `rateLimitsByLimitId` metadata takes precedence over legacy `rateLimits` block for the same group (first-writer-wins in `ExtractGroups`). |
| **Codex Finding (P2)** | P2 | **Accepted & Implemented** | `ReadCreditsValue` validates finite numeric balances using invariant culture; rejects `NaN`, `Infinity`, non-numeric strings, and out-of-range values. |

## Scoped Implementation

1. **`src/AIMonitor.Infrastructure/Providers/OpenAi/CodexQuotaMapper.cs`**:
   - `RateLimitStats`: Maps `planType`, `credits`, and `rateLimitReachedType` into provider-neutral `Stat` objects.
   - `ReadCreditsValue`:
     - Checks `hasCredits`: if explicitly `false` (bool or `"false"` string), returns `null` to suppress the credits tile, overriding contradictory balance or unlimited fields.
     - Preserves valid numeric zero (e.g. `"0"`, `0`, `"0.00"`) when `hasCredits` is true or omitted.
     - Checks `unlimited`: formats explicit unlimited status as `"Unlimited"` (taking precedence over finite balance).
     - Validates credit balances as finite numbers with invariant culture for both string and JSON numeric representations. Rejects `NaN`, `Infinity`, `+Infinity`, `-Infinity`, non-numeric strings, and out-of-range numeric values (e.g. `1e9999`).
     - Preserves decimal-string precision (`"123.450"`, `"0.00"`).
     - Does not invent credit values or currencies.
   - `FormatPlanType`: Formats recognized plans, with fallback to readable title casing via `FormatWords` without speculative product aliases.
   - Grouping: Uses grouped labels (e.g. `Codex · Plan`, `GPT-5 · Plan`) when multiple buckets exist (`groups.Count > 1`), and plain labels (`Plan`, `Credits`, `Limit reason`) when a single bucket exists.
   - Unified group extraction helper `ExtractGroups` shared between `Meters` and `RateLimitStats` to preserve existing group ordering, meter deduplication, and `rateLimitsByLimitId` precedence over legacy `rateLimits`.
2. **`src/AIMonitor.Infrastructure/Providers/OpenAi/OpenAiLiveQuotaClient.cs`**:
   - In `FetchCodexAsync`, extracts `rateLimitStats` and combines them with `historyStats`.
   - Rate limit metadata survives in `ProviderSnapshot.Stats` when history is disabled (`includeHistory: false`), unavailable, or fails with an error (`HistoryError` populated).
   - Metadata-only payload (0 quota windows) keeps `error: NoQuotaWindowsError` and `snapshot.Ok == false`; does not turn metadata into percentage quota success.
   - Preserves existing quota meters, natural ordering, history mapping, and OAuth precedence.
   - No reset-credit redemption, purchase, notification email, login mutation, simultaneous Admin mode, extra API categories, or new network calls.
3. **`src/AIMonitor.Presentation.Wpf/ViewModels/ProviderTabViewModel.cs`**:
   - Existing `StatTiles` and `StatsSummary` bindings map `Stat` items directly to the UI dashboard tiles and summary line.

## Changed Files

- `Requirements.md`: Appended R-007 with verbatim user quote, approval, scoped interpretation, and updated status.
- `src/AIMonitor.Infrastructure/Providers/OpenAi/CodexQuotaMapper.cs`: Added `RateLimitStats` and helper methods; refactored group extraction into shared `ExtractGroups`; hardened `ReadCreditsValue` with invariant finite numeric validation and `hasCredits: false` suppression.
- `src/AIMonitor.Infrastructure/Providers/OpenAi/OpenAiLiveQuotaClient.cs`: Wired `CodexQuotaMapper.RateLimitStats` into `FetchCodexAsync`.
- `tests/Fixtures/OpenAi/codex-rate-limits-r007.json`: Synthetic golden fixture containing multiple buckets with plan, credits, and limit reason.
- `tests/AIMonitor.Infrastructure.Tests/Providers/OpenAi/CodexQuotaMapperTests.cs`: Added unit and contract tests for present, absent, malformed, numeric zero, unlimited, multiple buckets, golden fixture, credit balance regressions (rejecting NaN, Infinity, nonnumeric strings, and out-of-range numbers), `hasCredits: false` suppression, unlimited precedence over non-null balance, conflicting metadata priority, and unknown plan readable fallback. Removed unused `payload` local.
- `tests/AIMonitor.Infrastructure.Tests/Providers/OpenAi/OpenAiLiveQuotaClientTests.cs`: Added integration tests verifying metadata survival without history, history failures, error state on metadata-only payload, and multiple buckets.
- `tests/AIMonitor.Presentation.Wpf.Tests/ProviderTabViewModelTests.cs`: Added UI mapping test for `StatTiles` and `StatsSummary` with Codex rate-limit stats.
- `Docs/HANDOFF_R007.md`: Updated with full review dispositions, implementation details, and verification commands/results.

## Exact Verification Commands and Results

1. **Targeted Red Regressions Verification (RED)**:
   - Initial implementation: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~RateLimitStats|FullyQualifiedName~GetSnapshotAsync_CodexMetadata"`
     - Result: 10 failed, 10 passed (FAIL as expected before initial implementation).
   - Post-review credit balance validation: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~RateLimitStats_WhenCreditBalanceMalformedOrOutOfRange"`
     - Result: 7 failed, 1 passed (FAIL as expected before fix).
   - Post-review hasCredits:false suppression: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~RateLimitStats_WhenHasCreditsFalse"`
     - Result: 5 failed, 0 passed (FAIL as expected before fix: contradictory balance, unlimited, and zero were returned rather than empty).
2. **Targeted Green Regressions Verification (GREEN)**:
   - Command: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~RateLimitStats_WhenHasCreditsFalse"`
     - Result: PASS (5 passed, 0 failed, 0 skipped, Duration: 27 ms).
   - Command: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~RateLimitStats"`
     - Result: PASS (43 passed, 0 failed, 0 skipped, Duration: 54 ms).
   - Command: `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~OpenAi"`
     - Result: PASS (303 passed, 0 failed, 0 skipped, Duration: 2 s).
   - Command: `dotnet test tests/AIMonitor.Presentation.Wpf.Tests --no-restore --filter "FullyQualifiedName~UpdateFromSnapshot_WithCodexRateLimitStats"`
     - Result: PASS (1 passed, 0 failed, 0 skipped, Duration: 17 ms).
3. **Full Test Suite**:
   - Command: `dotnet test AIMonitor.sln --no-build`
   - Result: PASS (1,399 passed, 0 failed, 0 skipped across 4 projects: Domain 218, Application 106, Infrastructure 732, Presentation.Wpf 343).
4. **Parity Report Test**:
   - Command: `python -m unittest tests/verification/test_parity_report.py`
   - Result: PASS (1 test passed in 0.190s).
5. **Code Formatting Verification**:
   - Command: `dotnet format AIMonitor.sln --verify-no-changes --no-restore`
   - Result: PASS (0 format violations, clean exit 0).
6. **Build Verification**:
   - Command: `dotnet build AIMonitor.sln --no-restore`
   - Result: PASS (0 Warning(s), 0 Error(s), clean exit 0).
7. **Git Whitespace / Diff Check**:
   - Command: `git diff --check`
   - Result: PASS (0 errors).

## Quota, Review, and Tool Status

- No quota warning below 5% or rate/usage limit encountered during execution (quota healthy).
- Claude and Codex independent reviews triaged and dispositions documented.
- No external agents, subagents, or reviewers invoked during this pass.
- No credentials, live account calls, network calls, registry, install, delete, commit, push, or publish performed.
- All background tasks completed and terminated; no background tasks remain.

## Remaining Risks and Next Steps

- Live owner verification of real Codex rate-limit readout on Windows desktop without sharing credentials.
- Awaiting owner acceptance for R-007, R-006, and broader R-004/R-005.
