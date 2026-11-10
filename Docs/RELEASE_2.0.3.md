# AI Usage Monitor 2.0.3

- Read usage and cost for every local agent from the user-installed **ccusage** CLI (`ccusage daily --json --by-agent`). Add an **All agents** tab with per-agent, per-model daily and monthly figures and sessions. Models without a known price show as unpriced, not $0. The existing tabs show their agent's ccusage figures below the gauges. Without ccusage the app works as before and shows install guidance. ccusage is not bundled.
- Align the Claude transcript reader and price table with ccusage: deduplicate on message and request id, scan every project folder including subagents, and honour `CLAUDE_CONFIG_DIR`.
- Add an **OpenCode** tab: a gauge of this month's OpenCode cost against a monthly budget set per agent in Settings, a per-model stacked daily token chart with a daily cost line on a right-hand $ axis, and a By model breakdown.
- Add an **Overview** tab: one cost-versus-budget gauge for each agent ccusage finds this month, sorted by cost, at most three per row.
- Tabs are ordered Claude · OpenAI / Codex · Gemini / AGY · OpenCode · Overview · All agents. Tray and widget are unchanged.

Windows x64 downloads: use **AIUsageMonitor Setup 2.0.3.exe** to install, or **AIUsageMonitor 2.0.3.exe** for the self-contained portable app. Settings carry over from 2.0.2; agent budgets default to unset. Keep the previous installer for rollback.

Validation: build with 0 warnings/errors, all 2,330 synthetic .NET tests and format verification pass, and the NuGet vulnerability audit reports no vulnerable packages. The owner accepted R-014, R-015, R-016 and R-017. Real-desktop DPI/tray and clean-machine upgrade smoke matrices were not repeated. Executables are unsigned.
