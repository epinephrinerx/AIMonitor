"""OpenAI provider - Codex quota or platform usage via Admin Usage/Costs API.

Endpoints (all GET, Bearer auth with an **Admin** key, `sk-admin-...`):

  /v1/organization/usage/completions   token counts, groupable by model
  /v1/organization/costs               spend in USD, 1d buckets

What this can and cannot show
-----------------------------
Admin endpoints report API platform usage, not Codex quota. A Codex ChatGPT
login uses App Server for real quota percentages and daily total tokens.
API spend meters carry `percent=None` unless the user sets a monthly budget,
which is a local target, not a server-enforced limit.

A regular `sk-...` project key is rejected by these endpoints; the key must be
an Admin key created by an organization owner.
"""

from __future__ import annotations

import datetime as dt
import json
import ssl
import urllib.error
import urllib.parse
import urllib.request

from .. import formatting, codex_usage
from ..usage_log import DayBucket
from ..detection import OAUTH, EXPIRED, Source, bind, resolve
from .base import HistoryView, Meter, Provider, ProviderSnapshot, Stat
from .sources import OPENAI_SOURCES, openai_key

BASE_URL = "https://api.openai.com"
USAGE_PATH = "/v1/organization/usage/completions"
COSTS_PATH = "/v1/organization/costs"
TIMEOUT_SECONDS = 15
USER_AGENT = "AIUsageMonitor/2.0"

# The Costs endpoint only supports daily buckets.
MAX_BUCKETS = 180


class OpenAIProvider(Provider):
    id = "openai"
    display_name = "OpenAI"
    key_label = "Admin API key (optional; API spend)"
    key_placeholder = "sk-admin-…"
    extra_label = "Monthly budget (USD, optional)"
    extra_placeholder = "e.g. 50 — a local target, not an OpenAI limit"
    tagline = "Codex quota · optional API platform spend"
    setup_hint = (
        "Sign in to <b>Codex with ChatGPT</b> on this machine to see Codex "
        "quota percentages, reset times and available daily token totals. "
        "Requires Codex CLI or the Codex desktop app. The existing auth.json "
        "is read-only; open Codex to renew an expired login.<br><br>"
        "A Codex ChatGPT login takes priority over saved or environment keys. "
        "Your existing keys are kept. When no Codex ChatGPT login is found, "
        "an optional organization <b>Admin key</b> provides API platform spend. "
        "Regular project keys cannot read API spend."
    )

    def __init__(self) -> None:
        self._key = ""
        self._active_key = ""
        self._origin = ""
        self._budget = 0.0  # optional local monthly target, USD

    def configure(self, key: str, extra: str = "") -> None:
        self._key = (key or "").strip()
        try:
            self._budget = float(extra) if extra else 0.0
        except ValueError:
            self._budget = 0.0

    def sources(self) -> list[Source]:
        """The shared list, with `manual` bound to the key saved in settings."""
        saved = self._key
        return bind(
            OPENAI_SOURCES,
            "manual",
            (
                lambda: openai_key(saved, "saved in this app")
            )
            if saved
            else (lambda: None),
        )

    def is_configured(self) -> bool:
        """Configured means we can actually read usage, not merely signed in."""
        detected = self.detect()
        return detected.usable and detected.credential is not None

    def detect(self):
        # Existing installations often already have an Admin key. It must not
        # silently switch a quota monitor back to the API billing dashboard.
        # Keep expired Codex logins selected too, with an actionable error,
        # instead of changing the meaning of the displayed figures on expiry.
        sources = self.sources()
        codex = resolve(self.id, [s for s in sources if s.id == "codex_cli"])
        if codex.credential is not None and codex.credential.kind == OAUTH:
            return codex
        return resolve(self.id, sources)

    def _resolved_key(self) -> tuple[str, str]:
        """(key, where-it-came-from). Empty key means no usage-capable login."""
        detected = self.detect()
        if (detected.usable and detected.credential
                and detected.credential.kind != OAUTH and detected.credential.value):
            return detected.credential.value, detected.source_label
        return "", ""

    def fetch(self, days: int, metric: str, want_history: bool) -> ProviderSnapshot:
        detected = self.detect()
        if detected.credential and detected.credential.kind == OAUTH:
            return self._fetch_codex(detected, days, metric, want_history)
        key, origin = self._resolved_key()
        snapshot = ProviderSnapshot(
            provider_id=self.id,
            configured=bool(key),
            detection=detected,
            setup_hint=self.setup_hint,
            value_note="Actual API spend billed by OpenAI.",
        )
        if not key:
            if detected.state == "partial":
                snapshot.account = detected.account
                snapshot.setup_hint = detected.hint or self.setup_hint
            return snapshot
        self._active_key = key
        self._origin = origin

        now = dt.datetime.now(dt.timezone.utc)
        month_start = now.replace(
            day=1, hour=0, minute=0, second=0, microsecond=0
        )

        try:
            month_cost = self._total_cost(int(month_start.timestamp()))
            today_start = now.replace(hour=0, minute=0, second=0, microsecond=0)
            today_cost = self._total_cost(int(today_start.timestamp()))
        except _OpenAIError as exc:
            snapshot.error = str(exc)
            snapshot.unauthorized = exc.unauthorized
            return snapshot

        snapshot.fetched_at = now
        snapshot.account = (
            f"{detected.account} · {origin}" if detected.account
            else f"OpenAI organization (API platform) · {origin}"
        )

        if self._budget > 0:
            percent = min(100.0, month_cost / self._budget * 100.0)
            snapshot.meters.append(
                Meter(
                    key="month_budget",
                    title="Month to date",
                    subtitle=f"of your {formatting.money(self._budget)} target",
                    percent=percent,
                    severity=_severity(percent),
                    resets_at=_next_month(now),
                    detail=formatting.money(month_cost),
                )
            )
        else:
            snapshot.meters.append(
                Meter(
                    key="month_spend",
                    title="Month to date",
                    subtitle="API spend",
                    percent=None,
                    resets_at=_next_month(now),
                    detail=formatting.money(month_cost),
                )
            )
        snapshot.meters.append(
            Meter(
                key="today_spend",
                title="Today",
                subtitle="API spend",
                percent=None,
                detail=formatting.money(today_cost),
            )
        )

        if want_history:
            # The two meters above are already read and real. A failure
            # fetching the range behind them is a gap in the page, not the
            # service having failed - the same separation the Codex path
            # makes below, which this path was missed out of when
            # `history_error` was introduced.
            try:
                snapshot.history, totals = self._history(days, metric)
            except _OpenAIError as exc:
                snapshot.history_error = str(exc)
                return snapshot
            snapshot.stats = [
                Stat("Spend in range", formatting.money(totals["cost"])),
                Stat("Input tokens", formatting.compact(totals["input"])),
                Stat("Output tokens", formatting.compact(totals["output"])),
                Stat(
                    "Cached input",
                    formatting.compact(totals["cached"]),
                    "billed at the cached rate",
                ),
                Stat("Requests", formatting.compact(totals["requests"])),
            ]
        return snapshot

    def _fetch_codex(self, detected, days: int, metric: str, want_history: bool) -> ProviderSnapshot:
        snapshot = ProviderSnapshot(
            provider_id=self.id, configured=detected.usable or detected.state == EXPIRED,
            detection=detected, account=detected.account,
            setup_hint=detected.hint or self.setup_hint,
            value_note="Codex quota reported by OpenAI. Daily history contains total tokens "
            "only; output tokens, model/project breakdowns and API spend are unavailable.",
        )
        if not snapshot.configured:
            return snapshot
        try:
            limits, usage, history_error = codex_usage.fetch(
                detected.credential, want_history, self.cancel
            )
            snapshot.meters = codex_usage.meters(limits)
            snapshot.fetched_at = dt.datetime.now(dt.timezone.utc)
            snapshot.account = f"{detected.account} · Codex" if detected.account else "Codex (ChatGPT login)"
            # A history failure is not a provider failure. The quota windows
            # above came from the server on this same call; reporting them as
            # broken because the daily totals were unavailable took working
            # gauges off the screen.
            snapshot.history_error = history_error
            if not snapshot.meters:
                snapshot.error = "OpenAI returned no percentage quota windows for this account."
            if usage is not None:
                snapshot.history, snapshot.stats = codex_usage.history(usage, days, metric)
        except codex_usage.CodexError as exc:
            snapshot.error = str(exc)
            snapshot.unauthorized = exc.unauthorized
        return snapshot

    # -- endpoints --------------------------------------------------------

    def _get(self, path: str, params: dict) -> dict:
        # Every Admin API request funnels through here, so one check stops a
        # cancelled refresh before the next of the four - month cost, today
        # cost, usage history, cost history - each of which would otherwise
        # sit out its own 15-second timeout after the window had closed.
        if self.cancelled():
            raise _OpenAIError("Refresh cancelled.")
        query = urllib.parse.urlencode(params, doseq=True)
        request = urllib.request.Request(
            f"{BASE_URL}{path}?{query}",
            method="GET",
            headers={
                "Authorization": f"Bearer {self._active_key or self._key}",
                "Content-Type": "application/json",
                "User-Agent": USER_AGENT,
            },
        )
        try:
            with urllib.request.urlopen(
                request, timeout=TIMEOUT_SECONDS, context=ssl.create_default_context()
            ) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8", "replace")
            except OSError:
                pass
            detail = _detail(body)
            if exc.code in (401, 403):
                raise _OpenAIError(
                    "OpenAI rejected the key. The Usage API needs an "
                    "organization Admin key (sk-admin-…) with the Usage "
                    "Dashboard permission."
                    + (f" ({detail})" if detail else ""),
                    unauthorized=True,
                ) from exc
            if exc.code == 429:
                raise _OpenAIError("OpenAI is rate-limiting usage requests.") from exc
            raise _OpenAIError(
                f"OpenAI usage request failed (HTTP {exc.code})"
                + (f": {detail}" if detail else ".")
            ) from exc
        except urllib.error.URLError as exc:
            raise _OpenAIError(f"Could not reach api.openai.com: {exc.reason}") from exc
        except (TimeoutError, json.JSONDecodeError) as exc:
            raise _OpenAIError(f"Bad response from OpenAI: {exc}") from exc

    def _total_cost(self, start_time: int) -> float:
        payload = self._get(
            COSTS_PATH, {"start_time": start_time, "bucket_width": "1d", "limit": 31}
        )
        total = 0.0
        for bucket in payload.get("data") or []:
            for result in bucket.get("results") or []:
                amount = result.get("amount") or {}
                total += float(amount.get("value") or 0.0)
        return total

    def _history(self, days: int, metric: str) -> tuple[HistoryView, dict]:
        days = min(days, MAX_BUCKETS)
        today = dt.datetime.now().astimezone().date()
        start = today - dt.timedelta(days=days - 1)
        start_ts = int(
            dt.datetime.combine(start, dt.time.min).astimezone().timestamp()
        )

        usage = self._get(
            USAGE_PATH,
            {
                "start_time": start_ts,
                "bucket_width": "1d",
                "group_by": ["model"],
                "limit": days,
            },
        )
        costs = self._get(
            COSTS_PATH, {"start_time": start_ts, "bucket_width": "1d", "limit": days}
        )

        cost_by_day: dict[dt.date, float] = {}
        for bucket in costs.get("data") or []:
            day = _bucket_day(bucket)
            if day is None:
                continue
            for result in bucket.get("results") or []:
                amount = result.get("amount") or {}
                cost_by_day[day] = cost_by_day.get(day, 0.0) + float(
                    amount.get("value") or 0.0
                )

        tokens_by_day: dict[dt.date, dict[str, dict]] = {}
        totals = {"input": 0, "output": 0, "cached": 0, "requests": 0, "cost": 0.0}
        for bucket in usage.get("data") or []:
            day = _bucket_day(bucket)
            if day is None:
                continue
            for result in bucket.get("results") or []:
                model = result.get("model") or "unknown"
                cell = tokens_by_day.setdefault(day, {}).setdefault(
                    model, {"input": 0, "output": 0, "cached": 0, "requests": 0}
                )
                cell["input"] += int(result.get("input_tokens") or 0)
                cell["output"] += int(result.get("output_tokens") or 0)
                cell["cached"] += int(result.get("input_cached_tokens") or 0)
                cell["requests"] += int(result.get("num_model_requests") or 0)
                totals["input"] += int(result.get("input_tokens") or 0)
                totals["output"] += int(result.get("output_tokens") or 0)
                totals["cached"] += int(result.get("input_cached_tokens") or 0)
                totals["requests"] += int(result.get("num_model_requests") or 0)
        totals["cost"] = sum(cost_by_day.values())

        buckets: list[DayBucket] = []
        model_totals: dict[str, float] = {}
        for offset in range(days):
            day = start + dt.timedelta(days=offset)
            bucket = DayBucket(day)
            per_model = tokens_by_day.get(day, {})
            day_tokens = sum(
                cell["input"] + cell["output"] for cell in per_model.values()
            ) or 1
            for model, cell in per_model.items():
                if metric == "Output tokens":
                    value = float(cell["output"])
                elif metric == "Equivalent value":
                    # Costs are not grouped by model, so apportion the day's
                    # spend by that model's share of the day's tokens.
                    share = (cell["input"] + cell["output"]) / day_tokens
                    value = cost_by_day.get(day, 0.0) * share
                else:
                    value = float(cell["input"] + cell["output"])
                if value > 0:
                    bucket.per_model[model] = value
                    model_totals[model] = model_totals.get(model, 0.0) + value
            buckets.append(bucket)

        order = sorted(model_totals, key=lambda name: -model_totals[name])
        if len(order) > 8:
            kept, folded = order[:7], set(order[7:])
            for bucket in buckets:
                spill = sum(bucket.per_model.pop(name, 0.0) for name in folded)
                if spill:
                    bucket.per_model["Other"] = spill
            order = kept + ["Other"]

        history = HistoryView(
            buckets=buckets,
            series=order,
            by_model=sorted(model_totals.items(), key=lambda pair: -pair[1]),
            by_project=[],
            days=days,
            metric=metric,
            project_label="By project",
        )
        return history, totals


class _OpenAIError(Exception):
    def __init__(self, message: str, *, unauthorized: bool = False) -> None:
        super().__init__(message)
        self.unauthorized = unauthorized


def _detail(body: str) -> str:
    try:
        parsed = json.loads(body)
    except (json.JSONDecodeError, TypeError):
        return body.strip()[:160]
    error = parsed.get("error") if isinstance(parsed, dict) else None
    if isinstance(error, dict) and isinstance(error.get("message"), str):
        return error["message"]
    return ""


def _bucket_day(bucket: dict) -> dt.date | None:
    start = bucket.get("start_time")
    if not isinstance(start, (int, float)):
        return None
    return dt.datetime.fromtimestamp(start, dt.timezone.utc).astimezone().date()


def _next_month(now: dt.datetime) -> dt.datetime:
    year, month = (now.year + 1, 1) if now.month == 12 else (now.year, now.month + 1)
    return now.replace(
        year=year, month=month, day=1, hour=0, minute=0, second=0, microsecond=0
    )


def _severity(percent: float) -> str:
    if percent >= 95:
        return "critical"
    if percent >= 85:
        return "serious"
    if percent >= 70:
        return "warning"
    return "normal"
