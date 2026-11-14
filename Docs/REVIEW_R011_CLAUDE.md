# R-011 — Independent Claude review and triage

Claude Fable 5.1 high reviewed source read-only without seeing Codex findings and exited. No tests run by reviewer.

- High: fabricated Claude Pro/Max fallback — fixed to Not reported.
- High: section always visible after HasSnapshot — fixed for setup; unknown message no longer claims server returned null.
- Medium: Admin API path labelled Codex — manual/environment API mode explains unavailable Codex telemetry.
- Medium: refresh collapses details — expansion preserved and regression tested.
- Medium: unbound activity note leaves empty card — note bound.
- Low: UTC expiry differs from gauge-local time — explicit Z retained and labelled known expiry.
- Low: 2^63 conversion — strict integer parsing and boundary tests.
- Low: OS Expander style, unused props, existing ScreenEvidence friend assembly and wording — dark view inspected; preexisting friend assembly unchanged; cosmetic cleanup deferred.

Suggested gaps addressed with setup/API/expansion/boundary tests and actual WPF synthetic renders
with present metadata and expanded long-label narrow state. Integrator full results in HANDOFF_R011.md.
