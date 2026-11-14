# AI Usage Monitor 2.0.4

- Gauge captions on the dashboard cards wrap to at most two lines and end with "…" instead of running past the card.
- The **Gemini / AGY** tab shows a token stats row and a daily chart from ccusage's `antigravity` agent. Without that data the tab looks as before.
- The **Claude** card in Connections accepts an optional Anthropic organization **Admin API key**. With it, the Claude tab shows API usage and cost from Anthropic's Usage & Cost API. The key is stored with Windows DPAPI and is never shown again. A malformed or rejected key shows the reason, and cards for providers without a usage API say why a key cannot be used.
- A new **OpenCode** card in Connections shows whether `opencode` is installed and which providers are signed in, by name and sign-in type only. Secrets and environment variable names are never shown. When nothing is signed in, the card shows `opencode auth login` guidance. The check runs at start-up and on Re-detect.
- A new **Other agents** section in Connections lists the agents ccusage finds that have no tab of their own, with sign-in guidance for GitHub Copilot CLI and ZCode and general guidance for the rest.

Windows x64 downloads: use **AIUsageMonitor Setup 2.0.4.exe** to install, or **AIUsageMonitor 2.0.4.exe** for the self-contained portable app. Settings carry over from 2.0.3. Keep the previous installer for rollback.

Validation: build with 0 warnings/errors, all 3,179 synthetic .NET tests and format verification pass, and the NuGet vulnerability audit reports no vulnerable packages. The owner accepted R-018, R-019, R-020 and R-021 on a Debug build. Real-desktop DPI/tray and clean-machine upgrade smoke matrices were not repeated. Executables are unsigned.
