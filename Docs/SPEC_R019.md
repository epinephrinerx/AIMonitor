# SPEC R-019 — Anthropic Admin API key on the Claude card (API usage and cost)

Owner request (2026-11-11): "หน้า login ต้องรองรับการใช้งาน api ของทุกโปรแกรม" → meaning chosen: "ใส่ API key ดึงยอดจาก vendor";
API chosen: "Legacy Usage & Cost". Requirement: `Requirements.md` R-019 (✅). Base: `d927b3e`. Phase 2 (Writing Owner 2).

## Facts

- Docs (research 2026-11-11; Coding Owner did not call the API):
  - Usage: `GET https://api.anthropic.com/v1/organizations/usage_report/messages` — `starting_at` (RFC 3339, required),
    `ending_at`, `bucket_width=1d`, `group_by[]=model`, `limit` (1d: default 7, max 31), `page` = previous `next_page`.
    Response `{data:[{starting_at, ending_at, results:[{model, uncached_input_tokens, cache_read_input_tokens,
    cache_creation:{ephemeral_5m_input_tokens, ephemeral_1h_input_tokens}, output_tokens, …}]}], has_more, next_page}`;
    buckets oldest first, empty buckets have empty `results`.
    https://platform.claude.com/docs/en/api/beta/organization/usage_report/retrieve_messages
  - Cost: `GET https://api.anthropic.com/v1/organizations/cost_report` — `starting_at`, `ending_at`, `bucket_width=1d` only,
    `limit` max 31, `page`. `results[].amount` is a decimal **string in cents** (`"123.45"` = $1.23), `currency` "USD".
    https://platform.claude.com/docs/en/api/beta/organization/cost_report/retrieve
  - Headers: `x-api-key: <admin key>`, `anthropic-version` (docs examples use `2026-10-21`). Admin keys start `sk-ant-admin`.
    Workspace API keys do not work; individual (non-organization) accounts have no Admin API. Data appears within ~5 min.
    Key check: `GET /v1/organizations/me`. https://platform.claude.com/docs/en/manage-claude/admin-api ,
    https://platform.claude.com/docs/en/manage-claude/usage-cost-api
- Code: `ProviderMeta.Claude` has `NeedsKey=false`; `ProviderConnectionStore.SaveAsync` rejects a key for claude/agy and
  `GetAsync` never reads a claude key. OpenAI's Admin key path (`OpenAiLiveQuotaClient`, `KeyGuidance` in
  `ConnectDialogViewModel`, secret name `providers/{id}/key`, DPAPI store) is the pattern to follow.
  `ClaudeLiveQuotaClient` builds Claude's snapshot from the Claude Code OAuth file + `/api/oauth/usage` and transcripts.

## Changes

| Id | Where | Requirement |
|---|---|---|
| C1 | `ProviderMeta.Claude` | `NeedsKey=true`, `KeyLabel` "Admin API key (optional; API usage and cost)", `KeyPlaceholder` "sk-ant-admin…", no extra field. `Tagline` "Claude Code · full quota · optional API usage". `SetupHint` adds: the Admin key is optional, needs an Anthropic organization and an admin role, is created in Claude Console → Settings → Admin keys, and ordinary/workspace API keys cannot read usage; Claude Code sign-in stays the source of quota. |
| C2 | `ProviderConnectionStore` | Claude accepts, replaces and clears a key exactly like OpenAI (`providers/claude/key`); extra stays rejected for claude; agy unchanged. `GetAsync("claude")` returns the stored key. |
| C3 | `ConnectDialogViewModel.KeyGuidance` | For claude: a typed key not starting with `sk-ant-admin` shows a warning that only organization Admin keys can read usage and cost (same style as the OpenAI `sk-admin-` warning). Never echo the key. |
| C4 | new `Infrastructure/Providers/Claude/AnthropicAdminUsageClient.cs` | Given the key, an injected `HttpClient` and clock: request usage (`group_by[]=model`, `bucket_width=1d`) and cost for `starting_at` = first day of the current local month 00:00 local → UTC, `ending_at` = now; follow `next_page` while `has_more` (cap 10 pages); headers `x-api-key`, `anthropic-version: 2026-10-21`, `User-Agent: AIMonitor/<version>`. Map to: month tokens (sum of uncached input + cache read + both cache creation + output), today tokens (bucket whose local day is today), month cost USD and today cost USD (sum of `amount` ÷ 100, parsed with InvariantCulture). 15 s timeout like the OAuth client. |
| C5 | same | Errors: 401/403 → "Admin key rejected — use an organization Admin key (sk-ant-admin…)"; 404 → "Admin API not available for this account (individual accounts have no Admin API)"; 429 → "Rate limited — try again later"; other/network/parse → short message. Never include the key, headers or response body in messages or logs. |
| C6 | `ClaudeLiveQuotaClient` (or the composition that builds Claude's snapshot) | When a saved Claude Admin key exists, call C4 in the same refresh. On success add stats after the existing ones: "API cost today" (`$X.XX`), "API cost this month", "API tokens this month" (compact K/M like other token stats); `Stat.Detail` "Anthropic Admin API". On failure add one stat "API usage" with value "Unavailable" and `Detail` = the C5 message (no other stats from C4); Claude quota meters and OAuth behaviour never change. With no OAuth login but a working Admin key the tab is `configured: true` and shows the three stats (no quota meters, no setup card). |
| C7 | Agy card | `ProviderMeta.Agy.SetupHint` adds one sentence: Gemini API keys cannot read usage or quota (no usage API), so AGY uses your Antigravity sign-in. |
| C8 | composition (`App.xaml.cs`) | Pass the saved Claude key from the secret store to the Claude client the same way the OpenAI Admin key is passed; a saved-key change triggers the existing Changed → refresh path. |

## Tests (Test Author, from this spec)

Fake `HttpMessageHandler` only: request URL/query (`starting_at`, `group_by[]=model`, `bucket_width=1d`, `page` on page 2),
headers present, key never in exception text; parsing of a two-page usage response and a cost response with `"123.45"` →
$1.2345 summed; today/month split across the local midnight with an explicit zone; 401/404/429/timeout messages;
store accept/replace/clear for claude and still reject extra; `KeyGuidance` warning; Claude snapshot with OAuth + admin
(quota meters unchanged + three stats), admin only (configured, no setup card), admin failure (quota unchanged).
No real network, key or clock.

## Verification (Writing Owner)

`dotnet build AIMonitor.sln -c Release` · `dotnet test AIMonitor.sln -c Release --no-build` ·
`dotnet format AIMonitor.sln --verify-no-changes`. No real Admin key and no network call.

## Not included

No Gemini service account or Cloud Monitoring (R-012). No OpenCode API (no documented usage API; R-020 covers the card).
No env-var key source. No change to Claude quota meters, tray or widget. Analytics (beta) API not used.
