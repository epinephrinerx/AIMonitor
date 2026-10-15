# AI Usage Monitor 2.0.1

- Replace the active Gemini Cloud Monitoring provider with AGY CLI shared quota pools. Install AGY, sign in there and make agy.exe available on PATH; then refresh AIMonitor.
- Show Gemini and Claude/GPT session and weekly pools separately from standalone Claude and Codex quotas. AGY does not require an API key or Google project.
- Add a supplementary details section below the existing gauges for Claude/OpenAI; preserve the gauge control.
- Improve Codex quota parsing and process handling, provider error sanitization, startup preference reconciliation and installer packaging.
- Add synthetic regression tests, CI and reproducible screen evidence tools.

Windows x64 downloads: use **AIUsageMonitor Setup 2.0.1.exe** to install, or **AIUsageMonitor 2.0.1.exe** for the self-contained portable app. Keep the previous installer for rollback. Existing settings and legacy Gemini credentials are retained; the legacy Gemini provider is inactive.

Validation: 1,486 synthetic .NET tests passed before release packaging; 40 WPF client-area renders reviewed for the changed screens. Full real-desktop DPI/tray and clean-machine/Python upgrade smoke matrices have not been repeated for this release. AGY requires a working existing CLI login, and future CLI schema changes may require an update. Executables are unsigned.

CI portability fixes: pin the CSV report's generated date so its fixture does not age out of the requested range; test UTF-8 bytes through an in-process OS pipe without depending on a PowerShell child; use a resize target that fits the hosted runner desktop. The first CI failure is retained as run 38774520285. Local format, all 1,486 tests, three verification-tool tests, dependency audit and the self-contained/installer pipeline were run during release preparation.
