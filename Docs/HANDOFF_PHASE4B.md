# Phase 4B handoff — Claude quota payload parser

## Objective

Implement a pure Infrastructure adapter that parses synthetic Claude OAuth usage payloads into the provider-neutral `Meter` domain contract. Preserve the current and legacy payload behavior of the Python baseline, with no HTTP, OAuth, credential, filesystem-discovery, transcript, or UI work in this slice.

## Acceptance criteria

- A Claude quota parser in `AIMonitor.Infrastructure` accepts UTF-8 JSON (string, bytes, stream, or an equivalently testable boundary) and returns an ordered read-only meter collection.
- Parsing uses `System.Text.Json` from the .NET base framework; no NuGet dependency is added.
- Current `limits` entries map known kinds exactly:
  - `session` → `Session` / `5-hour window`
  - `weekly_all` → `Weekly` / `All models`
  - `weekly_opus` → `Weekly` / `Opus only`
  - `weekly_sonnet` → `Weekly` / `Sonnet only`
  - `weekly_oauth_apps` → `Weekly` / `API apps`
- `weekly_scoped` uses model `display_name`, then surface `display_name`, then `Scoped`, yielding `Weekly` / `<name> only`.
- Unknown kinds are retained rather than dropped. Their underscore-separated kind is rendered as the Python baseline does and weekly-prefixed unknown kinds remain grouped under `Weekly`.
- Semantic ordering is delegated to `MeterReadingOrder`: session, all-model weekly, per-model/other weekly, unknown. Ordering is independent of percentage and server array order.
- Meter identity is stable and unique: known single-window kinds use their kind; scoped windows include stable scope identity/name. Structurally duplicate identities are rejected with an actionable parser/format exception rather than silently dropping, overwriting, or input-order suffixing.
- Entry `group` is preserved when present and otherwise defaults to kind through the Domain contract.
- Numeric percentages are preserved as `double`, including values above 100. Missing/null/zero percent follows the Python payload baseline and becomes `0`, but malformed, negative, NaN, or infinite values must not bypass Domain validation.
- When a matching legacy top-level block exists alongside a current limit, its numeric `utilization` overrides the rounded current `percent`, and its `locked_reason` is retained.
- If `limits` is absent, not an array, or empty, parse the legacy blocks `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`, and `seven_day_oauth_apps`, preserving their semantic order and group.
- Severity labels map through the existing Domain severity vocabulary; absent/unknown text normalizes to Normal, while warning/serious/critical map to High/VeryHigh/Critical.
- Reset timestamps accept UTC `Z`, explicit offsets, and naive ISO values (treated as UTC to match Python). Missing/empty/unreadable timestamps become null and never use the local machine time zone.
- Non-object members inside `limits` are skipped as in Python. Invalid JSON, a non-object root, duplicate meter identities, or structurally invalid numeric values fail predictably with an exception that contains no payload or secret material.
- Contract tests use committed synthetic, sanitized golden fixtures under `tests/Fixtures/Claude/`; no real token, account, PII, network, Registry, LocalAppData, or production resource is accessed.
- Tests are tagged `Category=Contract` and cover current shape, legacy-only shape, legacy precision override, stable ordering, scoped model/surface/fallback titles and keys, unknown kinds, malformed members/timestamps, severity mapping, invalid root/JSON/numbers, and duplicate identity rejection.

## Scope

Writing Owner may create/edit:

- `src/AIMonitor.Infrastructure/Providers/Claude/**`
- `tests/AIMonitor.Infrastructure.Tests/Providers/Claude/**`
- `tests/Fixtures/Claude/**`
- `tests/AIMonitor.Infrastructure.Tests/AIMonitor.Infrastructure.Tests.csproj` only to copy fixture files to test output when needed
- `Docs/ARCHITECTURE.md` only to update the implementation-status wording and document this parser boundary
- `Docs/HANDOFF_PHASE4B.md` only for a material implementation deviation or exact verification result

Do not edit Domain, Application, Presentation, package versions, workflow, Requirements, parity matrix, ADRs, Phase 4A artifacts, or repository instructions.

## Behavioral references (read-only)

- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\api.py`: `_LEGACY_BLOCK_FOR_KIND`, `_TITLES`, `_parse_timestamp`, `_title_for`, `_limits_from_payload`, `_reading_order`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\providers\claude_provider.py`: `_to_meter`
- `D:\Dev\Apps\AIMonitor\tests\test_limit_order.py`

Relevant parity anchors: PAR-005 (quota parsing/order) and PAR-013 (no invented data). PAR-001 remains foundation-only until provider clients and snapshot orchestration exist.

## Design constraints

- Parser performs no I/O beyond reading the caller-supplied JSON input.
- Never include the original payload, access token, account data, or arbitrary JSON body in exception messages or logs.
- Use deterministic ordinal comparisons for protocol identifiers and keys. User-facing per-model ordering remains case-insensitive through Domain rules.
- Do not replicate Python implementation structure one-for-one; expose a small C# parser API suitable for a future Claude HTTP client.
- Fixtures must be obviously synthetic (for example `Example Model`) and contain no credential-shaped values.
- Do not mark parity rows Verified or update R-001; Coding Owner does that after independent review and integration verification.
- Do not commit, push, publish, run the WPF app, or change machine configuration.

## Non-goals

- HTTP endpoint/header/retry/error handling
- Claude OAuth discovery, token reading, refresh, or account profile parsing
- Claude CLI transcript history/pricing/reporting
- Application provider port or refresh orchestration
- UI rendering

## Verification and handoff

Run from repository root and report exact results:

1. `dotnet test tests/AIMonitor.Infrastructure.Tests --filter Category=Contract`
2. `dotnet test tests/AIMonitor.Infrastructure.Tests`
3. `dotnet test tests/AIMonitor.Domain.Tests`
4. `dotnet build AIMonitor.sln --no-restore`
5. `dotnet format AIMonitor.sln --verify-no-changes --no-restore`

Also report files changed, fixture sanitization, contract test count, warnings, open questions, and remaining risks. Do not claim commands that were not run.
