# AI Usage Monitor 2.0.2

- Rename the AGY tab to **Gemini / AGY** across the dashboard, Connections, sign-in dialog and usage log.
- Group the Gemini / AGY gauges into a **Gemini** row (including model-specific pools such as Gemini 4 Argon) and a **Claude / GPT** row. Cards line up across rows, and their labels no longer overlap the gauge ring. Widget and tray keep the full pool names. Claude and OpenAI / Codex layouts are unchanged.
- Strengthen the Codex transport regression test: stdin and stdout encodings must not emit a UTF-8 BOM.

Windows x64 downloads: use **AIUsageMonitor Setup 2.0.2.exe** to install, or **AIUsageMonitor 2.0.2.exe** for the self-contained portable app. Settings carry over from 2.0.1 unchanged; the provider id stays `agy`. Keep the previous installer for rollback.

Validation: build with 0 warnings/errors, all 1,489 synthetic .NET tests and format verification pass; the owner accepted R-006, R-007, R-008, R-012 and R-013 on the Debug build against real accounts. Real-desktop DPI/tray and clean-machine upgrade smoke matrices were not repeated for this release. Executables are unsigned.
