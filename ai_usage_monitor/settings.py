"""Typed wrapper over QSettings, including sealed provider keys."""

from __future__ import annotations

import datetime as dt
import os
import re

from PySide6.QtCore import QSettings

from . import secrets

ORG = "AIUsageMonitor"
APP = "AIUsageMonitor"

# Settings written before the app was renamed from ClaudeUsageMonitor.
LEGACY_ORG = "ClaudeUsageMonitor"
LEGACY_APP = "ClaudeUsageMonitor"

# Reload cadence. Default is 3 minutes: often enough to catch a window filling
# up, rare enough that the app is not a background load.
DEFAULT_INTERVAL = 180

INTERVAL_OPTIONS = [
    ("Every 30 seconds", 30),
    ("Every minute", 60),
    ("Every 3 minutes · default", DEFAULT_INTERVAL),
    ("Every 5 minutes", 300),
    ("Every 10 minutes", 600),
    ("Every 30 minutes", 1800),
    ("Manual only", 0),
]

RANGE_OPTIONS = [("7 days", 7), ("14 days", 14), ("30 days", 30), ("90 days", 90)]

# Widget opacity, in whole percent. Below about 25% the meters stop being
# readable against a busy desktop, so that is the floor the UI offers and the
# floor `Settings.opacity` clamps to. Both sliders - the one in Settings and
# the one in the widget's context menu - work in these units.
OPACITY_MIN = 25
OPACITY_MAX = 100
OPACITY_STEP = 5
DEFAULT_OPACITY = 92

WIDGET_MAX_EDGE = 300  # the widget must fit inside 300x300
# The floor the user can drag down to. The meter row degrades on the way
# down - arcs that cannot reach a legible size are dropped, and the
# percentage moves out of the ring into the caption - so this is about the
# smallest that still reads at a glance.
WIDGET_MIN_W = 150
WIDGET_MIN_H = 96

# A sane dashboard size: used to validate stored geometry and as the floor when
# fitting to a screen. NOT the window's minimum - see below.
DASHBOARD_MIN_W = 760
DASHBOARD_MIN_H = 560

# The actual minimum the user can drag to, and the floor a stored dashboard
# rect has to clear to be believed. Resizing no longer changes modes, so this
# is just a size: anything at or above it is a size someone chose.
DASHBOARD_DRAG_MIN_W = 300
DASHBOARD_DRAG_MIN_H = 220

# Presets for the default dashboard size. The stored value is a (w, h) pair, so
# a window the user sized by hand is still remembered separately as geometry.
WINDOW_SIZE_OPTIONS = [
    ("Compact · 960 × 680", (960, 680)),
    ("Standard · 1120 × 820 · default", (1120, 820)),
    ("Wide · 1400 × 900", (1400, 900)),
    ("Remember last size", (0, 0)),
]
DEFAULT_WINDOW_SIZE = (1120, 820)

# How many display layouts keep a remembered window position. Enough for a
# laptop, the same laptop docked, a second desk and a projector, with room to
# spare; past that the oldest are forgotten rather than kept forever.
RETAINED_LAYOUTS = 8

# Exactly what `MainWindow._display_signature` produces: "d" and ten hex
# digits of a SHA-1. Nothing else under `geometry/` is a display layout, and
# nothing else may be deleted as though it were one.
LAYOUT_ID = re.compile(r"d[0-9a-f]{10}")


def _now_stamp() -> str:
    """Sortable, and readable by anyone who opens the settings tree.

    UTC, not local time. Local time is not monotonic: it steps backwards an
    hour at a DST fall-back and jumps when the machine changes zone, either
    of which would make a layout used minutes ago sort as the oldest and be
    the one deleted. Microseconds because two layouts saved inside the same
    second would otherwise compare equal and the tie would decide which
    survived.

    The trailing `Z` is what makes the value self-describing. Without it a
    stamp written in local time is indistinguishable from one written in UTC,
    and the two sort together as though they meant the same thing - an hour
    of local offset reading as an hour of age. `_stamp_is_utc` is the other
    half of that: anything unmarked is treated as predating this rule rather
    than silently trusted.
    """
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def _stamp_is_utc(stamp: str) -> bool:
    return stamp.endswith("Z")


THEME_OPTIONS = [
    ("Follow Windows · default", "system"),
    ("Light", "light"),
    ("Dark", "dark"),
]

# Test and screenshot harnesses set this so they never write to the real user's
# settings.
SCOPE_ENV_VAR = "AI_USAGE_MONITOR_SETTINGS_SCOPE"


class Settings:
    def __init__(self) -> None:
        scope = os.environ.get(SCOPE_ENV_VAR, "").strip()
        self._q = QSettings(ORG, scope or APP)
        if not scope:
            self._migrate_from_legacy()

    def _migrate_from_legacy(self) -> None:
        """Carry settings over from the pre-rename location, once.

        Copies every key rather than a hand-listed subset, so sealed provider
        keys come across too; `secrets.unseal` still reads them because it
        falls back to the old DPAPI entropy. The old location is left intact -
        this is a copy, not a move, so an older build still works.
        """
        if self._q.value("migratedFromLegacy") is not None:
            return
        legacy = QSettings(LEGACY_ORG, LEGACY_APP)
        for key in legacy.allKeys():
            if self._q.value(key) is None:
                self._q.setValue(key, legacy.value(key))
        self._q.setValue("migratedFromLegacy", True)

    # -- generic ---------------------------------------------------------

    def _int(self, key: str, default: int) -> int:
        try:
            return int(self._q.value(key, default))
        except (TypeError, ValueError):
            return default

    def _bool(self, key: str, default: bool) -> bool:
        value = self._q.value(key, default)
        if isinstance(value, bool):
            return value
        return str(value).lower() in ("1", "true", "yes")

    def _float(self, key: str, default: float) -> float:
        try:
            return float(self._q.value(key, default))
        except (TypeError, ValueError):
            return default

    # -- appearance ------------------------------------------------------

    @property
    def theme(self) -> str:
        value = str(self._q.value("theme", "system"))
        return value if value in ("system", "light", "dark") else "system"

    @theme.setter
    def theme(self, value: str) -> None:
        self._q.setValue("theme", value)

    @property
    def opacity(self) -> float:
        stored = self._float("widget/opacity", DEFAULT_OPACITY / 100)
        return min(OPACITY_MAX / 100, max(OPACITY_MIN / 100, stored))

    @opacity.setter
    def opacity(self, value: float) -> None:
        self._q.setValue("widget/opacity", round(float(value), 2))

    @property
    def always_on_top(self) -> bool:
        return self._bool("widget/alwaysOnTop", True)

    @always_on_top.setter
    def always_on_top(self, value: bool) -> None:
        self._q.setValue("widget/alwaysOnTop", bool(value))

    @property
    def widget_rotate(self) -> bool:
        """Cycle the widget through every service, the way the tray does.

        On by default: a widget pinned to one service looked broken next to
        a tray icon that was visibly rotating. Pinning one service from the
        Show menu turns this off and restores the single-provider fetch.
        """
        return self._bool("widget/rotate", True)

    @widget_rotate.setter
    def widget_rotate(self, value: bool) -> None:
        self._q.setValue("widget/rotate", bool(value))

    # -- refresh ---------------------------------------------------------

    @property
    def interval(self) -> int:
        return self._int("interval", DEFAULT_INTERVAL)

    @interval.setter
    def interval(self, value: int) -> None:
        self._q.setValue("interval", int(value))

    @property
    def range_days(self) -> int:
        return self._int("range_days", 14)

    @range_days.setter
    def range_days(self, value: int) -> None:
        self._q.setValue("range_days", int(value))

    @property
    def metric(self) -> str:
        return str(self._q.value("metric", "Total tokens"))

    @metric.setter
    def metric(self, value: str) -> None:
        self._q.setValue("metric", value)

    @property
    def show_connections_at_startup(self) -> bool:
        return self._bool("showConnectionsAtStartup", True)

    @show_connections_at_startup.setter
    def show_connections_at_startup(self, value: bool) -> None:
        self._q.setValue("showConnectionsAtStartup", bool(value))

    # -- startup and tray -------------------------------------------------

    @property
    def start_with_windows(self) -> bool:
        """On by default: a usage monitor you have to remember to launch is a
        usage monitor you find out about after you have hit the limit."""
        return self._bool("startWithWindows", True)

    @start_with_windows.setter
    def start_with_windows(self, value: bool) -> None:
        self._q.setValue("startWithWindows", bool(value))

    @property
    def minimize_to_tray(self) -> bool:
        """On by default: closing the window parks it in the tray so the
        monitor keeps watching, and Exit in the tray menu really quits."""
        return self._bool("minimizeToTray", True)

    @minimize_to_tray.setter
    def minimize_to_tray(self, value: bool) -> None:
        self._q.setValue("minimizeToTray", bool(value))

    # -- default window size ----------------------------------------------

    @property
    def window_size(self) -> tuple[int, int]:
        """(0, 0) means 'remember whatever size the window was last left at'."""
        width = self._int("window/width", DEFAULT_WINDOW_SIZE[0])
        height = self._int("window/height", DEFAULT_WINDOW_SIZE[1])
        if width == 0 or height == 0:
            return (0, 0)
        return (max(DASHBOARD_MIN_W, width), max(DASHBOARD_MIN_H, height))

    @window_size.setter
    def window_size(self, value: tuple[int, int]) -> None:
        self._q.setValue("window/width", int(value[0]))
        self._q.setValue("window/height", int(value[1]))

    @property
    def active_provider(self) -> str:
        return str(self._q.value("activeProvider", "claude"))

    @active_provider.setter
    def active_provider(self, value: str) -> None:
        self._q.setValue("activeProvider", value)

    def mark_once(self, key: str) -> bool:
        """True the first time only, recording that it has now happened.

        For one-shot hints - a tray balloon that is helpful once and nagging
        every time after.
        """
        flag = f"once/{key}"
        if self._q.value(flag) is not None:
            return False
        self._q.setValue(flag, True)
        return True

    # -- geometry --------------------------------------------------------

    def save_geometry(self, name: str, value, display: str = "") -> None:
        """Store a window rect, optionally scoped to a display layout.

        Scoping matters on a machine that moves between a laptop panel and a
        desk monitor: one shared rect means each setup overwrites the other's
        position, so you never get your layout back on either.
        """
        self._q.setValue(f"geometry/{name}", value)
        if display:
            self._q.setValue(f"geometry/{display}/{name}", value)
            self._q.setValue(f"geometry/{display}/usedAt", _now_stamp())
            self._forget_stale_layouts()

    def _forget_stale_layouts(self) -> None:
        """Keep the most recently used layouts and drop the rest.

        Every monitor arrangement the machine has ever been in leaves a
        subkey behind - a laptop panel, the same laptop docked, a meeting
        room projector - and nothing ever removed one. It is only a few
        hundred bytes each, so this is tidiness rather than a leak, but the
        settings tree is something a person may go and look at.

        This routine deletes from the user's settings, so it is deliberately
        narrow about what it is willing to touch:

        * only ids matching `LAYOUT_ID`, the exact shape
          `MainWindow._display_signature` produces. A hand-written key, a key
          from an older build, or one a later feature puts under `geometry/`
          is not this function's to remove.
        * never an empty id. `geometry/usedAt` would otherwise slice down to
          `""` and turn into `remove("geometry/")`, which QSettings reads as
          the whole group - every remembered rect, gone.
        * never the unscoped rect, which is the fallback for an arrangement
          that has not been seen before.
        """
        stamps: list[tuple[tuple[int, str], str, str]] = []
        for key in self._q.allKeys():
            if not key.startswith("geometry/") or not key.endswith("/usedAt"):
                continue
            layout = key[len("geometry/"):-len("/usedAt")]
            if not LAYOUT_ID.fullmatch(layout):
                continue
            stamp = str(self._q.value(key, ""))
            # Unmarked stamps were written before this became UTC, in whatever
            # zone the machine was in, so their ordering against a UTC stamp
            # means nothing: on UTC+7 a local 10:00 outranks a UTC 03:30 that
            # is half an hour *newer*. Rank them all behind the marked ones
            # instead of guessing. They are the first to go, the layout being
            # saved right now always carries a marked stamp so it is never the
            # victim, and after eight saves none are left.
            rank = 1 if _stamp_is_utc(stamp) else 0
            stamps.append(((rank, stamp), stamp, layout))
        if len(stamps) <= RETAINED_LAYOUTS:
            return
        stamps.sort(reverse=True)  # newest first, unmarked last
        for _, stamp, layout in stamps[RETAINED_LAYOUTS:]:
            if not LAYOUT_ID.fullmatch(layout):   # belt and braces before a delete
                continue
            # Read the timestamp again rather than trusting the snapshot the
            # sort was built from. Between the two, another copy of the app -
            # a second signed-in user, or one that started while the guard was
            # briefly down - may have used this very layout, and deleting a
            # layout somebody is sitting at is exactly what "least recently
            # used" is supposed to prevent.
            if str(self._q.value(f"geometry/{layout}/usedAt", "")) != stamp:
                continue
            self._q.remove(f"geometry/{layout}")

    def load_geometry(self, name: str, display: str = ""):
        """Prefer the rect saved for this exact display layout.

        Falls back to the last-used rect, which is also what an install made
        before layouts were scoped will have.
        """
        if display:
            scoped = self._q.value(f"geometry/{display}/{name}")
            if scoped is not None:
                return scoped
        return self._q.value(f"geometry/{name}")

    # -- provider credentials --------------------------------------------

    def provider_key(self, provider_id: str) -> str:
        """Decrypt and return the stored key, or '' if unset/unreadable."""
        return secrets.unseal(str(self._q.value(f"providers/{provider_id}/key", "")))

    def set_provider_key(self, provider_id: str, plaintext: str) -> None:
        path = f"providers/{provider_id}/key"
        if not plaintext:
            self._q.remove(path)
            return
        self._q.setValue(path, secrets.seal(plaintext))

    def provider_extra(self, provider_id: str) -> str:
        """A non-secret companion value (budget, project id)."""
        return str(self._q.value(f"providers/{provider_id}/extra", ""))

    def set_provider_extra(self, provider_id: str, value: str) -> None:
        self._q.setValue(f"providers/{provider_id}/extra", value)

    def provider_enabled(self, provider_id: str, default: bool = True) -> bool:
        return self._bool(f"providers/{provider_id}/enabled", default)

    def set_provider_enabled(self, provider_id: str, value: bool) -> None:
        self._q.setValue(f"providers/{provider_id}/enabled", bool(value))
