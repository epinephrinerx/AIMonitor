"""Background refresh across every configured provider.

Everything that touches the network or the filesystem runs here so the window
never blocks. The worker owns the provider objects outright and emits finished
snapshots, so no mutable state is shared across threads.

Two things keep the cost down:

* `want_history` is false in widget mode, so a compact window never parses
  transcripts or asks a provider for chart data.
* A failure in one provider never discards another's result - each is fetched
  and reported independently.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass, field

from PySide6.QtCore import QObject, Signal, Slot

from . import memory
from .providers import Provider, ProviderSnapshot, build_all


@dataclass
class RefreshResult:
    snapshots: dict[str, ProviderSnapshot] = field(default_factory=dict)
    finished_at: dt.datetime | None = None
    working_set_mb: float = 0.0


class RefreshWorker(QObject):
    """Lives on its own thread; `refresh` is invoked via a queued connection."""

    finished = Signal(object)  # RefreshResult

    def __init__(self) -> None:
        super().__init__()
        self.providers: list[Provider] = build_all()
        self._by_id = {provider.id: provider for provider in self.providers}
        self._enabled: dict[str, bool] = {p.id: True for p in self.providers}

    @Slot(str, str, str)
    def apply_credentials(self, provider_id: str, key: str, extra: str) -> None:
        provider = self._by_id.get(provider_id)
        if provider is not None:
            provider.configure(key, extra)

    @Slot(str, bool)
    def set_enabled(self, provider_id: str, enabled: bool) -> None:
        self._enabled[provider_id] = enabled

    @Slot(int)
    def set_throughput_interval(self, seconds: int) -> None:
        for provider in self.providers:
            setter = getattr(provider, "set_throughput_interval", None)
            if callable(setter):
                setter(seconds)

    @Slot(int, str, bool, str)
    def refresh(
        self, days: int, metric: str, want_history: bool, only_provider: str
    ) -> None:
        result = RefreshResult()
        for provider in self.providers:
            if not self._enabled.get(provider.id, True):
                continue
            if only_provider and provider.id != only_provider:
                continue
            try:
                result.snapshots[provider.id] = provider.fetch(
                    days, metric, want_history
                )
            except Exception as exc:  # noqa: BLE001 - one provider must not kill the rest
                result.snapshots[provider.id] = ProviderSnapshot(
                    provider_id=provider.id,
                    configured=provider.is_configured(),
                    error=f"Unexpected error: {exc}",
                    setup_hint=provider.setup_hint,
                )

        # Parsing and JSON decoding churn a lot of short-lived objects; hand the
        # pages back rather than sitting on them until the next refresh.
        memory.trim_working_set()
        result.finished_at = dt.datetime.now(dt.timezone.utc)
        result.working_set_mb = memory.working_set_mb()
        self.finished.emit(result)
