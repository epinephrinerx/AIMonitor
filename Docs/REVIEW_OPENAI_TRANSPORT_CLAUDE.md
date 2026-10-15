# OpenAI transport Claude independent review — 2026-10-15

Reviewer: Claude Fable 5.1 High, independent read-only review, no other review/handoff findings read. CLI exited 0. No credentials/live accounts/registry/install/commit/push/subagents.

## Findings

- Medium: installed-Codex test Kill does not WaitForExit before deleting its temp fixture. Temp sqlite files remained after runs. Kill only on success also permits child leakage after assertion/timeout failure. Use bounded Kill/Wait in finally.
- Medium: default Contract test spawning real Codex App Server violates TESTING.md fake-transport policy and depends on installed CLI version/notification ordering. Move real CLI handshake to an explicitly invoked standalone offline smoke probe; keep default tests synthetic.
- Low: absent CLI silently returns as passed rather than visible skip.
- Low: dumper subprocess ReadLine has no timeout.

## Evidence

Reviewer actually ran `dotnet test tests/AIMonitor.Infrastructure.Tests --filter "FullyQualifiedName~CodexProcessTests|FullyQualifiedName~CodexAppServerLauncherTests|FullyQualifiedName~CodexJsonRpcClientTests"`: 36 passed, 0 failed. This included local CLI and synthetic dumper probes before policy fix. Anonymous pipe experiment confirmed Encoding.UTF8 emits EF-BB-BF and UTF8Encoding(false) starts at JSON `{`. Production refactor preserves arguments/environment/redirect/cleanup; Unicode retained. No newly leaked Codex process observed, but test-owned temp directories could remain.

## Triage

Accept cleanup, bounded I/O and default-suite policy findings. Move installed CLI smoke to a standalone manual offline verification tool with explicit executable path, isolated empty CODEX_HOME, stripped credential environment, initialize only, matching response-id handling, deadlines and guaranteed child cleanup. No real account/login/token call. Document distinction from default tests. Do not loosen default fake-provider test policy.

## Fix disposition

Agy removed installed-Codex smoke from default tests and retained 4 deterministic/synthetic pipe regressions. Synthetic child I/O is bounded; Kill/Wait/Dispose is in finally. Standalone Python probe and TESTING.md document explicit manual initialize-only smoke. Codex post-fix review found accepted issues addressed; integrator independently ran the probe and 3 Python verification tests successfully, and packaging build/full tests passed with 1,403 tests. Claude did not perform a separate post-fix rereview.
