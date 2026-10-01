# Phase 4A handoff — domain contracts and pure display rules

## Objective

Implement the first behavior-bearing vertical slice of AIMonitor 2.0 in C#: provider-neutral domain contracts plus deterministic meter severity, reading order, and reset-display formatting. Port observable behavior from the Python reference without copying its framework structure.

## Acceptance criteria

- `AIMonitor.Domain` exposes immutable provider-neutral models sufficient to represent meters, stats, history, account/error state, fetch time, and detection metadata without referencing Infrastructure, WPF, or provider-specific payload types.
- Optional data remains optional: in particular a meter percentage and reset timestamp may be absent, and no denominator or percentage is invented.
- Effective meter severity applies local thresholds of 75% (High) and 90% (Critical), combines them with server severity, and always selects the more severe result. Missing percentage preserves server severity. Unknown server severity must not outrank a known severity accidentally.
- Severity has a non-colour representation (glyph plus user-facing label) covering Normal, High, Very high, and Critical.
- Meter reading order is stable and semantic: session first, all-model weekly second, per-model weekly next in case-insensitive subtitle order, unknown kinds last; ordering is independent of percentage and input order and drops nothing.
- Reset display can represent both a coarse countdown and a local clock value when a reset timestamp exists. It uses an injected/reference `now` value rather than reading the system clock inside the rule, and returns no reset display when the timestamp is absent.
- Countdown boundaries preserve the Python baseline: elapsed=`now`, under 60 seconds=`under a minute`, minutes, hours/minutes, and days/hours.
- Domain tests cover happy paths and boundaries, including 74.9/75/89.9/90, worse server severity, null percentage/reset, expired reset, stable ordering, and culture/time-zone-independent behavior.
- Existing architecture smoke tests continue to pass; no real credentials, network, Registry, LocalAppData, or WPF process are used.

## Scope

Writing Owner may create/edit:

- `src/AIMonitor.Domain/**`
- `tests/AIMonitor.Domain.Tests/**`
- `Docs/HANDOFF_PHASE4A.md` only if documenting a material implementation deviation or verification result

Do not edit Application, Infrastructure, Presentation, project topology, package references, workflow, Requirements, parity matrix, ADRs, or repository instructions.

## Behavioral references (read-only)

- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\providers\base.py`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\theme.py`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\formatting.py`
- `D:\Dev\Apps\AIMonitor\ai_usage_monitor\api.py` (`_reading_order` only; provider payload parsing is out of scope)
- `D:\Dev\Apps\AIMonitor\tests\test_days_and_severity.py`
- `D:\Dev\Apps\AIMonitor\tests\test_limit_order.py`

Relevant parity anchors: PAR-001 (contract foundation only), PAR-005 (ordering only), PAR-013 (no invented percentage), PAR-016, and PAR-017.

## Design constraints

- Keep Domain at `net10.0` and free of Windows/WPF/HTTP/filesystem dependencies.
- Prefer small records/enums/value objects and stateless functions/services. Avoid speculative abstractions, dependency injection packages, and provider-specific JSON parsing.
- Public APIs must validate structurally invalid inputs where useful, but optional provider data is not invalid merely because it is absent.
- Use `DateTimeOffset` for instants. Reset formatting must accept an explicit `now` and `TimeZoneInfo` (or an equivalently deterministic boundary) so tests do not depend on wall clock or machine time zone.
- Preserve user-facing English strings from the baseline for this slice; localization is out of scope.
- Do not add NuGet dependencies or relax warnings-as-errors/nullable settings.
- Do not mark a parity row Verified or update R-001; the Coding Owner will do that only after review and final verification.
- Do not commit, push, publish, run the WPF app, or change machine configuration.

## Non-goals

- Provider JSON parsing or Claude/OpenAI/Gemini adapters
- Pricing, detection-source resolution, refresh orchestration, history ingestion, reporting, settings, credentials, Windows integration, or UI rendering
- Matching Python class/function names one-for-one
- Colour tokens or WPF brushes

## Verification and handoff

Run from repository root and report exact results:

1. `dotnet test tests/AIMonitor.Domain.Tests`
2. `dotnet test tests/AIMonitor.Application.Tests`
3. `dotnet build AIMonitor.sln --no-restore`
4. `dotnet format AIMonitor.sln --verify-no-changes --no-restore`

Also report files changed, behavior decisions, test count, warnings, open questions, and remaining risks. Do not claim commands that were not run.
