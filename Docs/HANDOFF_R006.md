# R-006 — OpenAI Platform / Gemini connection fixes

Current outcome (2026-10-15): implementation and synthetic verification complete; owner live-connection acceptance pending. Integrator independently verified packaging build (0 warnings/errors), 1,351 passing tests, self-contained portable output and successful Inno installer compile using `./build_installer.ps1 -IsccPath ./artifacts/tools/inno/compiler/ISCC.exe`. `./scripts/verify_packages.ps1`, final `dotnet format AIMonitor.sln --verify-no-changes --no-restore`, and `git diff --check` passed. Both synthetic renderers refreshed (36 screens each); gallery regenerated (38 pairs, 106 ungraded functional criteria), Python report regression passed (1 test). Codex post-fix review verified its findings resolved; a separate Claude post-fix rereview has not been run. No installation, real credential access, live connection call, commit or push occurred. Broader R-005 startup/upgrade/manual acceptance remains open in STATUS_2026-10-14.md.

Date: 2026-10-15. User clarified OpenAI is a Platform API key; project versus Admin key is **not established**.
Gemini login method, permissions and live error are also not established. No real credential was read or sent.

## Agy investigation and triage

Antigravity/Agy analyzed source excerpts using `gemini-3.8-flash-high`, effort high, without account calls.
Headless command permission and Windows argument-length/protocol errors in earlier attempts were execution
errors, not quota evidence. The final bounded source-excerpt analysis returned successfully.

- **Confirmed code gap:** `GeminiLiveQuotaClient.ResolveProjectAsync` ignores `GOOGLE_CLOUD_PROJECT` and
  `GOOGLE_CLOUD_PROJECT_ID`. A CLI login with a configured environment project still tries Resource Manager
  project listing, adding an unnecessary permission dependency and possibly selecting the wrong project.
- **Confirmed code gap:** `GeminiCliCredentialReader` accepts only cloud-platform scope. Cloud Monitoring
  also accepts monitoring/monitoring.read, so an otherwise suitable read-scoped token is discarded.
  Missing/insufficient scope is silently treated as a missing login, losing actionable detection information.
- **Confirmed feedback gap:** provider credential detection can remain Connected after an HTTP authentication
  or permission failure. Preserve the source/candidates but report actionable failure state and safe hint.
- **Provider limitation, not a broken prefix guard:** ordinary model-request keys and administration keys
  serve different endpoints. Preserve project-key Limited behavior and OAuth precedence; add clear feedback
  during key entry. Agy's statement that the user's actual key is definitely a project key was an inference
  and is **not accepted as a confirmed finding**.
- **Policy, not an implementation bug:** gcloud user ADC requires a refresh-token flow not authorized by the
  existing read-only external-login policy; keep Limited and explain it. Do not refresh another tool's login.

## Current official sources

Admin endpoints use an Admin API key; model-request credentials must not be conflated with these.
[OpenAI Admin APIs](https://developers.openai.com/api/docs/guides/admin-apis),
[Usage reference](https://platform.openai.com/docs/api-reference/usage),
[RBAC](https://developers.openai.com/api/docs/guides/rbac).

Gemini CLI supports different authentication methods and project environment variables. A CLI login alone
does not establish Cloud Monitoring access.
[Gemini CLI authentication](https://geminicli.com/docs/get-started/authentication/).

timeSeries.list requires an explicit resource and accepts cloud-platform, monitoring or monitoring.read
OAuth scope; IAM/API access on the selected project still must be available.
[Cloud Monitoring timeSeries.list](https://cloud.google.com/monitoring/api/ref_v3/rest/v3/projects.timeSeries/list).

## Fix pipeline

1. Add synthetic regressions for environment project priority/fallback, accepted read scopes, limited
   missing-scope diagnostics, auth/permission detection and credential-entry guidance; run them red.
2. Fix only confirmed gaps. Keep provider source precedence, read-only login discovery, sanitized errors,
   no invented usage and the existing external-token refresh policy.
3. Independent Claude/Codex review, triage and fixes. Honor the global <5%/usage-limit quota pause gate.
4. Full build/test/format/audit and local package rebuild, then refresh screen evidence.
5. Owner verifies real connections without sharing keys; R-004 visual score and real install/upgrade remain pending.

## Execution and verification results (2026-10-15)

- **Fixture reference fix:** Replaced missing `WriteGeminiCliOAuth` call with `WriteGeminiCliLogin` in `GeminiLiveQuotaClientTests.cs:166`.
- **Regressions run RED:**
  - Targeted Infrastructure regression filter: 11 failed, 20 passed, total 31 (exit 1). Failures verified on missing scope diagnostics, accepted monitoring scopes, environment project priority, and HTTP 401/403 detection states for OpenAI and Gemini.
  - Presentation KeyGuidance regression filter: 3 failed, 3 passed, total 6 (exit 1). Failures verified on `sk-proj-…`, `sk-admin-…`, and ordinary prefix guidance.
- **Implementations completed:**
  - `GeminiCliCredentialReader`: Accepted `cloud-platform`, `monitoring`, and `monitoring.read` scopes; returns `UsageCapable = false` with `"Cloud Monitoring"` reason when scopes are missing or insufficient.
  - `GeminiLiveQuotaClient`: Added `GOOGLE_CLOUD_PROJECT` and `GOOGLE_CLOUD_PROJECT_ID` environment variables priority and fallback in `ResolveProjectAsync`.
  - `GeminiLiveQuotaClient` & `OpenAiLiveQuotaClient`: Captured HTTP status code in `GeminiApiException` and `OpenAiApiException`; transitioned `DetectionState` to `Expired` (HTTP 401) and `Limited` (HTTP 403) with safe hint while preserving source and candidates.
  - `ConnectDialogViewModel` & `ConnectDialog.xaml`: Implemented `KeyGuidance` providing live feedback during OpenAI key entry, clarifying that API spend requires an organization Admin key (`sk-admin-…`) and standard project keys (`sk-proj-…`) cannot query usage.
- **Exact verification results:**
  - `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~GeminiCliCredentialReaderTests|FullyQualifiedName~EnvironmentProject_AvoidsProjectDiscovery|FullyQualifiedName~TokenEndpointErrorStatus|FullyQualifiedName~AdminApiErrorStatus"`: PASS (31 passed, 0 failed, 0 skipped).
  - `dotnet test tests/AIMonitor.Presentation.Wpf.Tests --no-restore --filter "FullyQualifiedName~KeyGuidance"`: PASS (6 passed, 0 failed, 0 skipped).
  - `dotnet test AIMonitor.sln --no-restore`: PASS (1,332 passed, 0 failed, 0 skipped across 4 test projects: Application 106, Domain 218, Infrastructure 667, Presentation.Wpf 341).
  - `python -m unittest tests/verification/test_parity_report.py`: PASS (1 passed, 0 failed).
  - `dotnet format AIMonitor.sln --verify-no-changes --no-restore`: PASS (0 format violations).
  - `dotnet build AIMonitor.sln --no-restore`: PASS (0 Warning(s), 0 Error(s)).
- **Quota status:** No quota warning or usage limit reached (well above 5% threshold).
- **Reviewer status:** Independent reviews from Codex (`Docs/REVIEW_R006_CODEX.md`) and Claude (`Docs/REVIEW_R006_CLAUDE.md`) completed and addressed below.

## Reviewer fix pass (2026-10-15)

### Findings triage and disposition
1. **Gemini 401 recovery advice ignores credential source (Codex P2 / Claude Medium):** ACCEPTED. `GeminiLiveQuotaClient.UpdateDetectionForAuthError` now differentiates between Gemini CLI OAuth (suggests `gemini` CLI login) and service account credentials (suggests checking/updating service account key in Settings or Google Cloud Console; never tells a service-account user to run `gemini`).
2. **Gemini 403 host and source guidance (Claude Medium):** ACCEPTED. Failing host `cloudresourcemanager.googleapis.com` now advises setting explicit project or `GOOGLE_CLOUD_PROJECT`. Failing host `monitoring.googleapis.com` advises checking project, IAM (Monitoring Viewer role), and Cloud Monitoring API access, customized by source (CLI vs service account).
3. **History 401/403 detection update (Codex P2):** ACCEPTED. Both Gemini and OpenAI clients update detection state to `Expired` (401) or `Limited` (403) with actionable hints when history endpoints fail with auth/permission errors, while preserving successful live meters and partial success (`Ok = true`, `HistoryError` populated). Non-auth history failures (500, transport) leave detection unchanged (`Connected`).
4. **OpenAI 401 advice for env / CLI key sources (Claude Low):** ACCEPTED. `OpenAiLiveQuotaClient.UpdateDetectionForAuthError` now provides source-appropriate advice: mentions `OPENAI_ADMIN_KEY`/`OPENAI_API_KEY` for env source, Codex CLI login for `codex_cli`, and Settings/Connections for manual key.
5. **Actionable missing monitoring scopes & SetupHint project guidance (Claude Low):** ACCEPTED. `GeminiCliCredentialReader`'s `MissingMonitoringScopeReason` now instructs running `gemini` or configuring a service account in Settings. `SetupHint` in `GeminiLiveQuotaClient` now advises setting the project in Settings or with `GOOGLE_CLOUD_PROJECT`.
6. **Limited OAuth candidate token Value invariant (Claude Low):** ACCEPTED. `GeminiCliCredentialReader` sets token `Value` to `string.Empty` when `!isUsageCapable`.
7. **Coverage gaps addressed (Claude / Codex):**
   - Added test verifying Limited CLI does not mask a usable later gcloud service account.
   - Added tests verifying explicit project override beats credential project and env, and credential project beats env.
   - Added test verifying missing-scope CLI login returns Limited snapshot without making HTTP requests.
   - Added tests verifying `SourceId`, `Account`, and `Candidates` are preserved across auth failures.
   - Added test verifying `ConnectDialogViewModel.Key` change raises `PropertyChanged` for `KeyGuidance`.
8. **Project syntax validation:** DEFERRED as instructed (preserving safe URL-escaping without syntax restrictions).

### Verification evidence
- **Targeted regressions run RED:**
  - 11 failed, 7 passed, total 18 (exit 1) on Infrastructure targeted filter.
  - Verified failures on: Gemini service-account 401 advice, Gemini CLI 403 guidance, Resource Manager 403 project guidance, history 401/403 detection updates on both Gemini and OpenAI, missing monitoring scope recovery hint, empty token Value invariant on limited CLI, and OpenAI env 401 guidance.
- **Implementations completed:**
  - `src/AIMonitor.Infrastructure/Providers/Gemini/GeminiCliCredentialReader.cs`
  - `src/AIMonitor.Infrastructure/Providers/Gemini/GeminiLiveQuotaClient.cs`
  - `src/AIMonitor.Infrastructure/Providers/Gemini/GeminiApiException.cs`
  - `src/AIMonitor.Infrastructure/Providers/OpenAi/OpenAiLiveQuotaClient.cs`
  - `tests/AIMonitor.Infrastructure.Tests/Providers/Gemini/GeminiCliCredentialReaderTests.cs`
  - `tests/AIMonitor.Infrastructure.Tests/Providers/Gemini/GeminiCredentialResolverTests.cs`
  - `tests/AIMonitor.Infrastructure.Tests/Providers/Gemini/GeminiLiveQuotaClientTests.cs`
  - `tests/AIMonitor.Infrastructure.Tests/Providers/OpenAi/OpenAiLiveQuotaClientTests.cs`
  - `tests/AIMonitor.Presentation.Wpf.Tests/ConnectDialogViewModelTests.cs`
- **Targeted regressions run GREEN:**
  - `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~GetSnapshotAsync_MissingMonitoringScope|FullyQualifiedName~GetSnapshotAsync_ProjectPriority|FullyQualifiedName~GetSnapshotAsync_Gemini401|FullyQualifiedName~GetSnapshotAsync_Gemini403|FullyQualifiedName~GetSnapshotAsync_GeminiHistory|FullyQualifiedName~GetSnapshotAsync_OpenAi401|FullyQualifiedName~GetSnapshotAsync_OpenAiHistory|FullyQualifiedName~ReadAsync_InsufficientMonitoringScope"`: PASS (18 passed, 0 failed).
  - `dotnet test tests/AIMonitor.Presentation.Wpf.Tests --no-restore --filter "FullyQualifiedName~KeyGuidance|FullyQualifiedName~Key_WhenChanged"`: PASS (7 passed, 0 failed).
  - `dotnet test tests/AIMonitor.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~ResolveAsync_GeminiCliLimitedScopes_DoesNotMaskUsableGcloudServiceAccount"`: PASS (1 passed, 0 failed).
- **Full test suite:**
  - `dotnet test AIMonitor.sln --no-restore`: PASS (1,351 passed, 0 failed, 0 skipped across 4 test projects: Domain 218, Application 106, Infrastructure 685, Presentation.Wpf 342).
  - `python -m unittest tests/verification/test_parity_report.py`: PASS (1 passed, 0 failed).
  - `dotnet format AIMonitor.sln --verify-no-changes --no-restore`: PASS (0 format violations).
  - `dotnet build AIMonitor.sln --no-restore`: PASS (0 Warning(s), 0 Error(s)).
- **Quota status:** No quota alert or limit encountered (well above 5% threshold).
