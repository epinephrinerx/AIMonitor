# R-011 — Independent Codex review

Reviewed source without reading the Claude report. Writer still finishing tests at review time; all findings below must be triaged against final source.

- Medium: `BuildSupplementary` invents Claude `Anthropic Pro / Max` plan when no plan stat exists; replace with Not reported. Missing OpenAI credits should not imply none.
- Medium: numeric fallback converts double to Int64 after comparing with long.MaxValue as double; 2^63 rounds to the same value and can cast to negative. Prefer strict TryGetInt64 for integer service fields and regression tests for 9223372036854775808 and 1e30.
- Medium: XAML horizontal StackPanels and DockPanels contain unwrapped dynamic labels/values. Long grouped labels, credit balances or limit reasons may overflow a 300px dashboard. Use constrained wrapping layouts and render long-label fixture at narrow widths.
- Medium: legacy stat filtering applies to every provider although the supplementary section is Claude/OpenAI only. Keep unrelated provider stats intact.
- Low: `configuredNow || HasSnapshot` is always true after setting HasSnapshot; avoid presenting account telemetry as populated for unconfigured accounts. Preserve metadata-only failed snapshots when stats are present.
- Low: earned reset `expiresAt` is Unix seconds; reused epoch milliseconds heuristic can invent a valid date from an invalid seconds value. Validate as seconds with bounds and reject invalid dates; don't label a capped-list minimum as globally authoritative next expiry.

No new account mutations or requests observed. Existing gauge construction and GaugeControl remain outside the changes. Existing stale StatsSummary clearing is a reasonable fix for refresh behavior.
