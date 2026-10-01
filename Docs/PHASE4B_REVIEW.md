# Phase 4B independent review

Reviewer: GPT-6 Sol (Max)  
Date: 2026-09-28  
Mode: read-only

## Findings

1. **P1 — Exception chains can expose untrusted payload fields.** Parser errors interpolate provider-supplied `kind` values, while duplicate-key inner exceptions may contain scope display names. Logging `Exception.ToString()` would therefore expose arbitrary payload/possible account metadata even though the top-level duplicate message is generic. Use fixed safe messages and do not attach an inner exception whose message contains untrusted values; add tests against the complete exception string with sentinel values.
2. **P1 — Scoped identity omits the scope type.** A model and a surface with the same `display_name` both become `weekly_scoped:<name>`, causing a false duplicate. Prefix the stable key with the selected scope type (`model` or `surface`) while retaining the same user-facing subtitle. Truly duplicate identities should still fail.
3. **P2 — Offset timestamps are not normalized to UTC.** Python `_parse_timestamp` calls `astimezone(UTC)`, while the C# parser retains the original offset. Normalize every valid timestamp to offset zero; preserve the represented instant and keep naive ISO values interpreted as UTC.
4. **P2 — Returned collection is mutable through a cast.** `MeterReadingOrder.Sort` returns an array behind `IReadOnlyList<Meter>`, and the parser returns it directly. Return genuinely read-only backing and test mutation through `IList<Meter>` is rejected without changing the collection.

## Reviewer notes

- Current/legacy fallback, known titles, legacy precision override, semantic ordering, severity mapping, malformed-member skipping, and fixture sanitization were inspected.
- Writer verification before review: contract tests 22/22; Infrastructure tests 24/24; Domain tests 136/136; build and format passed.
- The review process was stopped after it became unresponsive during additional exploratory edge-case checks. Findings above had already been confirmed from source and tests; no file was edited by the reviewer.

## Required disposition

Writing Owner must fix findings 1–4 and add focused regression tests. Coding Owner will request a bounded final disposition check and run integration verification before marking parity progress.

## Final disposition

Status: **APPROVED**

- Exception messages and full exception chains no longer retain untrusted payload/scope values.
- Scoped model/surface identities are distinct while true duplicate identities still fail.
- Valid timestamps normalize to UTC and returned meters use genuinely read-only collection backing.
- Focused contract suite passed 29/29 during final reviewer verification.
