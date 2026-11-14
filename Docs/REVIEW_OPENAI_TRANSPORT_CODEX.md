# OpenAI transport Codex independent review — 2026-10-15

Reviewed CodexProcess production diff and CodexProcessTests before reading Claude findings. User reported `Codex Usage Request Timed Out`; integrator reproduced initialize-only failure with UTF-8 BOM and success without it using installed Codex 0.162.1, sanitized environment and isolated empty CODEX_HOME. No credentials/account calls used.

Production fix: BOM-less UTF8Encoding and shared CreateStartInfo retain redirected pipes, hidden window, argument separation, sanitized environment and existing cleanup. StreamWriter byte-boundary and Unicode regression tests use the production encoding configuration.

## Findings

- P2: New real subprocess tests need bounded reads and guaranteed cleanup. The pipe probe uses synchronous ReadLine without a deadline; both probes use Dispose without a finally Kill/Wait on all assertion/timeout paths. CodexProcess.Dispose closes streams/handles but does not itself terminate the process. A failed initialize or probe can therefore hang a test or leave a child process behind. Bound reads/writes and terminate/wait in finally; keep test-owned cleanup only.
- P2: The installed-CLI smoke test silently returns when no CLI is installed, reporting a passed test although the handshake did not run. Make absence an explicit discovery-time skip or move the installed-CLI probe to an explicitly invoked offline smoke tool. Record actual environment-specific execution separately from deterministic default tests.

No production blocker found in the encoding change itself. Post-fix review and results pending.

## Post-fix disposition

Accepted test findings addressed: installed-CLI test removed from default suite, deterministic synthetic process test has bounded write/flush/read and Kill/Wait/Dispose in finally, no absent-CLI silent pass. Standalone offline initialize probe is separate from tests and documented under manual verification. Codex reread the changes. Integrator further validated cleanup paths stay inside workspace, no swallowed directory-cleanup failure, valid result object required, and unused stderr sent to DEVNULL. Offline probe and 3 Python verification tests passed independently. Packaging pipeline independently rebuilt solution with 0 warnings/errors and all 1,403 default tests passing. Owner live-account acceptance remains pending.
