# AI Usage Monitor

A native Windows desktop monitor for AI usage quota — Claude, OpenAI and Gemini
in one window. Written in Python with PySide6 (Qt), shipped as a standalone
`.exe` and a per-user installer.

Two shapes in one window: a **full dashboard** with a tab per service, and a
**desk widget** under 300×300 that stays on top and goes translucent. It also
lives in the system tray, showing your session level as a drawn icon.

---

## Contents

- [Installing](#installing)
- [Services and what each can report](#services-and-what-each-can-report)
- [How usage is counted](#how-usage-is-counted) — read this if the numbers surprise you
- [Window modes](#window-modes)
- [System tray](#system-tray)
- [Settings](#settings)
- [Resource use](#resource-use)
- [Known limits](#known-limits)
- [Credential handling](#credential-handling)
- [Building](#building)
- [Architecture](#architecture)
- [Working with Claude Code](#working-with-claude-code)

---

## Installing

Run **`AIUsageMonitor-Setup-1.1.0.exe`**.

It installs **per user** into `%LocalAppData%\Programs\AIUsageMonitor`, so there
is no UAC prompt and no admin rights are needed — the app only reads the current
user's own AI tool logins and writes to `HKCU`, so a machine-wide install buys
nothing. The wizard still offers one if you want it.

Optional during setup: a desktop shortcut. **Start with Windows is not a setup
option on purpose** — the app owns that registry entry itself (on by default,
toggled in Settings), and two writers for one value is how you get a startup
item you cannot remove.

Uninstall from Apps & features. It asks once whether to also delete your saved
settings, including any stored API keys. A *silent* uninstall always keeps them.

### Portable alternative

`AIUsageMonitor.exe` is a single 46 MB file that needs no install. It is slower
to start — a one-file build unpacks its whole payload into `%TEMP%` on every
launch, measured at **1.40 s** against **0.63 s** for the installed build, and
it leaves `_MEI*` folders behind. Use it for a USB stick; otherwise prefer the
installer.

### The installer is unsigned

SmartScreen will show "Windows protected your PC" on a machine that has not seen
the file before. Click **More info → Run anyway**. Removing that warning needs a
code-signing certificate.

---

## Services and what each can report

Every service is detected the same way: an existing CLI login, an environment
variable, or a key you save in the app. The Connections page shows what was
found for each.

| Service | Detected from | What it reports |
|---|---|---|
| **Claude** | Claude Code login | **True quota.** Session (5-hour), weekly, and per-model weekly windows, with real percentages and reset times. Plus local history: tokens per day, by model, by project. |
| **OpenAI** | Codex ChatGPT login · `OPENAI_ADMIN_KEY` / `OPENAI_API_KEY` · Admin key saved here | **Codex quota:** server-reported percentages and reset times, plus available daily token totals. Without a Codex ChatGPT login, an optional Admin key (`sk-admin-…`) provides API platform spend and tokens by model. |
| **Gemini** | Gemini CLI · `GOOGLE_APPLICATION_CREDENTIALS` · gcloud ADC · service account | **Request counts only.** From Cloud Monitoring, which is OAuth-only. Needs Monitoring Viewer on the project. |

### OpenAI: Codex quota and API spend

Sign in to Codex with ChatGPT, then refresh the OpenAI tab. The monitor reads
`auth.json` from `CODEX_HOME` (default `~/.codex`) and uses the installed Codex
executable's [App Server protocol](https://learn.chatgpt.com/docs/app-server)
to read `account/rateLimits/read`. Both Claude and Codex therefore have real
percentage gauges in the dashboard, widget and tray. Window durations and reset
times come from the server, including additional limit groups when returned.

`account/usage/read` supplies available daily **total** tokens. Missing history
does not discard quota. This data does not provide output-token, model/project,
or dollar breakdowns; select **Total tokens** to see the daily chart. Missing
days are not filled with invented zeroes. Codex limits are not a promise of
visibility into every ChatGPT feature's message limits.

A Codex ChatGPT login takes priority, including when an older installation has
saved Admin keys. No settings reset or key deletion is needed. Expired Codex
logins show a renewal message instead of silently switching to API spend.
Without a Codex ChatGPT login, the app tries the saved Admin key, environment
key, then a CLI API key. Ordinary project keys are marked Limited. API mode
retains its separate monthly spend/budget and history views.

Codex CLI or the Codex desktop app must be installed separately. The monitor
finds `codex.exe` on PATH or in the desktop app's per-user installation. It starts
a hidden App Server only during refresh, using an isolated temporary home and
the experimental externally supplied token login. Access tokens travel through
stdin, not command arguments; refresh tokens and the user's Codex configuration
are never copied. Token refresh requests are refused: open Codex to renew the
original login. This experimental integration may need updates when Codex changes.

**What still has no public API.** Codex quota covers the Codex surface, not
every ChatGPT feature's message limits, and there is no public endpoint at all
for Gemini Advanced *subscription* limits. Gemini therefore has no denominator
and shows its request count plainly rather than inventing a percentage.

Consumer-account login was deliberately not built. It would mean storing your
Google or OpenAI password, or scraping session cookies from undocumented
endpoints that break constantly and violate those services' terms.

### OpenAI shows no data: do not reset settings

The 2026-09-14 fix makes Codex ChatGPT login take priority over existing Admin
keys. An earlier Codex integration build still selected saved Admin keys first,
so existing installations could stay in API spend mode instead of showing quota.
The fix preserves keys and settings; no Registry reset is needed.

To diagnose an empty OpenAI tab:

1. Check **Connections** for the selected source. With a Codex ChatGPT login,
   the fixed build should select **Codex CLI login (ChatGPT)**.
2. Check the running executable path. Exit the old copy from the tray before
   opening another build: the single-instance guard can bring an older copy to
   the front instead. Closing the window alone normally only hides it.
3. Open the OpenAI tab and read its error banner. If the token is expired, open
   Codex to renew the login; then refresh the monitor.
4. Select **Total tokens** for the daily chart. History availability is separate
   from quota; unavailable history should not remove valid quota gauges.

The build verified in the live GUI on 2026-09-14 is
`dist/codex-first/AIUsageMonitor.exe`. The earlier `dist/openai-quota` build and
the previously installed Start Menu shortcut were not updated in that repair.

---

## How usage is counted

If your token numbers look strange, this section is why. It matters because on a
typical Claude Code workload **cache reads are around 98% of all tokens**, so
whether they count decides almost everything about your quota.

### The four kinds of token

| Kind | What it is |
|---|---|
| **Input** | New text you send that is *not* served from cache. |
| **Output** | What the model generates. |
| **Cache write** | Context written into the prompt cache the first time. |
| **Cache read** | Context served from that cache on later turns. |

A long Claude Code session re-sends its whole context every turn. Almost all of
it is a cache read, so a raw "input tokens" figure can read as near-zero while
the account is working hard. On this project's own transcripts:

```
Input              1,372      0.00%
Output           682,241      0.21%
Cache write    4,499,529      1.36%
Cache read   325,585,170     98.43%
```

If you want "how much context did I actually send", the number is
**input + cache read**, not input alone.

### Do cache reads count against the limit?

**For API rate limits (ITPM): no.** Anthropic documents that cached input tokens
do not count toward input-tokens-per-minute limits; only uncached input does.

**For the Max/Pro subscription quota: almost certainly yes — but Anthropic has
not documented the formula.** Two things point that way:

1. A community report ([claude-code issue #24147][issue]) with 30 days of data
   showing cache reads at 99.93% of all tokens and claiming they deplete the
   quota fully. There is **no maintainer reply**, so this is a user's claim, not
   confirmation.

2. An argument from magnitude, using this project's own measurements. Over the
   sampled intervals the account gained 1,364% of session capacity while
   producing 438,018 output tokens and 229,594,445 cache-read tokens. If cache
   reads did *not* count, a full session would allow roughly 32,000 output
   tokens — about **143 tokens per message** against the plan's advertised 225
   messages per 5 hours. That is implausibly small for real answers. If cache
   reads do count, the same data gives ~16.9M cache-read tokens per session, or
   ~75,000 per message, which matches a real context size.

**An attempt to prove it directly failed, and is reported as failed.** Regressing
30 days of recorded 5-hour utilisation against each token kind across 285
intervals produced correlations of |r| < 0.05 for every model tested — no signal.
The likely reason is that usage from the Claude desktop app appears in the
utilisation figure but not in the CLI transcripts this app can read, so the two
series do not describe the same work.

### What follows from it

If the inference holds, your quota is driven by **context size × number of
messages**, not by how long the answers are. Compacting the conversation and
keeping `CLAUDE.md` small will do more for your limits than writing shorter
prompts.

**The gauges are correct regardless.** They come straight from Anthropic's own
usage endpoint as percentages — the app never computes them — so whatever the
formula is, the meters reflect it.

[issue]: https://github.com/anthropics/claude-code/issues/24147

---

## Window modes

| | Dashboard | Widget |
|---|---|---|
| Size | any, from 300×220 | ≤ 300 × 300 |
| Frame | normal window | frameless, draggable anywhere on its surface |
| Always on top | no | optional (default on) |
| Transparency | opaque | adjustable, default 92% |
| Shows | tabs, gauges, stat tiles, daily chart, model and project breakdowns | a row of radial meters with reset countdowns |
| Resident memory | ~75–94 MB | ~16–26 MB |

**Switching:** drag the window below 380 px on either edge and it collapses.
Double-click the widget, or use its right-click menu, to expand again. `Ctrl+W`
toggles. Each mode remembers its own position and size.

The widget's right-click menu carries always-on-top, an **opacity slider** with a
live % readout, which service to show, refresh, expand and quit.

**One copy at a time.** Launching the app again — from the Start Menu, the
desktop shortcut, or setup's Launch checkbox — does not start a second monitor.
The new process finds the running one, asks it to come to the front, and exits.
That keeps you to one tray icon, one poller against the usage endpoint (two
would double your request rate and invite an HTTP 429), and one writer for the
saved window geometry. The lock is a per-user named pipe, so it dies with the
process: a crash or a forced kill never leaves the app unable to restart, and
two people signed in to the same machine each get their own copy.

### Meters

Both modes use the same meter language: a 270° arc, filled to the level, with
the percentage in the middle. Colour follows local thresholds — **yellow from
75%, red from 90%** — applied on top of whatever severity the server reports,
taking whichever is worse. A ⚠ glyph rides along past the threshold so the state
never rests on colour alone.

Reset always shows **both** the countdown and the wall-clock time
(`resets in 1h 43m · 18:32`): one answers how long you have, the other whether
you can go to lunch first.

The widget degrades rather than squashing — meters that cannot reach a legible
size are dropped, and below a 46 px arc the percentage moves out to the caption
instead of overprinting the ring. At the 180×120 floor, one arc survives.

### Displays and resolution

Each monitor arrangement remembers its **own** window position and size, keyed by
a signature of every screen's position, size and scale factor. Switching between
a laptop panel and a desk monitor no longer makes each overwrite the other's
layout.

When the desktop changes the window refits — it shrinks to stay on screen and
grows back to the preferred size. Windows does not reliably raise a signal when
the resolution of an existing monitor changes, so a 2-second poll backs up the Qt
signals.

---

## System tray

The tray icon is **drawn, not loaded**: a tile filled to the active service's
session level with the percentage across it, coloured by the same 75/90 rule. The
number is painted twice — ink above the fill line, white below — because the fill
line usually cuts through the digits.

With several services connected it **rotates every 2 seconds**, over only those
that have a percentage to show, and holds still when there is just one. The
tooltip names the service, since a rotating icon cannot.

- **Double-click** restores the full window.
- **Right-click** opens the menu:

```
Resume
Show Widget
─────────────
Claude                                              (header)
      Session · 5-hour window — 52% · resets in 1h 43m (15:00)
      Weekly · All models — 23% · resets in 18h 43m (Sep 11, 8:00)
─────────────
Refresh now
Setting
Exit
```

Every signed-in service lists its meters **inline at the top level**, rebuilt each
time the menu opens so the countdowns are live rather than frozen at app start.

---

## Settings

| Setting | Default | Notes |
|---|---|---|
| Start with Windows | **On** | Per-user `Run` key. After the first launch the **registry wins**, so turning it off in Windows' own Startup Apps page sticks and is not re-added. |
| Minimize to tray | **On** | Closing parks the app in the tray and it keeps watching. Exit in the tray menu quits for real. |
| Theme | **Follow Windows** | Light / Dark / Follow. Re-checked every 10 s while set to Follow. |
| Default window size | **1120 × 820** | Compact / Standard / Wide / Remember last size. Seeds the size when nothing is remembered, and is the target after a display change — a size you dragged to wins over it. Changing the setting applies immediately. |
| Refresh interval | **3 minutes** | 30 s · 1 · 3 · 5 · 10 · 30 min · manual. `F5` forces one. |
| Widget opacity | **92%** | Slider, 25–100%. |
| Always on top | **On** | Widget mode only. |
| Chart range | **14 days** | 7 / 14 / 30 / 90 days. |
| Chart metric | **Total tokens** | Total tokens · output tokens · equivalent value. |

Settings live in the registry under `HKCU\Software\AIUsageMonitor`.

---

## Resource use

Measured on the packaged executable, not estimated.

These measurements predate the Codex App Server integration. OpenAI quota
refresh temporarily starts a separate Codex process, which adds memory and
startup overhead until the request finishes; it is closed after each refresh.

| State | Memory | 1-second timer |
|---|---|---|
| Dashboard | 75–94 MB | running |
| Widget | 16–26 MB | running |
| **Parked in tray** | **7–12 MB** | **fully idle** |

Three things do the work:

1. **Aggregate on ingest.** Transcript records are folded into per-day counters
   as they are read and the record is thrown away. Nothing retains a per-message
   object, so memory is bounded by (days × models) rather than by how much
   history exists.
2. **Incremental reads.** Each refresh seeks to where the last one stopped, in
   binary mode — a text-mode seek only accepts opaque cookies from `tell()`.
3. **Idle when parked.** Hiding to the tray releases chart arrays, stops the
   per-second timer, and hands pages back to Windows. That is where the app
   spends most of its life, and it should be its cheapest state.

---

## Known limits

- **Claude history sees CLI sessions only.** Work done in the Claude desktop app
  writes to a different store and does not appear in the daily chart or the
  project breakdown. The **quota gauges are unaffected** — they are account-wide.
- **Rate limiting.** Refreshing far more often than the 3-minute default earns an
  HTTP 429. The app degrades correctly: an error banner appears and local history
  still renders.
- **Mixed-DPI monitors.** Dragging the window between screens at different scale
  factors can briefly show the wrong physical size before it settles.
- **Tray icon starts hidden.** Windows 11 puts new tray icons in the overflow
  (the `^` chevron) until you drag one out to pin it.
- **Expired Claude token.** The app never refreshes it — Claude Code owns that
  cycle, and a second process writing that file can invalidate your login. If it
  expires the app says so and asks you to start Claude Code.
- **Codex login and runtime.** Requires a file-backed ChatGPT login and an
  installed Codex executable. Keyring-only logins are not detected. Expired or
  rejected tokens require opening Codex again. A timeout or unavailable token
  history is shown as an error rather than being presented as zero usage.

---

## Credential handling

Credential access is **strictly read-only**. The app never writes another tool's
login file and never attempts a token refresh.

API keys you save here are sealed with **Windows DPAPI** (`CryptProtectData`)
before being written to settings, bound to your Windows user account — copying
the blob to another machine or user yields nothing, and the plaintext never
touches disk. Where DPAPI is unavailable, storage refuses rather than silently
writing plaintext.

Network requests are only ever made to the services you have connected, using
their documented endpoints.

---

## Building

```powershell
.\build_ai_installer.ps1     # portable .exe + installer
```

Builds the one-file portable executable, the one-directory tree, and wraps the
latter with Inno Setup. Needs Inno Setup 6:

```powershell
winget install JRSoftware.InnoSetup
```

The script finds `ISCC.exe` in Program Files, `%LocalAppData%\Programs`, or
wherever the uninstall entry points — winget installs it per-user, which is not
where you would first look.

To build just the portable exe:

```powershell
.\.venv\Scripts\python.exe -m PyInstaller AIUsageMonitor.spec --noconfirm
```

From source:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m ai_usage_monitor
```

To change the version, edit `AppVersion` in `installer\AIUsageMonitor.iss` and
`version_info.txt`. The `AppId` GUID must stay fixed: it is what makes an upgrade
replace the existing install instead of stacking a second entry in Apps &
features.

---

## Architecture

```
ai_usage_monitor/
  providers/
    base.py              the Provider contract: Meter / Stat / ProviderSnapshot
    claude_provider.py   OAuth quota + local transcript history
    openai_provider.py   Codex quota/history or Admin Usage & Costs API
    gemini_provider.py   Cloud Monitoring via service-account JWT
    sources.py           where each service's login can live
  app.py                 QApplication setup, then the single-instance gate
  single_instance.py     one running copy per user, via a named local socket
  detection.py           finds an existing sign-in, read-only
  codex_usage.py         isolated Codex App Server client and quota/history mapping
  usage_log.py           incremental, aggregate-on-ingest transcript parsing
  api.py                 Claude OAuth usage/profile endpoints
  credentials.py         read-only access to ~/.claude/.credentials.json
  secrets.py             DPAPI seal/unseal for API keys
  startup.py             the start-with-Windows registry entry
  memory.py              working-set trim and measurement
  pricing.py             per-token list prices for equivalent-value figures
  formatting.py          token counts, durations and reset times as text
  tray.py                the drawn tray icon, rotation and menu
  worker.py              background refresh across providers
  settings.py            typed QSettings wrapper
  theme.py               palettes, severity thresholds, Windows theme probe
  main_window.py         tabs, mode switching, widget chrome, display handling
  dashboard.py           one provider's full page
  connections_page.py    the landing page: every service, detected the same way
  connect_dialog.py      one shared sign-in dialog
  settings_dialog.py     preferences
  readme_dialog.py       this document, in-app
  widgets/               gauge, charts, cards, compact view — all QPainter
tools/make_icon.py       generates assets/icon.ico
```

Adding a service means writing one `Provider` subclass and adding it to
`providers/__init__.py`; the tabs, connection cards and refresh loop are all
driven off that list.

Run the OpenAI detection, protocol, quota/history and API regression tests with:

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

### Notes

- Charts avoid a second y-axis, assign categorical hues in fixed order, and fold
  past the eighth series into "Other". Axis maxima are chosen by rounding the
  *step*, so ticks land on round numbers.
- Dollar figures for Claude are labelled *equivalent API value* — a Max or Pro
  subscription is not billed per token. OpenAI dollar figures in Admin API mode
  are real spend; Codex quota mode does not report dollar values.
- Cache pricing follows the published multipliers: a 5-minute cache write costs
  1.25× the input rate, a 1-hour write 2×, and a cache read 0.1×.
- A failure in one service never discards another's result; each is fetched and
  reported independently, with its own error banner.

## Working with Claude Code

Read [CLAUDE.md](https://github.com/epinephrinerx/AIMonitor/blob/main/CLAUDE.md)
before changing the project. It records the active package, Codex-first
detection requirements, credential handling, the 2026-09-14 fix and
verification results, and commands for testing and building. Keep both
documents aligned with the implementation when behavior changes.
