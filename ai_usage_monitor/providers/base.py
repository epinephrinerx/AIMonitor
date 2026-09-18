"""The provider contract every monitored service implements.

A provider answers one question: "what does this account's usage look like right
now?" It returns meters (things with a percentage and a reset time), stats
(things that are just a number), and optionally a local history series.

Not every service can fill in every field, and the model is deliberate about
that. Claude exposes a real server-side quota endpoint, so its meters carry true
percentages, as does OpenAI's Codex quota. API spend without a local budget and
Gemini request counts use `percent=None`, rendered as a stat without inventing
a denominator.
"""

from __future__ import annotations

import datetime as dt
import threading
from dataclasses import dataclass, field

from ..detection import Detection, Source, resolve


@dataclass(frozen=True)
class Meter:
    """A usage window. `percent` is None when the service exposes no denominator."""

    key: str
    title: str
    subtitle: str
    percent: float | None
    severity: str = "normal"
    resets_at: dt.datetime | None = None
    detail: str = ""
    locked_reason: str | None = None

    @property
    def resets_in(self) -> dt.timedelta | None:
        if self.resets_at is None:
            return None
        return self.resets_at - dt.datetime.now(dt.timezone.utc)


@dataclass(frozen=True)
class Stat:
    label: str
    value: str
    detail: str = ""


@dataclass
class HistoryView:
    """Chart-ready aggregates for the selected range and metric."""

    buckets: list = field(default_factory=list)
    series: list[str] = field(default_factory=list)
    by_model: list[tuple[str, float]] = field(default_factory=list)
    by_project: list[tuple[str, float]] = field(default_factory=list)
    days: int = 14
    metric: str = "Total tokens"
    project_label: str = "By project"


@dataclass
class ProviderSnapshot:
    """Everything one tab needs to render itself."""

    provider_id: str
    configured: bool = False
    meters: list[Meter] = field(default_factory=list)
    stats: list[Stat] = field(default_factory=list)
    history: HistoryView | None = None
    account: str = ""
    # A refresh that produced nothing usable. Setting this means the service
    # failed, and the tab says so.
    error: str | None = None
    # The daily history could not be read, but whatever else arrived is good.
    # Quota comes from the server and local or historical data has no bearing
    # on it, so a history failure must never travel as `error`: doing that
    # marked the whole service failed and took live gauges off the screen.
    history_error: str | None = None
    unauthorized: bool = False
    setup_hint: str = ""
    value_note: str = ""
    fetched_at: dt.datetime | None = None
    detection: Detection | None = None

    @property
    def ok(self) -> bool:
        return self.configured and self.error is None


class Provider:
    """Base class. Subclasses are cheap to construct and hold no Qt objects."""

    # Set by the worker before each fetch. A provider with long waits inside
    # one call - a subprocess to read, a range to page through - checks this
    # and gives up early, so closing the window does not have to sit out the
    # full socket timeout of work nobody is waiting for any more.
    #
    # Deliberately an attribute rather than a `fetch` argument: only the
    # providers that can act on it need to know it exists, and the contract
    # every provider implements stays the one question it was.
    cancel: "threading.Event | None" = None

    id: str = ""
    display_name: str = ""
    # Shown on the settings card when the provider has no credentials yet.
    setup_hint: str = ""
    # True when the provider needs a key the user must paste in.
    needs_key: bool = True
    key_label: str = "API key"
    key_placeholder: str = ""
    # Optional non-secret companion field (budget, project id). Empty label
    # means the provider has no second field.
    extra_label: str = ""
    extra_placeholder: str = ""
    # Short line under the service name on the connections card.
    tagline: str = ""

    def cancelled(self) -> bool:
        """True once this refresh has been abandoned."""
        return self.cancel is not None and self.cancel.is_set()

    def configure(self, key: str, extra: str = "") -> None:
        """Supply credentials read from settings."""
        raise NotImplementedError

    def is_configured(self) -> bool:
        raise NotImplementedError

    def sources(self) -> list[Source]:
        """Where this service's login can live, most explicit first.

        Every provider returns the same shape, which is what lets one code
        path detect and render all of them.
        """
        return []

    def detect(self) -> Detection:
        """Find the best available login. Cheap: filesystem and env only."""
        return resolve(self.id, self.sources())

    def fetch(self, days: int, metric: str, want_history: bool) -> ProviderSnapshot:
        """Do the network/disk work. Called on a worker thread, never the GUI."""
        raise NotImplementedError
