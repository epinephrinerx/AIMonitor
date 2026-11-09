# SPEC R-014 — ccusage integration and Claude pricing/transcript fixes

Coding Owner spec, 2026-11-09. Requirement: `Requirements.md` R-014. Workflow: `Docs/AI_WORKFLOW.md` §5.8
(Inspector stage A on this spec → Writing Owner + Test Author in parallel → Inspector stage B + Reviewer1/2 → Integrator).

Evidence used for this spec (not committed, Coding Owner scratchpad):
- ccusage 20.0.48 source (MIT, `apps/ccusage/LICENSE`) and its `docs/guide/json-output.md`.
- One real run on this machine: `ccusage daily --json --by-agent --sections daily,monthly,session` → exit 0, 6.9 s,
  471 KB, 38 daily / 3 monthly / 405 session rows, agents `antigravity, claude, codex, copilot, opencode, zcode`.
- Official Anthropic pricing page `platform.claude.com/docs/en/about-claude/pricing` read 2026-11-09.

## Phases and owners

| Phase | Scope (files may only be touched by this phase) | Writing Owner | Starts after |
|---|---|---|---|
| P1 | ccusage data source: Domain records, Application port + cache, Infrastructure runner/parser, composition registration only (no UI) | WO2 (opencode, kimi-k2.7-code) | Inspector A on P1 |
| P2a | `src/AIMonitor.Domain/ClaudeModelPricing.cs` and its tests | WO1 (opencode + Ollama qwen-coder-12k) — small, self-contained | Inspector A on P2a |
| P2b | `ClaudeTranscriptStore.cs`, `ClaudeTranscriptPathResolver.cs`, the `CLAUDE_CONFIG_DIR` read in `App.xaml.cs`, their tests | WO3 (agy, Gemini 3.8 Flash) | P2a merged (shares cost code) |
| P3 | "All agents" tab (View/ViewModel) | spec written after P1 merges | P1 |
| P4 | ccusage rows in existing tabs' supplementary section | spec written after P3 merges | P3 |

P1 and P2a/P2b touch disjoint files and may run in parallel. Test Author writes tests for every phase from this
spec only (§5.6: must not read the implementation first).

---

## P1 — ccusage data source

### P1.1 Domain (`src/AIMonitor.Domain/AgentUsage.cs`, pure, no I/O)

- `TokenCounts(long Input, long Output, long CacheCreation, long CacheRead, long Total)`
  - every value ≥ 0; `Total ≥ Input + Output + CacheCreation + CacheRead` (else `ArgumentOutOfRangeException`).
  - `Other => Total − (Input + Output + CacheCreation + CacheRead)` — tokens ccusage counts but does not bucket
    (observed for antigravity and opencode, e.g. total 122,410,471 vs bucket sum 122,095,834 → Other 314,637).
- `ModelUsage(string Model, TokenCounts Tokens, decimal? CostUsd)`
  - `CostUsd == null` means **unknown price** (ccusage `missingPricing: true`). Never 0 for unknown.
  - `Model` non-blank, kept exactly as ccusage printed it.
- `AgentUsage(string Agent, TokenCounts Tokens, IReadOnlyList<ModelUsage> Models)`
  - `Agent` = ccusage agent id, trimmed, lower-case invariant (`claude`, `codex`, `antigravity`, `opencode`, …).
    Unknown ids are kept, never dropped.
  - `PricedCostUsd => sum of non-null model costs`; `HasUnpricedModels => any model CostUsd == null`.
- `DailyAgentUsage(DateOnly Date, IReadOnlyList<AgentUsage> Agents)`
- `MonthlyAgentUsage(int Year, int Month, IReadOnlyList<AgentUsage> Agents)` (Month 1–12)
- `SessionAgentUsage(string SessionId, DateTimeOffset LastActivity, AgentUsage Usage)`
- `AgentUsagePricing` enum: `OnlineRequested`, `Offline` (see decision OP3).
- `AgentUsageReport(IReadOnlyList<DailyAgentUsage> Daily, IReadOnlyList<MonthlyAgentUsage> Monthly,
  IReadOnlyList<SessionAgentUsage> Sessions, IReadOnlyList<string> UnpricedModels, AgentUsagePricing Pricing,
  DateTimeOffset RetrievedAt, int SkippedRows)`
  - Daily sorted by date ascending, Monthly by (year, month) ascending, Sessions by LastActivity descending.
  - `UnpricedModels` distinct, ordinal-sorted.

### P1.2 Application (`src/AIMonitor.Application/AgentUsage/`)

- Port `IAgentUsageSource { Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct); }`
- `AgentUsageUnavailableException(AgentUsageErrorKind kind, string message)`; kinds:
  `NotInstalled`, `Timeout`, `OutputTooLarge`, `Failed`, `InvalidResponse`. Messages are user-facing, actionable,
  and never contain stdout/stderr text, file paths or environment values.
- `CachedAgentUsageSource : IAgentUsageSource` (uses existing `IClock`):
  - returns the cached report when the last **successful** read for the same `since` is younger than
    `MinInterval` (default 15 min, constructor parameter); otherwise reads through.
  - `ReadAsync(..., force)` overload bypasses the interval.
  - on failure: rethrow the exception **and** keep the previous good report available via
    `LastGood` (`AgentUsageReport?`) and `LastError` (`AgentUsageUnavailableException?`).
  - concurrent callers share one in-flight read (no parallel ccusage processes).
  - `OperationCanceledException` from the caller's token is rethrown and is not stored as `LastError`.

### P1.3 Infrastructure (`src/AIMonitor.Infrastructure/AgentUsage/`)

**Runner `CcUsageRunner`** (mirror `Providers/Agy/AgyUsageRunner.cs`: injectable resolver, process factory,
timeout and output limit; same pipe-drain and kill-on-deadline behaviour).

- Resolve executable by scanning `PATH` directories in order for `ccusage.exe`, then `ccusage.cmd`.
  Not found → `NotInstalled` ("ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.").
  Never invoke `npx`, never download.
- Arguments via `ProcessStartInfo.ArgumentList`, constants only:
  `daily --json --by-agent --sections daily,monthly,session` plus `--since yyyyMMdd` (invariant) when `since` is set.
  No user-provided text is ever passed.
- `UseShellExecute=false`, no window, stdin closed, `NO_COLOR=1` added to the environment.
- Pricing: first run without `--offline`. If it times out or exits non-zero, run once more with `--offline` appended;
  success of the second run → `Pricing = Offline`, first-run success → `OnlineRequested`. Both fail → error of the **second** run.
- Timeout per run 120 s; stdout limit 32 MiB (exceeding → kill, `OutputTooLarge`); stderr drained and discarded,
  capped at 1 MiB.
- Exit code ≠ 0 → `Failed` ("ccusage could not read usage. Run `ccusage daily` in a terminal to see why.").

**Parser `CcUsageJsonParser.Parse(string json, AgentUsagePricing pricing, DateTimeOffset retrievedAt)`**

Structural rules (violation → `InvalidResponse`, whole report rejected):
- Root is an object containing arrays `daily`, `monthly`, `session` (each may be empty).
- More than 20,000 rows in any section, or more than 500 `modelBreakdowns` in one row → `InvalidResponse`.
- Max JSON depth 64.

Row rules (violation → row skipped, `SkippedRows += 1`; if every row of every section was skipped and at least one
row existed → `InvalidResponse`):
- `daily[i].period` matches `yyyy-MM-dd` (invariant, real calendar date). Use `daily[i].agents[]`; the row-level
  `agent: "all"` totals are ignored (they are derived). Row without an `agents` array → skipped.
- `monthly[i].period` matches `yyyy-MM`; same `agents[]` rule.
- `session[i]`: `period` = session id (non-blank string ≤ 256 chars), `agent` string, `metadata.lastActivity`
  ISO-8601 **with** offset or `Z`; the row itself is the `AgentUsage` (no `agents[]`).
- Token fields `inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens, totalTokens`: JSON integers
  0 ≤ n ≤ 2^53 − 1. Missing, fractional, negative, string → invalid row. `totalTokens` < bucket sum → invalid row.
- `modelBreakdowns[j]`: `modelName` non-blank; same four token fields (no `totalTokens` here: model Total = bucket sum);
  `cost` finite number ≥ 0 parsed as `decimal`; `missingPricing: true` → `CostUsd = null` regardless of `cost`.
  An invalid model entry invalidates its row.
- Agent `Tokens` come from the agent object's own fields, not from summing models.
- `totals.unpricedModels` optional string array → `UnpricedModels`; absent → empty.
- Unknown extra properties are ignored everywhere.

**Source `CcUsageAgentUsageSource : IAgentUsageSource`** = runner + parser + `IClock` for `retrievedAt`.

**Composition**: register `CachedAgentUsageSource(new CcUsageAgentUsageSource(...))` in `App.xaml.cs`; nothing
consumes it until P3. Startup must not run ccusage.

### P1.4 Tests (Test Author)

- Synthetic fixtures only under `tests/Fixtures/CcUsage/` (shape of the real run, invented numbers/ids/model names).
  Never run the real ccusage in tests; runner tests use a fake process like `AgyUsageRunner` tests do.
- Must cover: Other tokens (oracle: total 1,000 with buckets 100+200+300+350 = 950 → Other 50); `missingPricing`
  → null cost and excluded from `PricedCostUsd` (oracle: models 1.25 + unknown → 1.25, `HasUnpricedModels`);
  each skip rule; each structural rule; all-rows-skipped; session ordering; online→offline fallback; both-fail;
  timeout; output limit; not installed; cache interval (14:59 cached, 15:00 re-read); force; single in-flight read;
  failure keeps `LastGood`; caller cancellation not stored.

---

## P2a — Claude pricing (official prices, read 2026-11-09)

Replace the fixed cache-read multiplier with per-model rates. `ClaudeModelRate` gains `CacheReadPerMillion` as an
explicit constructor value; 5m write stays ×1.25 and 1h write ×2.0 of input (true for every row below).

| Model id | Input | Output | Cache read | Notes |
|---|---|---|---|---|
| claude-fable-5-1 | 10 | 50 | **0.25** | was 1.00 (×0.1) |
| claude-mythos-5-1 | 10 | 50 | **0.25** | |
| claude-fable-5 / claude-mythos-5 | 10 | 50 | 1.00 | unchanged |
| claude-opus-5-5 | **4** | **20** | **0.20** | new; was prefix fallback 5/25/0.50 Estimated |
| claude-opus-5, 4-8, 4-7, 4-6 | 5 | 25 | 0.50 | unchanged |
| claude-opus-4-5 | 5 | 25 | 0.50 | new |
| claude-opus-4-1, claude-opus-4 | **15** | **75** | 1.50 | new; was prefix 5/25 Estimated |
| claude-sonnet-5-5 | 2 | 10 | **0.10** | new; was prefix 3/15/0.30 Estimated |
| claude-sonnet-5 | 2 | 10 | 0.20 | unchanged |
| claude-sonnet-4-6, 4-5, claude-sonnet-4 | 3 | 15 | 0.30 | 4-5 and 4 new |
| claude-haiku-5-5 | 0.10 / **0.50** | 0.50 / **2.50** | 0.01 / **0.05** | second value when the request's prompt > 100,000 tokens |
| claude-haiku-4-5 | 1 | 5 | 0.10 | unchanged |

- Haiku 5.5 tier is **per request**: prompt = input + cache creation + cache read of that one transcript record;
  > 100,000 → all of that record's tokens at the higher rates. This forces cost to be computed per record at ingest,
  not per day/model aggregate.
- Prefix fallbacks stay `Estimated`, with values updated to the current generation: opus 4/20/0.20,
  sonnet 2/10/0.10, haiku 0.10/0.50/0.01, fable 10/50/0.25, mythos 10/50/0.25.
- Oracles for tests: opus-5-5, 1M input + 1M output + 1M cache read → 4 + 20 + 0.20 = 24.20;
  fable-5-1, 2M cache read → 0.50; haiku-5-5 record with 60k input + 50k cache read (110k) and 1k output →
  0.06×0.50 + 0.05×0.05 + 0.001×2.50 = 0.03 + 0.0025 + 0.0025 = 0.035.

## P2b — Claude transcript reader (outline; detailed after P2a merges)

Dedup key `message.id + requestId` (fallback `message.id + sessionId + timestamp`, except ids starting `msg_`/`resp_`
which collapse on `message.id`); on duplicate keep non-sidechain over sidechain, else larger total tokens — which
requires storing each kept record's contribution so a replacement can subtract it from day×model and day×project
counters. Recursive scan of `projects/**/*.jsonl` (includes `subagents/`). `CLAUDE_CONFIG_DIR` comma-separated list
replacing defaults; defaults `%XDG_CONFIG_HOME%\claude` (else `~/.config/claude`) then `~/.claude`; a root counts only
if it has `projects/`; data from all roots combined and deduplicated. Model `<synthetic>` ignored. Acceptance: daily
Claude tokens equal `ccusage claude daily --json` on the same machine (tolerance to be set from a first comparison).

## Coding Owner decisions after Inspector A (2026-11-09) — these override anything above

Inspector A report (opencode, qwen3.7-plus, plan agent, spec-only) raised 13 points; decisions:

| # | Section | Decision |
|---|---|---|
| A1 | P1.2 cache | **Single slot.** The cache holds one report and the `since` it was read with. A call with a different `since` reads through and replaces the slot. |
| A2 | P1.3 runner | For **each PATH directory in order**, try `ccusage.exe` then `ccusage.cmd`; first hit wins. Empty PATH entries and entries that are not rooted absolute paths are skipped. |
| A3 | P1.3 parser | `unpricedModels` is read from **`root.totals.unpricedModels`** only. `totals` missing or not an object → treated as no unpriced models (not an error). |
| A4 | P1.3 parser | Dates parsed with `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)`; months with `yyyy-MM` and 1 ≤ month ≤ 12; `2026-02-30`, `2026-13` → row skipped. |
| A5 | P1.3 parser | Session row with `metadata` missing, `lastActivity` missing, or `lastActivity` without offset/`Z` → row skipped. |
| A6 | P1.3 runner | Retry with `--offline` **only** on timeout or non-zero exit of the first run. Exit 0 + parse failure → `InvalidResponse`, no retry. |
| A7 | P1.4 oracle | Oracle restated: successful read at 10:00:00 with MinInterval 15 min → call at 10:14:59 returns cached; call at **10:15:00 re-reads** (age equal to MinInterval is not "younger"). |
| A8 | P2a | 5m vs 1h cache write is decided per record by the existing transcript fields `usage.cache_creation.ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens` (fallback: `cache_creation_input_tokens` = all 5m), as today. Rates: 5m = input × 1.25, 1h = input × 2.0 for every model in the table (matches the official page). |
| A9/A13 | P2a | Prefix fallbacks have **one tier only** (the normal tier) and are `Estimated`. The >100k tier applies only to exact `claude-haiku-5-5` (and its date-snapshot form). |
| A10 | P2b | Acceptance (manual/tool check, not a unit test): for every **completed** local day present in both sources, Claude input, output, cache-creation and cache-read token sums equal ccusage exactly; today is excluded. |
| A11 | P2b | `CLAUDE_CONFIG_DIR` set but empty or whitespace-only → treated as **unset** (defaults are used). |
| A12 | P1.3 runner | Keep `ArgumentList` with the `.cmd` path (the npm shim forwards `%*`, the comma argument has no spaces so it is not quoted). Not testable without a real shim (TESTING.md forbids real ccusage in tests) → **Integrator verifies once** with the installed ccusage and records the result in the handoff. |
| OP3 | P1.1/P1.3 | Verified in ccusage source (`ccusage-core/src/pricing.rs` `load_with_overrides`): a failed online price fetch **silently falls back to embedded prices with exit 0**. So `AgentUsagePricing` becomes `OnlineRequested` / `Offline`; UI must word `OnlineRequested` as "online prices requested (ccusage uses its built-in prices if the fetch fails)". No stderr parsing. |
| OP4 | P2a | Haiku 5.5 tier needs the per-record prompt size, so cost is computed **per record at ingest** and accumulated as a cost counter per day×model and day×project (decimal), next to the token counters. P2b's dedup replacement subtracts the replaced record's stored tokens **and** cost. |
| OP1 | P1.1 | Keep `Other = Total − bucket sum` as specified. |

## Coding Owner decisions after the first P1 trial merge (2026-11-09) — override anything above

Trial merge of `ce716d5` (WO2 code) + `16aab15` (Test Author tests): 72 compile errors, all API-surface mismatches.

| # | Decision | Who changes |
|---|---|---|
| M1 | `AgentUsageUnavailableException(AgentUsageErrorKind kind, string message)` — kind first, as P1.2 says (an overload adding `Exception inner` last is fine). | WO2 |
| M2 | `CachedAgentUsageSource(IAgentUsageSource inner, IClock clock, TimeSpan? minInterval = null)`; null → 15 min. | WO2 |
| M3 | Runner API is WO2's: `internal interface ICcUsageRunner { Task<CcUsageRunResult> RunAsync(DateOnly? since, CancellationToken ct); }`, `internal sealed record CcUsageRunResult(string Output, AgentUsagePricing Pricing)`, `CcUsageRunner(Func<string?> resolve, Func<ProcessStartInfo, ICcUsageProcess> start, TimeSpan timeout, int outputLimit)`. | Test Author |
| M4 | Source ctor: public `CcUsageAgentUsageSource(IClock clock)`; internal `CcUsageAgentUsageSource(IClock clock, ICcUsageRunner runner)`. | Test Author |
| E1 | Model entry with `cost` absent (and no `missingPricing: true`) → invalid model → row skipped. | WO2 (currently treats as unknown price) |
| E2 | A `totalTokens` on a model entry is an ignored extra property. | — |
| E3 | `agents[]` entry with missing/blank/non-string `agent` → row skipped. `agents: []` → valid row with zero agents. Agent object without a `modelBreakdowns` array → row skipped. | WO2 + Test Author |
| E4 | A successful read (forced or not) clears `LastError`. A failed forced read does not evict the cache: a later non-forced call within MinInterval of the last success returns `LastGood`. | WO2 + Test Author |
| E5 | Non-string or missing `period` on daily/monthly rows → row skipped. | WO2 + Test Author |

## Coding Owner decisions after P1 code review (Reviewer1 + Reviewer2, 2026-11-09) — override anything above

Reviewed `083120e`. Both reviewers found R1 independently; R1–R4 block the merge.

| # | Decision | Who changes |
|---|---|---|
| R1 | `--offline` retry must actually run (A6). A first-run **timeout** or **non-zero exit** → second run with `--offline`; second success → `Pricing = Offline`; both fail → second run's error. No retry for `NotInstalled`, `OutputTooLarge`, `InvalidResponse`, process **start** failure (classified `Failed` but not retried), or caller cancellation. | WO2 + Test Author |
| R2 | stdout over the limit is always `OutputTooLarge`, never `Timeout`, including with an asynchronous (pipe-like) reader. If the deadline fired, the result is `Timeout` even when the process has exited in the same instant (partial output is never parsed). | WO2 + Test Author (fakes must include an async `TextReader` that yields, not only `StringReader`) |
| R3 | `metadata.lastActivity` must carry an explicit offset or `Z` **in the time part** (A5). `2026-11-09T10:00:00` → row skipped. | WO2 + Test Author |
| R4 | Session rows parse `modelBreakdowns` like daily/monthly agent objects: same model-entry rules, an invalid entry skips the row, `Usage.Models` is populated. | WO2 + Test Author |
| R5 | The 500-entry limit (`InvalidResponse`) applies to **every** `modelBreakdowns` array that is read: row level, each `agents[i]`, and session rows. | WO2 + Test Author |
| R6 | Cache state (`report`, `since`, `successAt`) is stored as one immutable snapshot in a single reference field; the pre-gate fast path reads that one reference. | WO2 |
| R7 | Runner seam exactly as M3: `CcUsageRunner(Func<string?> resolve, Func<ProcessStartInfo, ICcUsageProcess> start, TimeSpan timeout, int outputLimit)` (stderr cap is a 1 MiB constant), and `RunAsync` is a **public** (implicit) interface implementation. | WO2 |
| R8 | PATH entries that are drive-relative (`C:`, `C:x`) are skipped (A2). | WO2 |
| R9 | Accepted as is: the stdout limit counts UTF-16 chars (ccusage JSON is ASCII). | — |
