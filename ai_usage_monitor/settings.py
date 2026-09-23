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



def _own_scope(org: str, app: str) -> QSettings:
    r"""A view of exactly one application's own settings, and nothing else.

    `QSettings` consults fallbacks by default: an organisation-wide default
    under `HKCU\Software\<org>\OrganizationDefaults`, and the system scope.
    `value()` answers from those, and `allKeys()` even lists their names, so
    asking a plain `QSettings` "do you already have this key?" does not ask
    about its own storage at all.

    That is not a hypothetical. Measured: with an organisation default in
    place, a brand-new application key reports `value("theme")` as
    `'from-org-defaults'` rather than `None`. Migration would read that as
    "already carried over", skip the copy, and then the cleanup below would
    delete the only real copy the user had. Every decision about what has
    been migrated is made through this.
    """
    store = QSettings(org, app)
    store.setFallbacksEnabled(False)
    return store


def _prune_empty_registry_keys(org: str, app: str) -> bool:
    r"""Remove `HKCU\Software\<org>\<app>`, then the organisation key if it
    is left holding nothing. True when the organisation key is gone.

    `QSettings.clear()` empties a key and leaves it, so the old name stays
    visible in the registry editor - which is the whole complaint.

    Two rules, and the second one was learned the hard way:

    * only keys with no values and no subkeys are deleted. `DeleteKey`
      refuses when subkeys remain but will happily delete a key that still
      holds values, which is not the guarantee this needs - a test caught an
      earlier version doing exactly that.
    * the recursive walk stays inside `<app>`. The organisation key is
      examined, never descended into. An earlier version pruned from the
      leaves of the organisation and so deleted an **empty sibling
      application** that had nothing to do with this one; measured, and
      nothing had asked for it.

    There is a window between reading a key and deleting it in which another
    process could write a value, and Windows offers no way to hold a registry
    key against that. It is narrow, and the alternative is leaving the key
    forever.
    """
    if not _is_safe_key_name(org) or not _is_safe_key_name(app):
        return False
    try:
        import winreg
    except ImportError:  # not Windows; QSettings used a file we do not own
        return False

    def empty_and_delete(path: str) -> bool:
        """Delete `path` if it holds nothing. True when it is gone."""
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
                subkeys, values, _ = winreg.QueryInfoKey(key)
            if subkeys or values:
                return False
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
            return True
        except FileNotFoundError:
            return True
        except OSError:
            return False

    def prune_subtree(path: str) -> bool:
        """Children first, then the key itself. Confined to one subtree."""
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
                count, _, _ = winreg.QueryInfoKey(key)
                children = [winreg.EnumKey(key, i) for i in range(count)]
        except FileNotFoundError:
            return True
        except OSError:
            return False
        for child in children:
            prune_subtree(f"{path}\\{child}")
        return empty_and_delete(path)

    if not prune_subtree(rf"Software\{org}\{app}"):
        return False
    # The organisation key is only removed when nothing else lives under it,
    # and its other tenants are left exactly as they are.
    return empty_and_delete(rf"Software\{org}")


def _delete_registry_value(org: str, app: str, key: str, expected=None) -> bool:
    r"""Delete one value, named by a QSettings-style `group/name` path.

    Not `QSettings.remove()`: that removes the key **and every setting
    beneath it**, so removing a `geometry/dashboard` that was checked would
    also take a `geometry/dashboard/...` that arrived afterwards and never
    was. Measured. This removes exactly the one value it is given.

    `expected` is re-read and compared under the same open handle, so the
    gap between deciding a value is a spent duplicate and removing it is as
    small as this can be made. It cannot be closed: Windows offers no way to
    hold a registry key against another writer, so there is no atomic
    compare-and-delete. What is left is a window of microseconds, and a
    writer that would have to be a build retired several releases ago.
    """
    if not _is_safe_key_name(org) or not _is_safe_key_name(app):
        return False
    try:
        import winreg
    except ImportError:
        return False
    *groups, name = key.split("/")
    if not name or any(not _is_safe_key_name(part) for part in groups):
        return False
    # Qt's Windows backend spells the default unnamed value as `Default` or
    # `.`, and hands those names back from `allKeys()`. Deleting a value
    # literally called "Default" would miss it, the key would still hold
    # something, and the tidy-up would never finish.
    if name in ("Default", "."):
        name = ""
    path = "\\".join([rf"Software\{org}\{app}", *groups])
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            path,
            0,
            winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE,
        ) as handle:
            if expected is not None:
                try:
                    current, _ = winreg.QueryValueEx(handle, name)
                except FileNotFoundError:
                    return True  # already gone
                if str(current) != str(expected):
                    return False  # rewritten since it was checked
            winreg.DeleteValue(handle, name)
        return True
    except FileNotFoundError:
        return True
    except OSError:
        return False


def _is_safe_key_name(name: str) -> bool:
    """One registry key name, not a path and not nothing.

    The function above builds a path by interpolation, so a name carrying a
    separator would move which key gets examined and deleted. Production only
    ever passes a module constant, but a generic signature invites reuse.
    """
    return bool(name) and not any(c in name for c in "\\/")


class Settings:
    def __init__(self) -> None:
        scope = os.environ.get(SCOPE_ENV_VAR, "").strip()
        self._q = QSettings(ORG, scope or APP)
        if not scope:
            self._migrate_from_legacy()

    def _migrate_from_legacy(self) -> None:
        """Carry settings over from the pre-rename location, then remove it.

        Copies every key rather than a hand-listed subset, so sealed provider
        keys come across too; `secrets.unseal` still reads them because it
        falls back to the old DPAPI entropy.

        This used to be a copy and nothing else, so that an older build would
        still find its settings. That build was retired and removed from the
        repository, and what the choice left behind was a `ClaudeUsageMonitor`
        key sitting in the registry of everyone who had ever run the old
        version - a name the application had otherwise finished with.

        A key already present here is left alone rather than overwritten: the
        value this application holds is the newer of the two, and the one the
        user last chose.
        """
        own = _own_scope(ORG, APP)
        if own.value("migratedFromLegacy") is None:
            legacy = _own_scope(LEGACY_ORG, LEGACY_APP)
            for key in legacy.allKeys():
                if own.value(key) is None:
                    self._q.setValue(key, legacy.value(key))
            self._q.setValue("migratedFromLegacy", True)
            self._q.sync()
        self._discard_legacy()

    def _discard_legacy(self) -> None:
        """Remove the pre-rename settings that are provably duplicates.

        Separate from the copy above because most installations migrated long
        ago: they are already marked and would never reach this through the
        copy path.

        "Provably" is doing real work here, and it used to be doing less. A
        value is removed only when the one this application holds **equals**
        it. A key of the same name is not proof of anything: the copy step
        does not overwrite, so a name can match while the contents differ -
        a setting changed since the migration, or a sealed credential this
        application stored badly over one the old location still holds
        intact. Anything that does not match is left where it is, and the
        old key then stays too. Clutter is much the lesser mistake.

        The rest of the care:

        * every question about what is already here is asked of this
          application's own storage, with fallbacks off - see `_own_scope`;
        * the copies are flushed and checked before an original is touched,
          because `QSettings` does not promise a `setValue` has reached
          storage until `sync()` says so;
        * values are deleted one at a time, by name, through the registry -
          `QSettings.remove()` takes everything beneath the name with it;
        * the marker is set only once the old location is really gone, so a
          failure anywhere means the next launch tries again rather than
          leaving the key forever.
        """
        own = _own_scope(ORG, APP)
        if own.value("legacyDiscarded") is not None:
            return
        legacy = _own_scope(LEGACY_ORG, LEGACY_APP)

        duplicates = []
        for key in legacy.allKeys():
            here, there = own.value(key), legacy.value(key)
            if here is None or here != there:
                return  # not ours to tidy: it was never copied, or it differs
            duplicates.append((key, there))

        self._q.sync()
        if self._q.status() != QSettings.Status.NoError:
            return

        for key, value in duplicates:
            if not _delete_registry_value(LEGACY_ORG, LEGACY_APP, key, value):
                return
        legacy.sync()
        if legacy.status() != QSettings.Status.NoError or legacy.allKeys():
            # Something arrived after the list was taken, or a write failed.
            # Leave the marker unset so the next launch looks again.
            return

        if not _prune_empty_registry_keys(LEGACY_ORG, LEGACY_APP):
            return
        self._q.setValue("legacyDiscarded", True)
        self._q.sync()

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
