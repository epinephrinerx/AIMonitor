"""Claude provider - server-side quota plus local transcript history.

This is the only provider with true quota percentages: Claude Code ships an
OAuth usage endpoint that reports utilisation against the plan's real limits.
Credentials are the ones Claude Code already stored, read-only.
"""

from __future__ import annotations


from .. import api, credentials, formatting, usage_log
from ..detection import Source
from .base import HistoryView, Meter, Provider, ProviderSnapshot, Stat
from .sources import CLAUDE_SOURCES


class ClaudeProvider(Provider):
    id = "claude"
    display_name = "Claude"
    needs_key = False
    tagline = "Claude Code · full quota"
    setup_hint = (
        "Sign in to Claude Code on this machine (run `claude` in a terminal). "
        "This app reads that existing login read-only and never writes to it."
    )

    def __init__(self) -> None:
        self._store = usage_log.TranscriptStore()
        self._account: api.Account | None = None

    def configure(self, key: str = "", extra: str = "") -> None:
        return  # credentials come from Claude Code, not from settings

    def sources(self) -> list[Source]:
        return CLAUDE_SOURCES

    def is_configured(self) -> bool:
        return credentials.available()

    def fetch(self, days: int, metric: str, want_history: bool) -> ProviderSnapshot:
        snapshot = ProviderSnapshot(
            provider_id=self.id,
            configured=True,
            detection=self.detect(),
            setup_hint=self.setup_hint,
            value_note="Equivalent API value at list price - a subscription is not billed per token.",
        )

        creds = None
        try:
            creds = credentials.load()
        except credentials.CredentialsError as exc:
            snapshot.configured = False
            snapshot.error = str(exc)
            snapshot.unauthorized = True

        if creds is not None:
            if creds.expired:
                snapshot.error = (
                    "The stored access token has expired. Start Claude Code to "
                    "refresh your login, then refresh here."
                )
                snapshot.unauthorized = True
            else:
                try:
                    usage = api.fetch_usage(creds)
                    snapshot.meters = [_to_meter(limit) for limit in usage.limits]
                    snapshot.fetched_at = usage.fetched_at
                    if usage.spend and usage.spend.enabled:
                        snapshot.stats.append(
                            Stat(
                                "Extra usage credits",
                                formatting.money(usage.spend.used),
                                "billed separately from the plan",
                            )
                        )
                except api.ApiError as exc:
                    snapshot.error = str(exc)
                    snapshot.unauthorized = exc.unauthorized

                if self._account is None and snapshot.meters:
                    try:
                        self._account = api.fetch_account(creds)
                    except api.ApiError:
                        self._account = None

        if self._account is not None:
            plan = self._account.plan or "-"
            tier = (self._account.rate_limit_tier or "").replace("default_claude_", "")
            tier = tier.replace("_", " ")
            who = self._account.full_name or self._account.email or ""
            detail = f"{plan} plan" + (f" · {tier}" if tier else "")
            snapshot.account = f"{who} — {detail}" if who else detail

        if want_history:
            snapshot.history = self._history(days, metric)
            totals = self._store.window_totals(days)
            snapshot.stats = [
                Stat(
                    "Tokens in range",
                    formatting.compact(totals.total_tokens),
                    f"{formatting.compact(self._store.all_totals.total_tokens)} all time",
                ),
                Stat("Output tokens", formatting.compact(totals.output_tokens)),
                Stat(
                    "Equivalent API value",
                    formatting.money(totals.cost_usd),
                    "at API list price",
                ),
                Stat(
                    "Served from cache",
                    f"{totals.cache_hit_rate * 100:.0f}%",
                    f"{formatting.compact(totals.cache_read)} cached reads",
                ),
                Stat("Assistant messages", formatting.compact(totals.messages)),
            ] + snapshot.stats

        return snapshot

    def _history(self, days: int, metric: str) -> HistoryView:
        self._store.refresh()
        buckets, series = self._store.daily(days, metric)
        return HistoryView(
            buckets=buckets,
            series=series,
            by_model=self._store.breakdown("model", metric, days),
            by_project=self._store.breakdown("project", metric, days),
            days=days,
            metric=metric,
            project_label="By project",
        )

    def release_history(self) -> None:
        """Widget mode does not draw charts; nothing to hold on to."""
        return


def _to_meter(limit: api.Limit) -> Meter:
    return Meter(
        key=limit.kind,
        title=limit.title,
        subtitle=limit.subtitle,
        percent=limit.percent,
        severity=limit.severity,
        resets_at=limit.resets_at,
        locked_reason=limit.locked_reason,
    )
