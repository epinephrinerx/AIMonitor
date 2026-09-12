"""Output-token throughput, summarised per interval.

Deliberately **summarised, not sampled**: each point is the average output
tokens/sec over one whole interval (3 minutes by default). The tracker diffs
cumulative output tokens between refreshes and closes a bucket once the
interval has elapsed, so there is no second clock and no per-second work - the
existing refresh cycle is the only thing driving it.

Averaging also makes the trace readable. Per-response generation is violently
bursty; a one-second sample is mostly spikes and zeros, while an interval mean
shows how hard you were actually working over that stretch.

Two honest limits:

* The figure comes from local Claude Code transcripts, so it counts CLI
  sessions only - the same blind spot as the daily chart.
* A bucket shorter than the interval is never emitted, so the first point
  appears one interval after the app starts.
"""

from __future__ import annotations

import datetime as dt
from collections import deque
from dataclasses import dataclass

# Selectable summary intervals. 3 minutes is the default and is labelled as
# such in the UI.
INTERVAL_OPTIONS = [
    ("1 min", 60),
    ("3 min · default", 180),
    ("5 min", 300),
    ("10 min", 600),
    ("15 min", 900),
]
DEFAULT_INTERVAL = 180

# Enough points to fill the plot at any interval; 60 x 15 min is 15 hours.
MAX_SAMPLES = 60


@dataclass(frozen=True)
class Sample:
    """One closed interval."""

    at: dt.datetime  # when the interval ended (UTC)
    tokens_per_second: float
    tokens: int
    seconds: float


class ThroughputTracker:
    """Folds cumulative output-token counts into per-interval averages."""

    def __init__(self, interval_seconds: int = DEFAULT_INTERVAL) -> None:
        self.interval_seconds = max(30, int(interval_seconds))
        self.samples: deque[Sample] = deque(maxlen=MAX_SAMPLES)
        self.peak = 0.0
        self._last_total: int | None = None
        self._bucket_start: dt.datetime | None = None
        self._bucket_tokens = 0

    # -- configuration ----------------------------------------------------

    def set_interval(self, seconds: int) -> None:
        """Change the summary interval, discarding history at the old scale.

        Keeping old points would silently mix 1-minute and 15-minute averages
        on one axis, which is a lie about what the trace shows.
        """
        seconds = max(30, int(seconds))
        if seconds == self.interval_seconds:
            return
        self.interval_seconds = seconds
        self.samples.clear()
        self.peak = 0.0
        self._bucket_start = None
        self._bucket_tokens = 0
        # `_last_total` is kept: the token counter is continuous regardless of
        # how it is bucketed, and resetting it would drop a real delta.

    # -- ingest -----------------------------------------------------------

    def observe(self, cumulative_output_tokens: int, now: dt.datetime | None = None) -> None:
        """Record the running output-token total. Call once per refresh."""
        now = now or dt.datetime.now(dt.timezone.utc)

        if self._last_total is None or self._bucket_start is None:
            # First observation is a baseline only - there is no earlier total
            # to diff against, and counting it would attribute the whole of
            # history to one interval.
            self._last_total = cumulative_output_tokens
            self._bucket_start = now
            return

        # A shrinking total means transcripts were pruned; re-baseline instead
        # of recording a negative rate.
        delta = cumulative_output_tokens - self._last_total
        self._last_total = cumulative_output_tokens
        if delta > 0:
            self._bucket_tokens += delta

        elapsed = (now - self._bucket_start).total_seconds()
        if elapsed < self.interval_seconds:
            return

        # The interval is a MINIMUM bucket length, not a fixed grid. If the app
        # sat in widget mode (which parses no transcripts) or the machine
        # slept, the bucket simply runs long and the rate is averaged over the
        # real elapsed time. Splitting a gap into fixed-width buckets would
        # have to invent when the tokens were produced.
        self._append(now, self._bucket_tokens, elapsed)
        self._bucket_start = now
        self._bucket_tokens = 0

    def _append(self, end: dt.datetime, tokens: int, seconds: float) -> None:
        rate = tokens / seconds if seconds > 0 else 0.0
        self.samples.append(
            Sample(at=end, tokens_per_second=rate, tokens=int(tokens), seconds=seconds)
        )
        self.peak = max(self.peak, rate)

    # -- queries ----------------------------------------------------------

    @property
    def current(self) -> float:
        """Rate of the most recently closed interval."""
        return self.samples[-1].tokens_per_second if self.samples else 0.0

    @property
    def mean(self) -> float:
        """Mean across every interval held."""
        if not self.samples:
            return 0.0
        return sum(s.tokens_per_second for s in self.samples) / len(self.samples)

    @property
    def pending_tokens(self) -> int:
        """Output tokens accumulated in the interval still open."""
        return self._bucket_tokens

    @property
    def span_seconds(self) -> int:
        return len(self.samples) * self.interval_seconds

    def series(self) -> list[Sample]:
        return list(self.samples)
