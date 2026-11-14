# R-008 — OpenAI / Codex Login Guidance and Available Additional Data Assessment

## Summary

Integrator final verification: rendered 36 synthetic WPF screens to `artifacts/verification/r008/wpf` and inspected `connect-openai-light.png`; guidance and Save/Cancel fit visibly. Independent Codex/Claude copy reviews found no blocking issue (`Docs/REVIEW_R008.md`). `./build_installer.ps1 -SkipTests -IsccPath ./artifacts/tools/inno/compiler/ISCC.exe` exited 0 (0 build warnings/errors, portable publish and installer compile PASS). Tests were deliberately not repeated in this packaging pass for a copy-only change; writer's build/format and existing 1,403-test result are recorded below. Final `git diff --check` passed after trimming one excess EOF blank line in Requirements. Claude/Agy sessions for this pass exited. Corrected preview reopened from `publish/win-x64/AIUsageMonitor.exe` for owner inspection. No real-account login verification, installation, commit, push or release.

This handoff documents the completion of the bounded copy-only task under **R-008** and provides an informational assessment of additional provider data available for potential future display.

In accordance with user authorization and project constraints:
1. **Login Guidance Updated (Copy-only)**:
   - Updated `ProviderMeta.OpenAi.SetupHint` and `OpenAiLiveQuotaClient.SetupHint` with concise, readable English copy explaining:
     - Running `codex login` for browser ChatGPT OAuth.
     - Running `codex login --device-auth` for device-code flow (with note to enable it in ChatGPT security/workspace settings if required).
     - Running `codex login status` to check mode.
     - Returning to AIMonitor and clicking Refresh.
     - Architecture/limitation clarification: AIMonitor reads existing file-backed login (`~/.codex/auth.json`) read-only, does not initiate OAuth or refresh external tokens, and logins stored solely in OS credential stores or keychains may not be detected.
     - Organization Admin key spend: API spend requires an optional organization Admin key (`sk-admin-…`) via Connections or `OPENAI_ADMIN_KEY` (requires restart); ordinary project keys cannot read organization spend.
     - Priority: Codex OAuth always takes priority over API keys (even when expired), and existing saved keys are retained.
   - No new buttons, login launches, auth logic changes, or dependency changes were introduced.
2. **Additional Data Assessment (Informational Only)**:
   - Compared current OpenAI/Codex mapping with official App Server and Platform API documentation; the assessment below lists additional data rather than repeating existing meters/stats.
   - In accordance with instructions, **NO** new metrics or UI bindings were added; all items remain pending separate user decision and approval.
3. **Verification**:
   - `dotnet build` succeeded with 0 warnings and 0 errors.
   - `dotnet format` verified 0 violations.
   - `dotnet test` passed 1,403 / 1,403 tests.
   - All existing uncommitted changes in the repository were preserved.

Official references consulted:
- https://learn.chatgpt.com/docs/auth
- https://learn.chatgpt.com/docs/cli/reference

---

## Changed Files

1. **`src/AIMonitor.Presentation.Wpf/ViewModels/ProviderMeta.cs`**:
   - Updated `OpenAi.SetupHint` with practical login instructions, read-only behavior description, and Admin key spend guidance.
2. **`src/AIMonitor.Infrastructure/Providers/OpenAi/OpenAiLiveQuotaClient.cs`**:
   - Updated `SetupHint` constant to match `ProviderMeta.OpenAi.SetupHint`.
3. **`tests/AIMonitor.Presentation.Wpf.Tests/ProviderMetaTests.cs`**:
   - Synchronized `expectedHint` in `OpenAi_Metadata_MatchesSpecificationVerbatim` to maintain test suite integrity.
4. **`Requirements.md`**:
   - Appended section `## R-008 · เพิ่มคำแนะนำสำหรับการ login เข้าสู่ Codex / OpenAI และสำรวจข้อมูลส่วนที่แสดงเพิ่มเติมได้` with verbatim user quote, authorized scope (guidance-only), informational scope for additional data, and acceptance criteria.
5. **`Docs/HANDOFF_R008.md`**:
   - Created this handoff document.

---

## Exact Verification Commands and Results

| Check | Command | Result |
|---|---|---|
| **Build Verification** | `dotnet build AIMonitor.sln --no-restore` | **PASS** (0 Warning(s), 0 Error(s)) |
| **Format Verification** | `dotnet format AIMonitor.sln --verify-no-changes --no-restore` | **PASS** (0 format violations, exit code 0) |
| **Full Test Suite** | `dotnet test AIMonitor.sln --no-build` | **PASS** (1,403 passed, 0 failed, 0 skipped across 4 projects: Domain 218, Application 106, Infrastructure 736, Presentation.Wpf 343) |
| **Git Diff Inspection** | `git diff --stat` | Source copy changes inspected; preexisting uncommitted work preserved. No real credential files read. |

---

## Assessment: Additional Data Available for Future Display (Informational Only)

> [!NOTE]
> The following list is provided for evaluation only. In accordance with user direction, no new metrics have been added to the application. Any addition will require explicit user approval.

### OpenAI / Codex — additional fields in existing reads

- `account/rateLimits/read`: `rateLimitResetCredits.availableCount` and optional detail rows including `expiresAt`, `title`, and `description`. This is the number of earned quota-reset rights, distinct from workspace spending credits. Count is authoritative; missing detail rows must not be invented. Display only; no redemption operation is authorized.
- `account/usage/read`: `summary.currentStreakDays`, `summary.longestStreakDays`, and `summary.longestRunningTurnSec`. Current mapper displays lifetime/peak token stats but not these activity metrics. Each may be null and must remain absent when not supplied.
- Official source: https://learn.chatgpt.com/docs/app-server (Rate limits and Token usage sections).

Current app already maps quota group windows/durations/reset times, available daily total tokens, lifetime/peak tokens, plan, workspace credits and reached-limit reason; these are not new proposals. Browser OAuth and device-code OAuth are both ChatGPT authentication; the persisted auth mode does not by itself distinguish which login ceremony was used.

### OpenAI Platform API — optional future Admin-key expansion

- Usage breakdown by project, user or API-key ID and minute/hour bucket widths, beyond the current daily/model grouping. Requires the appropriate organization Admin permissions.
- Additional cache-write/uncached and text/audio/image token fields when returned by the completions usage endpoint. Existing input/output/cached totals and daily costs are already displayed.
- Official source: https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions .
- Do not label token-proportioned model costs as actual per-model billed costs. A separate verified cost attribution contract is needed for any such claim.

---

## Status and Remaining Risks

- **Status**: Waiting owner acceptance on the updated login guidance copy in the running UI (`Requirements.md` R-008).
- **Remaining Risks**:
  - Users whose Codex CLI exclusively writes credentials to an OS credential store (e.g., Windows Credential Manager) rather than `~/.codex/auth.json` will continue to require file-backed auth or Admin API key configuration. The updated copy explicitly explains this limitation.
  - No background processes or daemons were left running.
