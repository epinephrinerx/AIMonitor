# Phase 4C independent review

Reviewer: GPT-6 Sol (Max)  
Date: 2026-09-28  
Mode: read-only

## Findings

1. **P1 — Credential I/O is synchronous and not cancellable.** `ClaudeCredentialReader.Read` uses `File.ReadAllText`, and the live client calls it before its first await. Replace it with an injected asynchronous reader boundary using cancellable, size-bounded, strict UTF-8 file reads; test cancellation during credential reading.
2. **P2 — Response-body failures can escape the snapshot contract.** Unsupported charset and broken response streams can throw `InvalidOperationException`/`IOException`. Convert these to fixed safe error snapshots with synthetic tests.
3. **P2 — Timeout input is unvalidated.** Reject zero, infinite, unsupported negative, and excessive timeout values in the constructor; accept only a positive finite supported duration.
4. **P2 — Caller cancellation can lose races to terminal snapshot returns.** Recheck caller cancellation before all terminal returns and inside transport/body failure catches, including after parsing.
5. **P2 — Untrusted credential metadata can defeat redaction.** `ClaudeCredentials.ToString`/debug display print subscription/tier fields, and arbitrary subscription values enter detection metadata. Use a fixed redacted representation and allow only safe known plan labels for display; test credentials that repeat the token in metadata.
6. **P2 — Successful response body is unbounded.** Read the body as a stream with a strict byte cap even when `Content-Length` is absent or false; return a fixed safe error on overflow.
7. **P2 — `Retry-After` delta formatting is unsafe at boundaries.** Handle zero explicitly, round positive fractional seconds safely, and bound values too large for display without unchecked integer casts.
8. **P3 — Credential decoding/file-race/expiry edges are uncovered.** Open the file once instead of `Exists` then open, classify open races consistently, decode UTF-8 strictly, reject token control characters, and avoid avoidable epoch precision loss.
9. **P3 — Contract/ownership evidence is incomplete.** Tag credential reader/path tests `Category=Contract`; track fake handler disposal and response-content disposal explicitly; update stale architecture wording after implementation.

## Reviewer verification

- `dotnet test tests/AIMonitor.Application.Tests`: PASS — 9/9
- `dotnet test tests/AIMonitor.Infrastructure.Tests --filter Category=Contract`: PASS — 49/49
- `dotnet test tests/AIMonitor.Infrastructure.Tests`: PASS — 88/88

No file was edited and no network, real profile, real credentials, or Git operation was used by the reviewer.

## Required disposition

Writing Owner must fix findings 1–9 with focused synthetic regression tests, preserving the Phase 4C scope and no-real-access policy. Coding Owner will request a bounded final re-review and run full integration verification before updating parity/requirements.

## Re-review after first fix round

Status: **changes requested**

- Credential file size enforcement still trusts the initial `FileStream.Length` before an unbounded `CopyToAsync`; replace it with a cumulative cancellable read loop and add a file-growth regression test.
- Fractional epoch expiry is still rounded to milliseconds. Preserve `DateTimeOffset` tick precision and test instants immediately before and after the fixed clock.
- Include `HttpRequestException` from response-stream reads in safe body-failure mapping, recheck cancellation after credential parsing, and open credentials with `FileShare.Delete` so a Claude Code atomic replacement is not blocked.

## Final disposition

Status: **APPROVED**

- Credential and response reads are asynchronous, cancellable, strictly decoded, and cumulatively size-bounded.
- Timeout/cancellation races, credential redaction, safe plan metadata, `Retry-After` boundaries, tick-precision expiry, and disposal ownership have focused regression coverage.
- Final reviewer contract run passed 106/106 with no network, real profile, or real credential access.
