# Phase 4C handoff — Claude live quota client and read-only credential discovery

## Objective

Connect the Phase 4B Claude quota parser to a cancellable, testable live-quota adapter: define the Application provider port, discover Claude Code OAuth credentials read-only, call the Claude usage endpoint through injected `HttpClient`, and return a provider-neutral `ProviderSnapshot`. Automated tests must use only temp files, fake time, and fake HTTP transport.

## Acceptance criteria

### Application boundary

- `AIMonitor.Application` declares a provider client port that asynchronously returns `ProviderSnapshot` and accepts a `CancellationToken` plus a small request/options value suitable for future history range/metric support.
- Application owns an `IClock`-style UTC time boundary used for deterministic credential-expiry and fetched-at behavior.
- Application tests cover validation/default behavior of new request values without referencing Infrastructure.

### Claude credential discovery

- Resolve Claude Code credentials at `<CLAUDE_CONFIG_DIR>/.credentials.json` when a nonblank override is supplied; otherwise at `<user-profile>/.claude/.credentials.json`. Resolution is deterministic from explicit inputs and does not expand or write paths.
- Read the credential file only; never create, update, refresh, rename, chmod, or delete it.
- Parse `claudeAiOauth.accessToken`, `expiresAt`, `subscriptionType`, and `rateLimitTier` using `System.Text.Json`.
- Accept numeric expiry in epoch seconds or milliseconds (values above `10_000_000_000` are milliseconds), normalize to UTC, and treat `expiresAt <= clock.UtcNow` as expired. Missing/invalid/nonpositive/out-of-range expiry means unknown rather than invented.
- Secret-bearing credential objects must not expose the access token through `ToString`, exception text, logs, debug display, equality diagnostics, or record-generated representation.
- Missing file, unreadable/malformed/non-object JSON, missing/non-string/blank access token, and expired credentials map to actionable detection/snapshot states without throwing to the caller and without exposing file contents or tokens.
- Detection uses provider `claude`, source id `claude_code`, source label `Claude Code login`, and refresh hint `Run \`claude\` in a terminal to refresh the login.` An expired credential remains a candidate and maps to `Expired`; a valid credential maps to `Connected`; missing/invalid data maps to `NotConnected`.

### HTTP and snapshot behavior

- Infrastructure implements the Application provider port for Claude live quota using an injected `HttpClient`, injected credential reader/path inputs, and injected clock. Do not instantiate a real client inside tests and do not dispose a caller-owned client.
- Send exactly one `GET https://api.anthropic.com/api/oauth/usage` request with:
  - `Authorization: Bearer <access token>`
  - `anthropic-beta: oauth-2025-04-20`
  - `Accept: application/json`
  - `User-Agent: AIUsageMonitor/2.0`
- Check cancellation before credential I/O and pass the same token through credential reading, `HttpClient.SendAsync`, response-body reading, and any subsequent work. An already-cancelled call performs no file or HTTP access; cancellation propagates as `OperationCanceledException`, not an error snapshot.
- A valid response uses `ClaudeQuotaParser` and returns a snapshot with provider id `claude`, configured true, ordered meters, fetched-at from injected UTC clock, detection metadata, setup hint, and the existing value note. This slice returns no history/stats/account profile and does not invent them.
- Missing/invalid credentials return configured false, unauthorized true, no HTTP call, no meters, `NotConnected` detection, and an actionable sign-in error.
- Expired credentials return configured true, unauthorized true, no HTTP call, no meters, `Expired` detection, and an actionable refresh error.
- HTTP 401/403 returns unauthorized true with the actionable Claude Code refresh message. HTTP 429 returns a rate-limit error and uses a syntactically valid `Retry-After` delta when present, without retrying. Other non-success statuses return a safe status-based error without echoing response bodies.
- Transport/DNS failure, timeout, invalid quota JSON, and domain/parser failure become safe error snapshots (configured remains true and detection remains Connected); no exception or response body/token escapes. Caller cancellation is the only expected propagated operational exception.
- Response and exception disposal is correct. The adapter must not retry automatically.
- Snapshot collections remain immutable; errors never include the token, raw credential JSON, raw response body, or arbitrary server-provided error text.

### Tests and fixtures

- Infrastructure tests use temp directories unique to each test and synthetic credential JSON only. They must never inspect `%USERPROFILE%`, the real `CLAUDE_CONFIG_DIR`, real `.claude`, Registry, LocalAppData, or network.
- HTTP tests use a hand-written fake `HttpMessageHandler` that captures request metadata and returns synthetic responses.
- Cover success, seconds/milliseconds/unknown expiry, exact-expiry boundary, override/default path resolution, missing/corrupt/tokenless credential, token redaction, already-cancelled operation, request headers/method/URI, 401, 403, 429 with/without valid delta, other status with secret-like response body, transport error, timeout/cancellation distinction, invalid payload, parser failure, no retry, and injected-client ownership.
- Provider HTTP/credential tests are tagged `Category=Contract` where they verify external formats/protocols; no real credentials or production access.

## Scope

Writing Owner may create/edit:

- `src/AIMonitor.Application/Providers/**`
- `src/AIMonitor.Application/Time/**`
- `src/AIMonitor.Infrastructure/Providers/Claude/**`
- `tests/AIMonitor.Application.Tests/Providers/**`
- `tests/AIMonitor.Application.Tests/Time/**`
- `tests/AIMonitor.Infrastructure.Tests/Providers/Claude/**`
- `tests/AIMonitor.TestSupport/**` only for reusable hand-written clock/HTTP-independent fakes needed by both Application and Infrastructure tests
- `Docs/ARCHITECTURE.md` and `Docs/TESTING.md` only where the implemented port/client boundary or exact test command/status must replace stale wording
- `Docs/HANDOFF_PHASE4C.md` only for material deviations or exact verification results

Do not edit Domain, Presentation, package versions, workflow, Requirements, parity matrix, ADRs, prior handoff/review artifacts, or repository instructions.

## Behavioral references (read-only)

- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\credentials.py`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\detection.py`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\providers\sources.py` (`_claude_code_login`, `CLAUDE_SOURCES`)
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\api.py` (`_get`, endpoint constants/error mapping)
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\providers\claude_provider.py` (`fetch` live-quota portion only)
- `D:\Dev\Apps\AIMonitor\tests\test_refresh_lifecycle.py` (Claude cancellation cases)

Relevant parity anchors: PAR-001 (provider snapshot/client contract), PAR-002 (detection state/source/reason), PAR-003 (cancellation foundation), PAR-005 (already verified parser), PAR-029 (external login is read-only).

## Design constraints

- Standard .NET libraries only; do not add NuGet packages, DI containers, logging frameworks, or filesystem abstraction packages.
- Tests may exercise the real filesystem only inside disposable per-test temp directories. Use explicit path inputs so production profile/environment values are never touched by automated tests.
- Prefer a small credential-reader result/discriminated outcome over exception-driven control flow for expected missing/invalid/expired states.
- Do not retain the access token longer than the request requires; never store it in Domain or `ProviderSnapshot`.
- Do not parse JWT claims or add profile/account HTTP calls in this slice.
- `HttpClient.Timeout`/linked timeout design must distinguish caller cancellation from adapter timeout deterministically. Make timeout injectable for tests; default production timeout is 15 seconds.
- Treat every response body and credential field as untrusted. Exception chains used inside returned errors must be sanitized.
- Do not run a real WPF app, real Claude CLI, or provider network request.
- Do not mark parity rows Verified or update R-001; Coding Owner does that only after independent review and integration checks.
- Do not commit, push, publish, install tools, or change machine configuration.

## Non-goals

- Claude profile endpoint/account enrichment
- Claude transcript ingestion/history/pricing/stat tiles
- Refresh coalescing across providers or UI-thread dispatch
- Saved AIMonitor credentials/DPAPI
- OpenAI/Gemini integration or WPF composition root wiring
- Real network/manual login smoke testing

## Verification and handoff

Run from repository root and report exact results:

1. `dotnet test tests/AIMonitor.Application.Tests`
2. `dotnet test tests/AIMonitor.Infrastructure.Tests --filter Category=Contract`
3. `dotnet test tests/AIMonitor.Infrastructure.Tests`
4. `dotnet test tests/AIMonitor.Domain.Tests`
5. `dotnet build AIMonitor.sln --no-restore`
6. `dotnet format AIMonitor.sln --verify-no-changes --no-restore`

Also report files changed, exact test counts, credential/transport isolation evidence, warnings, open questions, and remaining risks. Do not claim commands that were not run.
