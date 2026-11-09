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
| R10 | `AgentUsageReport` itself normalizes in its constructor (P1.1 states order/dedup as a property of the record): daily by date asc, monthly by (year, month) asc, sessions by `LastActivity` desc, `UnpricedModels` distinct + ordinal sort. The parser may keep sorting too. Found by Test Author (4 red `AgentUsageReportTests`). | WO2 |

## P2b detailed (Coding Owner, 2026-11-09) — replaces the P2b outline above; Writing Owner 3

Base: branch `agent/opencode-r014-p2a` at `d76762a` (P2a pricing, reviewed). Reference behaviour: ccusage
`rust/adapters/claude/src/daily.rs` + `lib.rs` (`usage_dedupe_hash`, `push_deduped_daily_entry`,
`should_replace_deduped_daily_entry`). Scope: `ClaudeTranscriptPathResolver.cs`, `ClaudeTranscriptStore.cs`,
`UsageCounters.cs` (Domain), `ClaudeLiveQuotaClient.cs` and `App.xaml.cs` wiring only.

### P2b.1 Roots (`ClaudeTranscriptPathResolver`)
New `public static IReadOnlyList<string> ResolveAll(string? claudeConfigDir, string? xdgConfigHome, string userProfileDirectory)`
returning `projects` directories, pure (no environment or disk access):
1. `claudeConfigDir` non-blank (A11: blank/whitespace = unset) → split on `,`, trim each, drop empty entries,
   each entry → `<entry>\projects`. Defaults are **not** added.
2. Otherwise → `<xdg>\claude\projects` where `<xdg>` = `xdgConfigHome` if non-blank else
   `<profile>\.config`, then `<profile>\.claude\projects`.
3. Duplicates removed (`Path.GetFullPath`, `StringComparer.OrdinalIgnoreCase`), first occurrence kept, order kept.
The existing `Resolve(...)` stays unchanged (credential path logic is out of scope).

### P2b.2 Store roots and scan (`ClaudeTranscriptStore`)
- New ctor overload taking `IReadOnlyList<string> roots` (non-null, ≥ 1 entry, no blank entry); the existing
  `string root` ctors delegate with a one-item list. Internal ctor keeps the `Func<string, Stream> openFile` seam.
- On each refresh, a root is used only if its directory exists. None exists → `LastDirectoryError =
  "No Claude transcripts directory found."`, `FilesScanned = 0`, return 0. A root that exists but cannot be
  enumerated → `LastDirectoryError = "Could not read a Claude transcripts directory."`, other roots still scanned.
- Files: every `*.jsonl` under each root at any depth (including `subagents\`), via `EnumerationOptions
  { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = ReparsePoint }`; all files of all
  roots sorted `StringComparer.Ordinal` by full path; `FilesScanned` = total count.
- Fallback project label comes from the **first directory segment below the root** (the encoded project folder),
  not the immediate parent; a file directly in a root uses `""` → `(unknown)`.

### P2b.3 Record filter (before any key is claimed; not counted as malformed)
Skip a record if `message.model` is `<synthetic>`, or if any of `sessionId`, `requestId`, `message.id`,
`message.model` is **present as an empty string**. A missing model stays as today (priced as unknown).

### P2b.4 Dedup keys
Let `id` = non-empty `message.id`, `req` = non-empty `requestId`, `sid` = `sessionId` (else the file name without
extension), `ts` = the raw `timestamp` string, `side` = `isSidechain == true`.
- No `id` → no dedup (always counted; offsets still prevent re-reading).
- Exact key: `req` present → `id + "\n" + req`; else if `id` starts with `msg_` or `resp_` → `id + "\n\n" + sid`;
  else → `id + "\n\n" + sid + "\n" + ts`.
- Replay key `id + "\n" + sid`: a new record also matches a kept record with the same replay key when **either**
  of the two is a sidechain record (ccusage `/btw` replay rule).
- Match = exact key hit, else replay-key hit. A kept record is registered under its exact key and its replay key.

### P2b.5 Survivor and replacement
On a match, the new record replaces the kept one when (in order): kept is sidechain and new is not → replace;
new is sidechain and kept is not → keep; larger total tokens (input + output + cache creation + cache read)
wins; equal → larger cost wins; equal → keep the existing one.
Replacement subtracts the kept record's stored contribution (its day, model label, project label and
`UsageCounters`, tokens **and** cost — OP4) from day×model, day×project and `AllTotals`, then folds the new one.
A day bucket that was pruned is skipped for the subtraction; a label whose counters reach all-zero with
`Messages == 0` is removed. `Messages` stays 1 per surviving record.
Domain: `UsageCounters.Subtract(UsageCounters other)` (checked; throws if any field would go negative).
Stored contributions live in the bounded seen-set (400,000 entries, oldest evicted together with its keys).

### P2b.6 Wiring
`ClaudeLiveQuotaClient` builds the store from `ResolveAll(claudeConfigDir, xdgConfigHome, userProfile)`;
`App.xaml.cs` passes `Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")`. Credentials path unchanged.

### P2b.7 Tests (Test Author) and acceptance
Synthetic fixtures only. Cover each rule above, including: exact vs replay match, each survivor branch, cost
subtraction with a Haiku 5.5 > 100k record replaced by a smaller one, eviction, recursive `subagents\` scan,
first-segment project label, root list rules 1–3, missing/unreadable roots. Acceptance A10 (Integrator, manual):
every completed local day present in both sources has equal Claude input/output/cache-creation/cache-read sums
versus `ccusage claude daily --json`.

## Coding Owner decisions after P2b Inspector A (2026-11-09) — override the P2b detailed section

| # | Decision |
|---|---|
| B1 | Session aliases (ccusage `push_deduped_session_alias`): when a match joins two records with **different** `sid`, the survivor is registered under the replay key of **both** sids, so a later replay from either session finds it. |
| B2 | `sid` fallback when `sessionId` is missing: file at `<root>\<project>\<file>.jsonl` → file name without extension; deeper file → the directory segment directly below `<project>` (ccusage `extract_session_parts`). |
| B3 | `<synthetic>` records are **counted** (ccusage counts their tokens): model label `(synthetic)`, cost 0, not added to unpriced or estimated tokens. Replaces the P2b.3 skip. |
| B4 | A pruned day bucket makes only the day×model / day×project subtraction a no-op; the `AllTotals` subtraction always happens. A replacement may recreate a pruned bucket; the next prune removes it. |
| B5 | Add the `version` filter: a record whose `version` is present and is not a semver prefix (`<digits>.<digits>.<digit>…`, ccusage `is_semver_prefix`) is skipped, not counted as malformed. |
| B6 | Accepted divergence: no `-fast` model suffix for `usage.speed == "fast"` (A10 compares daily token sums only). |
| B7 | Eviction limitation is accepted and tested: the seen-set capacity becomes an internal ctor seam (default 400,000); with capacity 1, record A, then B (other key), then a duplicate of A → A is counted twice. |
| B8 | Exact-key match wins over replay-key match even when they point to different kept records (ccusage order). Two non-sidechain records with the same replay key and different exact keys both survive. |
| B9 | A `CLAUDE_CONFIG_DIR` entry whose last path segment is `projects` (OrdinalIgnoreCase) is used as the projects directory itself (ccusage `normalize_claude_config_path`, done syntactically). |
| B10 | Oracles (rates from P2a; cost = Σ tokens × rate / 1,000,000). (a) `claude-sonnet-5` A: input 100, output 50, cache read 20 → 100×2 + 50×10 + 20×0.20 = 704 → **$0.000704**; B same key, input 200 → 400 + 500 + 4 = 904 → **$0.000904** (corrected; WO3 caught the earlier 1,104); after B replaces A, day×model = B's counters, `Messages` = 1. (b) `claude-haiku-5-5` A: input 60,000, cache read 50,000, output 1,000 → prompt 110,000 > 100,000 → high tier 60,000×0.50 + 1,000×2.50 + 50,000×0.05 = 35,000 → **$0.035**; A has `isSidechain: true`; B has the same key, is not a sidechain (so it wins despite fewer tokens), input 1,000, output 500, cache read 100 → low tier 100 + 250 + 1 = 351 → **$0.000351**; after replacement `AllTotals.CostUsd` = **$0.000351**. (c) Survivor order: kept sidechain A (total 1,000) vs new non-sidechain B (total 10) → B wins. |

## Coding Owner decision after P1 test round 2 (2026-11-09)

| # | Decision | Who changes |
|---|---|---|
| R11 | R2 holds even when the child **never exits**: as soon as stdout passes the limit the runner kills the process tree and reports `OutputTooLarge` (no waiting for the deadline, no `--offline` retry). Tests must keep a hanging-child case for this, next to the exited/drained case. A test is never reshaped to fit the code (§5.6). | WO2 + Test Author |


## Coding Owner decision after P2b code (2026-11-09)

| # | Decision |
|---|---|
| B11 | Fallback project label: the first segment below the root is still decoded with the existing `ProjectFromDirectoryName` (`C--work-project` → `project`); only the chosen segment changes. |
| B12 | Memory (Reviewer1 MAJOR-1: 357 MB at 400,000 records). Store exact and replay keys as 64-bit hashes (FNV-1a 64 over the key's UTF-16 code units; a hash hit counts as a match), share model/project label strings (one instance per distinct label), keep aliases in a small array, no per-record `HashSet`. Target: 400,000 records retained ≤ 100 MB after a full GC (WO3 measures once with a scratch harness and reports the number). Update the class summary's memory statement. |
| B13 | `Unfold` never throws: subtract day×model and day×project with each field floored at zero (covers a bucket recreated after pruning, Reviewer1 MINOR-1), then subtract `AllTotals` last (exact). A record is not counted as malformed because of its replacement. |
| B14 | `CLAUDE_CONFIG_DIR` that is non-blank but yields zero entries (e.g. `","`) is treated as unset (like A11). |
| B15 | A `CLAUDE_CONFIG_DIR` entry that is not a fully qualified path, or is drive-relative (`C:x`), is skipped (same rule as R8); if no entry remains → treated as unset. `ResolveAll` stays free of environment and disk access (no `GetFullPath` on relative input). |
| B16 | Advisor iterations (ccusage `advisor_usages_from_line`; found in 2 real local transcripts): for each `message.usage.iterations[k]` with `type == "advisor_message"` and a non-empty `model`, add one extra record with the same timestamp, `sessionId`, `requestId`, `isSidechain`, project and day; `message.id` = `<id>:advisor:<n>` where `n` counts advisor entries only (0, 1, …; omitted dedup if the parent has no id); model and token fields from the iteration (`input_tokens`, `output_tokens`, `cache_creation_input_tokens` or `cache_creation.{ephemeral_5m,1h}`, `cache_read_input_tokens`); priced like any record; deduplicated by the same rules. An invalid iteration is skipped alone. |
| B17 | Follow-up, not in R-014: the credentials path still uses the raw `CLAUDE_CONFIG_DIR` (Reviewer1 MINOR-3). |

## P3 — "All agents" tab (Coding Owner, 2026-11-09; Writing Owner 2)

Base: current integration branch (P1 + P2a merged, `760bb1c`). Uses the P1 port only; no new ccusage calls besides
`CachedAgentUsageSource`. Layering: mapping logic is pure and unit-testable (no WPF types).

### P3.1 Presenter (`src/AIMonitor.Presentation.Wpf/ViewModels/AgentUsagePresenter.cs`, static, pure)
- `string AgentDisplayName(string agent)`: `claude` → "Claude Code", `codex` → "Codex", `antigravity` → "Antigravity",
  `opencode` → "OpenCode", `copilot` → "Copilot", `zcode` → "ZCode", anything else → the id unchanged.
- `string CostText(AgentUsage usage)` (and the same rule for a period total): no models → "$0.00";
  every model unpriced → "Unknown price"; some unpriced → `"$1.23 + unpriced"`; else `"$1.23"`.
  Money = `ChartFormat.Money`. A model with `CostUsd == null` never contributes `$0` (R-014 item 3).
- `string TokensText(long tokens)` = `ChartFormat.Compact`.
- `AgentUsagePeriod BuildDay(DailyAgentUsage day)` / `BuildMonth(MonthlyAgentUsage month)`: record
  `AgentUsagePeriod(string Title, long Tokens, string TokensText, string CostText, IReadOnlyList<AgentUsageLine> Agents)`;
  `AgentUsageLine(string Agent, string DisplayName, long Tokens, string TokensText, string CostText)`.
  Day title `yyyy-MM-dd` (invariant), month title `MMMM yyyy` (invariant, e.g. "November 2026").
  Agents sorted by `PricedCostUsd` desc, then tokens desc, then id ordinal. Period tokens = Σ agent `Tokens.Total`.
- `string PricingText(AgentUsagePricing p)`: `OnlineRequested` → "Prices: online (ccusage uses its built-in prices if the
  fetch fails)" (OP3); `Offline` → "Prices: ccusage built-in (offline)".
- `string ErrorText(AgentUsageErrorKind kind, string message)`: `NotInstalled` → the exception message (it already says
  how to install); others → message.
- `AgentUsageSummary? SummaryFor(AgentUsageReport? report, string agent, DateOnly today)`: record
  `AgentUsageSummary(AgentUsageLine? Today, AgentUsageLine? ThisMonth)`; `null` when report is null or the agent appears in
  neither today's daily row nor this month's monthly row. (Used by P4.)

### P3.2 View model (`AllAgentsTabViewModel`)
`TabTitle` "All agents", `TabId` "all-agents". `Update(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError, DateOnly today, int rangeDays)`:
- `Today` / `ThisMonth`: `AgentUsagePeriod?` for today's daily row and the current month row (null if absent).
- `Days`: daily rows for the last `rangeDays` local days including today, newest first; `Months`: all monthly rows newest first.
- `Sessions`: 20 most recent by `LastActivity`: `AgentUsageSessionLine(DisplayName, ShortId (first 8 chars), LastActivityText (local "yyyy-MM-dd HH:mm"), TokensText, CostText)`.
- `StatusText`: `"Updated HH:mm · " + PricingText` from `RetrievedAt` (local); plus `" · N rows skipped"` when `SkippedRows > 0`.
- `ErrorText`/`HasError` from `lastError`; when `lastGood` is also present add `"Showing data from HH:mm."`.
- `ShowInstallHint` = `lastError.Kind == NotInstalled && lastGood == null`; nothing else shown then.
- No report and no error yet → `StatusText` "Loading ccusage…".

### P3.3 Main window
- `MainWindowViewModel` gets an optional `CachedAgentUsageSource? agentUsage` ctor parameter (App passes `_agentUsageSource`).
- `Tabs`: `IReadOnlyList<object>` = the three provider tabs then `AllAgentsTab`; the tab ListBox binds to `Tabs` and
  `SelectedTabItem`. Selecting a provider tab sets `SelectedTab` exactly as today (tray/widget untouched); selecting
  All agents sets `IsAllAgentsSelected = true`, keeps `SelectedTab` unchanged, and saves `ActiveProvider = "all-agents"`.
  `AppSettings` normalisation must accept `"all-agents"`; startup with it selects the All agents tab.
- After every provider refresh (timer, F5, view change) call `agentUsage.ReadAsync(since, force, ct)` **after** the
  provider tabs are updated, without delaying them: `since` = first day of the month two months before today (local);
  `force` = true only when the user pressed Refresh/F5 while All agents is selected. Exceptions other than cancellation
  are already captured in `LastError`; never surface them as "Refresh failed". Then `AllAgentsTab.Update(LastGood, LastError, today, ChartRangeDays)`.
- XAML: provider content `ScrollViewer` collapsed when `IsAllAgentsSelected`; new `AllAgentsView.xaml` (UserControl)
  visible instead: status line, error/install card, two summary cards (Today, This month: tokens, cost, per-agent lines),
  "Daily" list, "Monthly" list, "Recent sessions" list. Theme brushes only (`CardBackgroundBrush`, `TextPrimaryBrush`, …).

### P3.4 Tests (Test Author) — presenter rules with synthetic `AgentUsageReport`s (oracles by hand), view-model
`Update` cases (no data, NotInstalled, error with last good, skipped rows, range filtering, session cut at 20, ordering),
`MainWindowViewModel` tab selection/`ActiveProvider` save, force only on manual refresh, provider refresh not blocked by a
slow fake source. No real ccusage.

## P4 — ccusage rows in the existing tabs (Coding Owner, 2026-11-09; Writing Owner 3)

- `ProviderTabViewModel.SetAgentUsage(AgentUsageSummary? summary)` stores the summary and rebuilds the supplementary rows;
  `UpdateFromSnapshot` keeps using the stored summary.
- Rows (group `"ccusage"`, appended after the existing rows): `"Today (ccusage)"` → `"<tokens> · <cost>"`,
  `"This month (ccusage)"` → same; a missing line is omitted. Gauges, charts, stat tiles unchanged (R-004).
- Section visibility: `ShowSupplementary = <existing rule> || summary has at least one line`.
- Mapping: Claude tab ← `claude`, OpenAI / Codex tab ← `codex`, Gemini / AGY tab ← `antigravity`.
- P4 adds one call site in `MainWindowViewModel` right after `AllAgentsTab.Update(...)`:
  `ClaudeTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(report, "claude", today))` and the same for the two others.
- Tests: rows present/absent, visibility rule, summary survives `UpdateFromSnapshot`, no change to meters/stat tiles.

## Coding Owner decisions after P3/P4 Inspector A (2026-11-09) — override the P3/P4 sections

| # | Decision |
|---|---|
| C1 | `ProviderTabs` stays provider-only (widget, tray). New `Tabs : IReadOnlyList<object>` (three provider tabs, then `AllAgentsTab`) only for the tab ListBox; ListBox `SelectedItem` ↔ new `SelectedTabItem : object`; `SelectedTab : ProviderTabViewModel` stays for all content bindings and changes only when a provider tab is picked; `IsAllAgentsSelected : bool`. Both tab types expose `TabTitle`. |
| C2 | `AppSettings` normalisation accepts `"all-agents"`. Startup with it: `IsAllAgentsSelected = true`, `SelectedTab = ClaudeTab`. |
| C3 | New `AgentUsageRefresher` (Presentation.ViewModels): ctor `(CachedAgentUsageSource source, Func<DateTimeOffset> utcNow, TimeZoneInfo zone)`; `Task RefreshAsync(bool force)`; event `Updated(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError)` raised on the caller's context after each read. At most one read in flight: a call while one runs does nothing unless `force`, which then runs once more after it. It catches every exception (cancellation included) — nothing escapes. `since` = first day of the month two months before the local today. `MainWindowViewModel.RefreshAsync` **starts** it (not awaited) after the provider tabs are updated, outside the provider try/catch, so `IsRefreshing`, `_pendingRefresh` and "Refresh failed" never depend on ccusage. `internal Task? AgentUsageTask` exposes the last started task for tests. |
| C4 | `RefreshAsync(string? onlyProviderId = null, bool manual = false)`; `RefreshCommand` passes `manual: true`; timer/view-change pass false; `force = manual && IsAllAgentsSelected`. A single-provider re-detect does not start a ccusage read. |
| C5 | `MainWindowViewModel` handles `Updated` in `private void OnAgentUsageUpdated(...)`: `AllAgentsTab.Update(lastGood, lastError, today, ChartRangeDays)` then the P4 line(s). P4 edits only that method body in this file. |
| C6 | `PricingText`: `"Prices: online"` / `"Prices: offline (built-in)"`; `PricingToolTip` (OnlineRequested only) = `"ccusage requested online prices; it uses its built-in prices if the fetch fails."` (OP3). |
| C7 | Brushes: `CardBackgroundBrush`, `BorderBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`, `SeverityWarningBrush` (error card border), `AccentBrush`; cards `CornerRadius 10`, `Padding 16,12`, like the stat-tile card. |
| C8 | AGY: when a ccusage summary has a line, `Supplementary` is created with only the ccusage rows (no reset or activity parts) and `ShowSupplementary = true`; otherwise AGY stays as today. Claude/OpenAI append the rows to their existing ones. Rows go to `Supplementary.Rows` only, never to `snapshot.Stats` or stat tiles. |
| C9 | Tab → agent map: `{ "claude": "claude", "openai": "codex", "agy": "antigravity" }`; other ids → no rows. |
| C10 | `SetAgentUsage(null)` removes the rows and restores the existing visibility rule; rows survive `UpdateFromSnapshot`. The new rows are the R-014 item 4 addition, accepted under R-004 (no existing element changes). |
| C11 | Oracles. `TokensText`: 9,999 → `"9,999"`; 10,000 → `"10K"`; 12,900 → `"12.9K"`. Money via `ChartFormat.Money((double)cost)`: 0.001 → `"<$0.01"`. `CostText` by model costs: [] → `"$0.00"`; [null] → `"Unknown price"`; [0.00] + [null] → `"$0.00 + unpriced"`; [1.23] → `"$1.23"`; [1.00, 0.23, null] → `"$1.23 + unpriced"`. `BuildDay`: claude (cost 1.23, total 1,000), codex (0.50, 2,000), opencode (only unpriced, 500) → order claude, codex, opencode; tokens 3,500 → `"3,500"`; period cost `"$1.73 + unpriced"`. `since`: today 2026-11-09 → 2026-09-01; 2026-12-01 → 2026-10-01; 2026-01-15 → 2025-11-01. `Days` with today 2026-11-09 and range 7 → rows dated 2026-11-03…2026-11-09 that exist, newest first (missing days omitted). Error with last good: `RetrievedAt` 2026-11-09T07:03:22Z, zone UTC+7 → suffix `"Showing data from 14:03."`. Sessions: 25 → first 20 (report order). `SummaryFor`: agent in this month's row but not today's → `Today` null, `ThisMonth` set; agent only in an earlier month → null. |
| C12 | A forced read waits for an in-flight read (P1 single gate); it does not cancel it. |

## Coding Owner decisions after P3 code review (2026-11-09)

| # | Decision |
|---|---|
| C13 | `AgentUsageRefresher`: capture `SynchronizationContext.Current` at `RefreshAsync` entry; continue the read with `ConfigureAwait(true)` (or post to the captured context) so `Updated` runs there (Reviewer1 MAJOR 1, Reviewer2 M-1). Handler exceptions are caught too, and the in-flight flag is reset in `finally`, so the refresher always recovers (MAJOR 3). |
| C14 | Selecting a provider tab after All agents always saves `ActiveProvider = provider.ProviderId`, even when it is the same `SelectedTab` instance (MAJOR 2). Setting `SelectedTab` programmatically also sets `SelectedTabItem` to it and `IsAllAgentsSelected = false` (MINOR 6). |
| C15 | Start the ccusage read after the provider try/catch/finally block (still only for full refreshes), so a failed provider refresh still starts it (MINOR 4). |
| C16 | `AllAgentsTabViewModel` exposes `HasDays`, `HasMonths`, `HasSessions`; section headers bind to them; `ShowInstallHint` true → only the install card is visible (MINOR 5). |
| C17 | Summary cards: tokens on one line (FontSize 20), cost on the next line (FontSize 14, wraps) so nothing clips at the 300 px minimum width (MINOR 7). |
| C18 | `AllAgentsTabViewModel.Update(lastGood, lastError, today, rangeDays, TimeZoneInfo zone)` and the presenter's session/time texts take the zone; `MainWindowViewModel` passes `TimeZoneInfo.Local`. No `DateTime.UtcNow` / `ToLocalTime()` inside these types (testability of the C11 oracle). Remove unused usings. |

## Coding Owner decisions after the P2b trial merge (2026-11-09)

Trial merge (integration HEAD + `7c4589a` + `c1f8c76`): build clean, 1,890 pass, 3 fail — all three in
`ClaudeTranscriptPathResolverResolveAllTests`, all test-side.

| # | Decision | Who |
|---|---|---|
| B18 | `ResolveAll` returns each kept entry in its **original spelling** (case and trailing separator as written, with `\projects` appended unless B9 applies); duplicates are detected with `GetFullPath` + `OrdinalIgnoreCase`. Tests compare paths `OrdinalIgnoreCase` after `GetFullPath`. `ResolveAll_XdgAndProfileDefaultsThatResolveToTheSameDirectory_AreListedOnce` has a false premise (`<profile>\.config\claude\projects` ≠ `<profile>\.claude\projects`) — replace it with a case where `xdgConfigHome` really equals another candidate, or drop it. | Test Author |
| B19 | A record that **replaces** a kept one is not "added": `TryIngest` returns `false` for it and `RefreshAsync`'s count excludes it (its counters still change). | Test Author (assert), WO3 (already so) |
| B20 | `version: "2.1"` (two components) is not a semver prefix → record skipped (ccusage needs `<d>.<d>.<d>`). | Test Author |
| B21 | `ClaudeLiveQuotaClient` ctors take an optional trailing `string? xdgConfigHome = null` (already in `7c4589a`); tests may feed it. | Test Author |

## Coding Owner decisions after P4 code review (2026-11-09)

| # | Decision |
|---|---|
| C19 | `SupplementaryDetailsViewModel.HasResetInfo` = any of `ResetTitle`, `ResetBadge`, `ResetSummary`, `ResetDetails` non-empty; the reset row `Border` in `SupplementaryDetailsView.xaml` collapses when false (same trigger style as the activity border), and `ProviderTabViewModel` forwards `HasResetInfo` with the other dependent properties. A ccusage-only section therefore shows the header and the ccusage rows only (Reviewer1 MAJOR 1, Reviewer2 MAJOR-1). WO3 may edit those two files for this. |
| C20 | Claude/OpenAI: when the existing rule would hide the section (`_baseShowSupplementary == false`, including "no snapshot yet"), a summary with a line creates a **ccusage-only** section exactly like AGY (no placeholder Plan/Credits rows, no reset part, no default "Rate Limit Resets" title). When the existing rule shows the section, the rows are appended as now. |
| C21 | Skip rebuilding `Supplementary` when the computed ccusage rows equal the current ones (no churn on every refresh). |
| C22 | P4 tests are the Test Author's and gate the merge (Reviewer2 MAJOR-2): ordered after the P3 tests. |
