# R-012 — AGY-only active integration

Date: 2026-10-16. Status: ready for user acceptance.

The third active provider is now AGY. It reads only `agy -p /usage --output-format json`, showing Gemini and Claude/GPT shared quota pools with session and weekly windows. Existing standalone Claude/OpenAI remain separate. No Google fallback is registered, and no token, spend or history is invented from quota fractions.

Changed areas:
- Infrastructure `Providers/Agy`: shell-free hidden runner, bounded concurrent pipe reads, deadline/cancellation and owned-child cleanup; strict envelope/fraction/reset mapping; sanitized failures.
- Application settings and connection store: migrate legacy active id/Enabled without copying secrets or project settings; AGY connections are keyless.
- WPF composition, provider metadata, tabs, connections, tray and report names; ScreenEvidence and synthetic AGY fixture. GaugeControl was not modified.
- Synthetic provider/process/settings/UI tests; README, Requirements, architecture/testing/parity notes and review triage.

Verification actually run:

| Command | Result |
|---|---|
| `dotnet test AIMonitor.sln --no-restore --verbosity minimal` | 1,486 passed: Domain 218, Application 117, Infrastructure 785, Presentation 366; zero failures/skips |
| `dotnet build AIMonitor.sln --no-restore` | 0 warnings, 0 errors; final test invocation also rebuilt the title change |
| `dotnet format AIMonitor.sln --verify-no-changes --no-restore` | Passed after targeted formatting |
| `git diff --check` | Passed; Git emitted CRLF normalization notices |
| `dotnet run --project tools/AIMonitor.ScreenEvidence -- tests/Fixtures/Agy/screens.json artifacts/verification/r012-wpf` | 40 synthetic WPF client-area renders; inspected AGY dashboard and keyless connection dialog |
| Separate read-only installed AGY `/usage` probe | Successful JSON contract, two groups/four windows, no model turns; account payload not saved as fixture |

Earlier verification failures (obsolete overlapping writer tests, stale Gemini assertions, Claude connection-extra regression and whitespace) were corrected before the passing run. Automated tests use synthetic data and fake processes only.

Opened `src/AIMonitor.Presentation.Wpf/bin/Debug/net10.0-windows/AIUsageMonitor.exe`, PID 46224, and confirmed its path/window. The installed older instance PID 43948 was closed with path checking to release the single-instance lock; its files were not replaced. No installer/publish/commit was performed.

User check: select AGY, Refresh, compare both pools and reset times against AGY /usage, then inspect dashboard/widget/tray at the usual window size. Live account visual correctness and real desktop DPI remain pending user acceptance. Legacy Gemini source and stored settings/secrets are retained inactive. Review and integration details: `REVIEW_R012.md`.
