"""Typed wrapper over QSettings, including sealed provider keys."""

from __future__ import annotations

import os

from PySide6.QtCore import QSettings

from . import secrets

ORG = "ClaudeUsageMonitor"
APP = "ClaudeUsageMonitor"

# Test and screenshot harnesses set this so they never write to the real user's
# settings. A run once left the shipped app on "Manual only" with a collapsed
# window geometry because a harness reused the production scope.
SCOPE_ENV_VAR = "CLAUDE_MONITOR_SETTINGS_SCOPE"

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

WIDGET_MAX_EDGE = 300  # the widget must fit inside 300x300
WIDGET_TRIGGER_EDGE = 380  # shrinking past this switches modes


class Settings:
    def __init__(self) -> None:
        scope = os.environ.get(SCOPE_ENV_VAR, "").strip()
        self._q = QSettings(ORG, scope or APP)

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
        return min(1.0, max(0.25, self._float("widget/opacity", 0.92)))

    @opacity.setter
    def opacity(self, value: float) -> None:
        self._q.setValue("widget/opacity", round(float(value), 2))

    @property
    def always_on_top(self) -> bool:
        return self._bool("widget/alwaysOnTop", True)

    @always_on_top.setter
    def always_on_top(self, value: bool) -> None:
        self._q.setValue("widget/alwaysOnTop", bool(value))

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
    def throughput_interval(self) -> int:
        """Seconds per throughput summary bucket. Default 3 minutes."""
        return self._int("throughputInterval", 180)

    @throughput_interval.setter
    def throughput_interval(self, value: int) -> None:
        self._q.setValue("throughputInterval", int(value))

    @property
    def active_provider(self) -> str:
        return str(self._q.value("activeProvider", "claude"))

    @active_provider.setter
    def active_provider(self, value: str) -> None:
        self._q.setValue("activeProvider", value)

    # -- geometry --------------------------------------------------------

    def save_geometry(self, name: str, value) -> None:
        self._q.setValue(f"geometry/{name}", value)

    def load_geometry(self, name: str):
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
