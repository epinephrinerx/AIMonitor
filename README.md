# AI Usage Monitor 2.0

A native Windows desktop monitor for AI usage quota: Claude, OpenAI / Codex and Gemini in one window.
Version 2.0 is a rewrite of the Python 1.3.3 application in C# / WPF (.NET 10) that keeps the same screens and
behaviour.

Two shapes in one app: a **dashboard** with a tab per service, and a compact **desk widget** (at most 300 × 300) that can
stay on top and go translucent. It also lives in the system tray, where the icon shows your five-hour window as a
filled tile with the percentage written across it.

## Installing

Run `AIUsageMonitor Setup 2.0.0.exe` (installs per user, no administrator rights needed), or run `AIUsageMonitor 2.0.0.exe` directly without installing. The first launch
imports the settings of a Python 1.3.x install if there is one; nothing in the old registry keys is deleted.

## What each service can report

| Service | Source | Shows |
|---|---|---|
| Claude | Your existing Claude Code login (read-only) and local transcripts | Session and weekly windows, usage per day by model and project, equivalent API value |
| OpenAI / Codex | Your Codex ChatGPT login (read-only), or an optional Admin API key | Codex quota windows, optional API spend against a monthly budget |
| Gemini | Your Gemini CLI login or a service account JSON | Request counts from Cloud Monitoring (Google publishes no token-level usage) |

Nothing is invented: when a service reports no percentage, the app shows a dash rather than a guess. The app never
writes to another tool's credentials and never sends your usage anywhere.

## Using it

- **Dashboard**: metric, range and refresh interval selectors; severity words on every gauge ("✓ Normal", "⚠ High");
  reset countdowns; a stacked usage chart per day with By model and By project breakdowns.
- **Widget**: Ctrl+W or the Widget button. Right-click for Expand, Refresh, Show (which service), Always on top and
  Opacity. Double-click to expand again.
- **Tray**: double-click to reopen; right-click lists every quota window with a live countdown.
- **Settings**: theme, default window size, start with Windows, minimise to tray, per-service monitoring.
- **Keyboard**: F5 refresh, Ctrl+W widget, Ctrl+L usage log, Ctrl+P print report, Ctrl+Q exit, F1 this readme.

## Credential handling

Keys you save in the app are sealed with Windows DPAPI for your user account. Existing logins of other tools are only
ever read.

## Building

```
dotnet build AIMonitor.sln
dotnet test AIMonitor.sln --no-build
.\build_installer.ps1
```

`build_installer.ps1` builds, tests, publishes a self-contained win-x64 build and compiles the Inno Setup installer
(Inno Setup 6 required).

## Licence

GPL-3.0-or-later. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
