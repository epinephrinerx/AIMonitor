"""Typed wrapper over QSettings, including sealed provider keys."""

from __future__ import annotations

import os
import sys

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
WIDGET_TRIGGER_EDGE = 380  # shrinking past this switches modes

# A sane dashboard size: used to validate stored geometry and as the floor when
# fitting to a screen. NOT the window's minimum - see below.
DASHBOARD_MIN_W = 760
DASHBOARD_MIN_H = 560

# The actual minimum the user can drag to. It has to sit BELOW
# WIDGET_TRIGGER_EDGE, or the window can never be dragged small enough to
# collapse into the widget and that gesture silently stops working.
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

_SYSTEM_THEME_LABEL = {
    "win32": "Follow Windows · default",
    "darwin": "Follow macOS · default",
}.get(sys.platform, "Follow system · default")

THEME_OPTIONS = [
    (_SYSTEM_THEME_LABEL, "system"),
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
    def start_at_login(self) -> bool:
        """On by default: a usage monitor you have to remember to launch is a
        usage monitor you find out about after you have hit the limit.

        Stored under the original `startWithWindows` name so an existing
        Windows install keeps its choice; what it means is now per-platform -
        a Run key on Windows, a LaunchAgent on macOS.
        """
        return self._bool("startWithWindows", True)

    @start_at_login.setter
    def start_at_login(self, value: bool) -> None:
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
        path = f"providers/{provider_id}/key"
        return secrets.unseal(str(self._q.value(path, "")), path)

    def set_provider_key(self, provider_id: str, plaintext: str) -> None:
        """Store a key, or clear it when `plaintext` is empty.

        The settings path doubles as the secret's handle, so the Keychain
        backend has a stable name to file the item under - one item per
        provider, replaced rather than accumulated when a key is re-entered.
        """
        path = f"providers/{provider_id}/key"
        if not plaintext:
            self._q.remove(path)
            # On macOS the settings value was only a pointer; the secret behind
            # it has to go too, or "clear" leaves the key in the Keychain.
            secrets.forget(path)
            return
        self._q.setValue(path, secrets.seal(plaintext, path))

    def provider_extra(self, provider_id: str) -> str:
        """A non-secret companion value (budget, project id)."""
        return str(self._q.value(f"providers/{provider_id}/extra", ""))

    def set_provider_extra(self, provider_id: str, value: str) -> None:
        self._q.setValue(f"providers/{provider_id}/extra", value)

    def provider_enabled(self, provider_id: str, default: bool = True) -> bool:
        return self._bool(f"providers/{provider_id}/enabled", default)

    def set_provider_enabled(self, provider_id: str, value: bool) -> None:
        self._q.setValue(f"providers/{provider_id}/enabled", bool(value))
