"""Aggregate token usage from the local Claude Code transcripts.

The quota gauges come from the server; this module supplies the history the
server does not expose - what was spent, when, on which model, in which project.

Three details carry the design:

* **Aggregate on ingest.** Records are folded into per-day counters as they are
  read and the parsed message is then discarded. Retaining a `Message` object
  per API call would grow without bound on a heavy user's machine; the counter
  tables are bounded by (days retained x models) and (days retained x projects),
  which is a few kilobytes no matter how much history exists.
* **De-duplicate on the API message id.** Claude Code writes one JSONL entry per
  content block, each repeating the whole `usage` object for its message.
  Counting entries would roughly double every figure. The seen-set is global
  rather than per-file, because a resumed session replays earlier messages into
  a new transcript.
* **Read incrementally.** Each refresh seeks to where the last one stopped, in
  binary mode - a text-mode seek only accepts opaque cookies from `tell()`.
"""

from __future__ import annotations

import datetime as dt
import json
import os
from dataclasses import dataclass, field
from pathlib import Path

from . import credentials, pricing

# How much history to keep in memory. Beyond this, day buckets are dropped -
# the longest range the UI offers is 90 days.
RETAINED_DAYS = 400

# Ids are ~40 bytes each; this bounds the de-duplication set on a machine with
# a very long history. Oldest entries are dropped first, and a dropped id can
# only be re-counted if that same transcript is re-read from offset zero.
MAX_SEEN_IDS = 400_000


@dataclass
class Counters:
    """Folded totals for one (day, model) or (day, project) cell."""

    messages: int = 0
    input_tokens: int = 0
    output_tokens: int = 0
    cache_write: int = 0
    cache_read: int = 0
    cost_usd: float = 0.0

    @property
    def total_tokens(self) -> int:
        return (
            self.input_tokens + self.output_tokens + self.cache_write + self.cache_read
        )

    @property
    def cache_hit_rate(self) -> float:
        """Share of cacheable input tokens that were served from cache."""
        cacheable = self.cache_read + self.cache_write + self.input_tokens
        return self.cache_read / cacheable if cacheable else 0.0

    def add(
        self,
        *,
        input_tokens: int,
        output_tokens: int,
        cache_write: int,
        cache_read: int,
        cost_usd: float,
    ) -> None:
        self.messages += 1
        self.input_tokens += input_tokens
        self.output_tokens += output_tokens
        self.cache_write += cache_write
        self.cache_read += cache_read
        self.cost_usd += cost_usd

    def merge(self, other: "Counters") -> None:
        self.messages += other.messages
        self.input_tokens += other.input_tokens
        self.output_tokens += other.output_tokens
        self.cache_write += other.cache_write
        self.cache_read += other.cache_read
        self.cost_usd += other.cost_usd


# Metrics the charts can be drawn against.
METRICS = ("Total tokens", "Output tokens", "Equivalent value")


def metric_of(counters: Counters, metric: str) -> float:
    if metric == "Output tokens":
        return float(counters.output_tokens)
    if metric == "Equivalent value":
        return counters.cost_usd
    return float(counters.total_tokens)


@dataclass
class DayBucket:
    day: dt.date
    per_model: dict[str, float] = field(default_factory=dict)

    @property
    def total(self) -> float:
        return sum(self.per_model.values())


def projects_dir() -> Path:
    return credentials.config_dir() / "projects"


def token_count(value) -> int | None:
    """A token count from a transcript field, or None when it is not one.

    Missing is zero: transcripts omit fields that did not apply. Everything
    else has to prove itself, because these values are fed straight into
    arithmetic and a bad one used to raise out of the whole refresh.

    A bool is rejected even though Python calls it an int - `True` as a token
    count means the writer was confused, not that one token was used - and so
    is anything negative.
    """
    if value is None:
        return 0
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value if value >= 0 else None
    if isinstance(value, float):
        # Some writers emit 1.0 rather than 1. Accept it only when it is an
        # exact whole number; 1.5 tokens is not a thing.
        return int(value) if value.is_integer() and value >= 0 else None
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return 0
        try:
            number = int(text)
        except ValueError:
            return None
        return number if number >= 0 else None
    return None


def _project_from_dirname(name: str) -> str:
    """Best-effort project label when a record carries no `cwd`.

    The directory encoding is lossy - a hyphen inside a real folder name is
    indistinguishable from a separator - so this takes the trailing segment.
    """
    tail = name
    if len(name) > 3 and name[1:3] == "--":
        tail = name[3:]
    return tail.rsplit("-", 1)[-1] or name


class TranscriptStore:
    """Incrementally parsed, pre-aggregated view of the local transcripts."""

    def __init__(self, root: Path | None = None) -> None:
        self.root = root or projects_dir()
        self.by_day_model: dict[dt.date, dict[str, Counters]] = {}
        self.by_day_project: dict[dt.date, dict[str, Counters]] = {}
        self.all_totals = Counters()
        self._offsets: dict[Path, int] = {}
        self._seen_ids: dict[str, None] = {}  # insertion-ordered set
        self.files_scanned = 0
        self.last_error: str | None = None
        # Records that looked like usage but could not be read. Counted rather
        # than logged: a transcript line is the user's own conversation.
        self.malformed = 0

    # -- ingest -----------------------------------------------------------

    def refresh(self) -> int:
        """Read anything appended since the last call. Returns records added."""
        added = 0
        self.last_error = None
        if not self.root.exists():
            self.last_error = f"No transcripts directory at {self.root}"
            return 0

        try:
            paths = sorted(self.root.glob("*/*.jsonl"))
        except OSError as exc:
            self.last_error = f"Could not list {self.root}: {exc}"
            return 0

        self.files_scanned = len(paths)
        for path in paths:
            try:
                added += self._read_file(path)
            except OSError:
                # A transcript being written right now can fail a read; the next
                # refresh resumes from the same offset.
                continue

        if added:
            self._prune()
        return added

    def _read_file(self, path: Path) -> int:
        offset = self._offsets.get(path, 0)
        size = path.stat().st_size
        if size < offset:
            offset = 0  # rotated or rewritten
        if size == offset:
            return 0

        fallback_project = _project_from_dirname(path.parent.name)
        added = 0
        # Binary mode: a text-mode seek only accepts cookies from tell(), so
        # resuming from a byte offset has to happen below the decoder.
        with path.open("rb") as handle:
            handle.seek(offset, os.SEEK_SET)
            for raw in handle:
                if not raw.endswith(b"\n"):
                    break  # partial trailing line; resume from its start
                offset += len(raw)
                line = raw.strip()
                if not line or not line.startswith(b"{"):
                    continue
                # Cheap pre-filter: only assistant records carry usage, and
                # parsing every user/attachment record is pure waste.
                if b'"type":"assistant"' not in line and b'"type": "assistant"' not in line:
                    continue
                try:
                    record = json.loads(line.decode("utf-8", "replace"))
                except (json.JSONDecodeError, UnicodeDecodeError):
                    continue
                if not isinstance(record, dict):
                    continue
                try:
                    counted = self._ingest(record, fallback_project)
                except Exception:  # noqa: BLE001 - see below
                    # `_ingest` validates every field it uses, so this should
                    # be unreachable. It is here because the alternative to
                    # being wrong about that is an exception travelling out of
                    # the worker and blanking the whole Claude snapshot -
                    # losing the server's quota gauges, which have nothing to
                    # do with the local transcripts, over one bad line.
                    self.malformed += 1
                    continue
                if counted:
                    added += 1

        self._offsets[path] = offset
        return added

    def _ingest(self, record: dict, fallback_project: str) -> bool:
        """Fold one record into the counters. Returns True if it counted.

        Nothing is remembered about a record until it has fully parsed. The
        de-duplication id used to be recorded first, which had two costs: a
        record with an unparseable token count raised out of the whole refresh
        and took the quota gauges down with the history, and the id it had
        already claimed meant the same record could never be counted later,
        even once a corrected copy arrived.
        """
        if record.get("type") != "assistant":
            return False
        message = record.get("message")
        if not isinstance(message, dict):
            return False
        usage = message.get("usage")
        if not isinstance(usage, dict):
            return False

        # Cheap exit for a record already counted. The id is only *recorded*
        # further down, once this record has proved it can be read.
        key = message.get("id") or record.get("requestId")
        if key and key in self._seen_ids:
            return False

        timestamp = record.get("timestamp")
        if not isinstance(timestamp, str):
            self.malformed += 1
            return False
        try:
            when = dt.datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        except ValueError:
            self.malformed += 1
            return False
        if when.tzinfo is None:
            when = when.replace(tzinfo=dt.timezone.utc)
        day = when.astimezone().date()

        cache_creation = usage.get("cache_creation")
        if isinstance(cache_creation, dict):
            write_5m = token_count(cache_creation.get("ephemeral_5m_input_tokens"))
            write_1h = token_count(cache_creation.get("ephemeral_1h_input_tokens"))
        else:
            # Older transcripts report only the aggregate; treat it as 5-minute.
            write_5m = token_count(usage.get("cache_creation_input_tokens"))
            write_1h = 0

        input_tokens = token_count(usage.get("input_tokens"))
        output_tokens = token_count(usage.get("output_tokens"))
        cache_read = token_count(usage.get("cache_read_input_tokens"))
        if None in (write_5m, write_1h, input_tokens, output_tokens, cache_read):
            self.malformed += 1
            return False

        # Past this point the record is known good, so claiming its id cannot
        # strand a record that would otherwise have counted.
        if key:
            self._seen_ids[key] = None

        model = message.get("model") or ""
        cost = pricing.cost(
            model,
            input_tokens=input_tokens,
            output_tokens=output_tokens,
            cache_write_5m=write_5m,
            cache_write_1h=write_1h,
            cache_read=cache_read,
        )

        cwd = record.get("cwd")
        project = Path(cwd).name if isinstance(cwd, str) and cwd else fallback_project

        fields = {
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "cache_write": write_5m + write_1h,
            "cache_read": cache_read,
            "cost_usd": cost,
        }
        model_label = pricing.display_name(model)
        self.by_day_model.setdefault(day, {}).setdefault(
            model_label, Counters()
        ).add(**fields)
        self.by_day_project.setdefault(day, {}).setdefault(
            project or "(unknown)", Counters()
        ).add(**fields)
        self.all_totals.add(**fields)
        return True

    def _prune(self) -> None:
        cutoff = dt.datetime.now().astimezone().date() - dt.timedelta(days=RETAINED_DAYS)
        for table in (self.by_day_model, self.by_day_project):
            for day in [d for d in table if d < cutoff]:
                del table[day]

        overflow = len(self._seen_ids) - MAX_SEEN_IDS
        if overflow > 0:
            for key in list(self._seen_ids)[:overflow]:
                del self._seen_ids[key]

    # -- queries ----------------------------------------------------------

    def _days_in_range(self, days: int) -> list[dt.date]:
        today = dt.datetime.now().astimezone().date()
        return [today - dt.timedelta(days=offset) for offset in range(days - 1, -1, -1)]

    def window_totals(self, days: int) -> Counters:
        totals = Counters()
        for day in self._days_in_range(days):
            for counters in self.by_day_model.get(day, {}).values():
                totals.merge(counters)
        return totals

    def daily(self, days: int, metric: str) -> tuple[list[DayBucket], list[str]]:
        """Per-day buckets plus the model order (largest contributor first).

        Models past the eighth fold into "Other" so the categorical palette is
        never cycled.
        """
        totals_by_model: dict[str, float] = {}
        buckets: list[DayBucket] = []

        for day in self._days_in_range(days):
            bucket = DayBucket(day)
            for model, counters in self.by_day_model.get(day, {}).items():
                value = metric_of(counters, metric)
                if value > 0:
                    bucket.per_model[model] = value
                    totals_by_model[model] = totals_by_model.get(model, 0.0) + value
            buckets.append(bucket)

        order = sorted(totals_by_model, key=lambda name: -totals_by_model[name])
        if len(order) > 8:
            kept, folded = order[:7], set(order[7:])
            for bucket in buckets:
                spill = sum(bucket.per_model.pop(name, 0.0) for name in folded)
                if spill:
                    bucket.per_model["Other"] = spill
            order = kept + ["Other"]
        return buckets, order

    def breakdown(self, key: str, metric: str, days: int) -> list[tuple[str, float]]:
        """Descending (label, value) pairs grouped by 'model' or 'project'."""
        table = self.by_day_model if key == "model" else self.by_day_project
        totals: dict[str, float] = {}
        for day in self._days_in_range(days):
            for label, counters in table.get(day, {}).items():
                value = metric_of(counters, metric)
                if value > 0:
                    totals[label] = totals.get(label, 0.0) + value
        return sorted(totals.items(), key=lambda pair: -pair[1])
