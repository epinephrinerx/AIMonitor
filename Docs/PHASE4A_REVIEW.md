# Phase 4A independent review

Reviewer: GPT-6 Sol (Max)  
Date: 2026-09-27  
Mode: read-only

## Findings

1. **P1 — Provider-neutral contract is not rich enough for the approved baseline.** `UsageHistory` carries only one scalar value per day, while the baseline history view must retain buckets/series, by-model and by-project breakdowns, range days, selected metric, and project label. `DetectionInfo` carries only source/description, while the baseline needs connection state (connected/limited/expired/not connected), source identity/label, reason or hint, account, and candidates. Expand immutable domain shapes without adding ingestion or resolution behavior.
2. **P1 — Invalid enum values can outrank Critical.** `Meter` accepts a cast value such as `(Severity)999`; `SeverityRules.Combine` compares its raw ordinal and returns it, after which `SeverityLabels.Describe` throws. Normalize or reject invalid enum values consistently and add regression tests. Unknown textual server labels must continue to normalize to Normal.
3. **P2 — Empty meter subtitle is valid baseline data.** `Meter` rejects blank subtitle, but the Python suite explicitly constructs a meter with `subtitle=""` in `tests/test_partial_success.py`. Permit empty subtitle while rejecting null if appropriate.
4. **P2 — Immutable records leak mutable collections.** `ProviderSnapshot` and `UsageHistory` retain caller-owned `List<T>` instances behind `IReadOnlyList<T>`. Copy inputs into immutable/read-only storage and test that subsequent source-list mutation cannot alter the snapshot.
5. **P2 — Meter identity and ordering inputs are incomplete.** The baseline meter includes a stable key, and semantic ordering also recognizes a session group. Add provider-neutral key/group data or an equivalent explicit normalized classification. Ensure ordering has deterministic tie-breakers independent of source order and retains every meter.
6. **P3 — Reset formatting edge coverage.** Current arithmetic matches Python for positive/negative sub-second values and `TimeZoneInfo.ConvertTime` handles DST, but explicit boundary tests for sub-second elapsed values and a DST transition would reduce regression risk.

## Reviewer verification

- `dotnet test tests/AIMonitor.Domain.Tests`: PASS — 74/74
- `dotnet test tests/AIMonitor.Application.Tests`: PASS — 2/2
- `dotnet build AIMonitor.sln --no-restore`: PASS — 0 warnings, 0 errors
- `dotnet format AIMonitor.sln --verify-no-changes --no-restore`: PASS

## Required disposition

Writing Owner should fix findings 1–5 and add focused tests. Finding 6 is recommended and should be included if it can be tested deterministically without platform-specific assumptions. No parity row is Verified until Coding Owner completes final integration checks.

## Re-review after first fix round

Status: **changes requested**

- Collection inputs are defensively copied, but properties still expose runtime arrays/dictionaries that consumers can cast and mutate. Use genuinely read-only backing and test mutation through the exposed property, not only mutation of the original input.
- Validate history structure: nonblank labels, finite nonnegative values, positive day range, and nonblank metric/project label.
- A meter key that defaults to kind does not distinguish same-kind windows. Make stable identity an enforced contract and ensure duplicate/ambiguous identities cannot make ordering depend on input order; add reversed-input/duplicate tests.
- Correct the `DetectionCandidate` comment: Python candidates are sources that yielded credentials, not every source attempted.

## Final disposition

Status: **APPROVED**

- Domain collections now use genuinely read-only backing over defensive copies and mutation attempts are covered by tests.
- History structure validation, stable required meter identity, duplicate-key rejection at both snapshot and sorting boundaries, and detection candidate semantics are corrected.
- Final reviewer check confirmed the last two fixes with no remaining finding.
