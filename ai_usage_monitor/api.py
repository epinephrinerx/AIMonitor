"""Client for the Claude OAuth usage endpoints.

These are the same endpoints Claude Code's own `/usage` command reads, so the
numbers here are the authoritative server-side quota rather than an estimate
reconstructed from local logs. Only GET requests are made, and only with the
credentials already on this machine.
"""

from __future__ import annotations

import datetime as dt
import json
import ssl
import urllib.error
import urllib.request
from dataclasses import dataclass, field

from . import credentials

BASE_URL = "https://api.anthropic.com"
USAGE_PATH = "/api/oauth/usage"
PROFILE_PATH = "/api/oauth/profile"
OAUTH_BETA = "oauth-2025-04-20"
USER_AGENT = "AIUsageMonitor/2.0"
# Kept modest: closing the window has to wait out an in-flight request before
# the worker thread can be torn down safely.
TIMEOUT_SECONDS = 15


class ApiError(Exception):
    """A usage request failed."""

    def __init__(
        self,
        message: str,
        *,
        unauthorized: bool = False,
        rate_limited: bool = False,
    ) -> None:
        super().__init__(message)
        self.unauthorized = unauthorized
        self.rate_limited = rate_limited


def _error_detail(body: str) -> str:
    """Pull the human-readable message out of an API error body."""
    try:
        parsed = json.loads(body)
    except (json.JSONDecodeError, TypeError):
        return body.strip()[:200]
    error = parsed.get("error") if isinstance(parsed, dict) else None
    if isinstance(error, dict):
        message = error.get("message")
        if isinstance(message, str):
            return message
    return ""


# Legacy top-level blocks that duplicate an entry in `limits`, but carry the
# utilization as a float rather than a rounded integer percent.
_LEGACY_BLOCK_FOR_KIND = {
    "session": "five_hour",
    "weekly_all": "seven_day",
    "weekly_opus": "seven_day_opus",
    "weekly_sonnet": "seven_day_sonnet",
    "weekly_oauth_apps": "seven_day_oauth_apps",
}

_TITLES = {
    "session": ("Session", "5-hour window"),
    "weekly_all": ("Weekly", "All models"),
    "weekly_opus": ("Weekly", "Opus only"),
    "weekly_sonnet": ("Weekly", "Sonnet only"),
    "weekly_oauth_apps": ("Weekly", "API apps"),
}


@dataclass(frozen=True)
class Limit:
    """One quota window as reported by the server."""

    kind: str
    group: str
    title: str
    subtitle: str
    percent: float
    severity: str
    resets_at: dt.datetime | None
    is_active: bool
    locked_reason: str | None = None

    @property
    def resets_in(self) -> dt.timedelta | None:
        if self.resets_at is None:
            return None
        return self.resets_at - dt.datetime.now(dt.timezone.utc)


@dataclass(frozen=True)
class Spend:
    """Pay-as-you-go credit usage, when the account has it enabled."""

    enabled: bool
    used_minor: int
    currency: str
    exponent: int
    percent: float
    limit_minor: int | None

    @property
    def used(self) -> float:
        return self.used_minor / (10**self.exponent)


@dataclass(frozen=True)
class Account:
    full_name: str | None
    email: str | None
    organization: str | None
    plan: str | None
    rate_limit_tier: str | None


@dataclass(frozen=True)
class UsageSnapshot:
    limits: list[Limit]
    spend: Spend | None
    fetched_at: dt.datetime
    raw: dict = field(repr=False, default_factory=dict)

    def by_kind(self, kind: str) -> Limit | None:
        return next((limit for limit in self.limits if limit.kind == kind), None)


def _parse_timestamp(value: object) -> dt.datetime | None:
    if not isinstance(value, str) or not value:
        return None
    text = value.replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def _get(path: str, token: str, cancel=None) -> dict:
    """One request. `cancel` is a `threading.Event` the caller may hand in.

    Checked here rather than at the two call sites so a third endpoint added
    later is covered without anyone having to remember. Only stops the *next*
    request from starting - a socket already waiting is left to its timeout,
    which is the same bargain the other two providers make.
    """
    if cancel is not None and cancel.is_set():
        raise ApiError("Refresh cancelled.")
    request = urllib.request.Request(
        BASE_URL + path,
        method="GET",
        headers={
            "Authorization": f"Bearer {token}",
            "anthropic-beta": OAUTH_BETA,
            "Accept": "application/json",
            "User-Agent": USER_AGENT,
        },
    )
    context = ssl.create_default_context()
    try:
        with urllib.request.urlopen(
            request, timeout=TIMEOUT_SECONDS, context=context
        ) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = ""
        try:
            body = exc.read().decode("utf-8", "replace")
        except OSError:
            pass
        detail = _error_detail(body)

        if exc.code in (401, 403):
            raise ApiError(
                f"Claude rejected the stored access token (HTTP {exc.code}). "
                "Start Claude Code to refresh your login, then refresh here.",
                unauthorized=True,
            ) from exc
        if exc.code == 429:
            retry_after = exc.headers.get("retry-after") if exc.headers else None
            when = f" Try again in {retry_after}s." if retry_after else " Try again shortly."
            raise ApiError(
                "Anthropic is rate-limiting usage requests." + when
                + " Lower the refresh frequency if this keeps happening.",
                rate_limited=True,
            ) from exc
        suffix = f": {detail}" if detail else "."
        raise ApiError(f"Usage request failed (HTTP {exc.code}){suffix}") from exc
    except urllib.error.URLError as exc:
        raise ApiError(f"Could not reach api.anthropic.com: {exc.reason}") from exc
    except (TimeoutError, json.JSONDecodeError) as exc:
        raise ApiError(f"Bad response from {path}: {exc}") from exc


def _title_for(kind: str, scope: dict | None) -> tuple[str, str]:
    if kind in _TITLES:
        return _TITLES[kind]
    if kind == "weekly_scoped":
        model = (scope or {}).get("model") or {}
        name = model.get("display_name")
        surface = ((scope or {}).get("surface") or {}).get("display_name")
        detail = name or surface or "Scoped"
        return "Weekly", f"{detail} only"
    # Unknown kind from a newer server: render it rather than dropping it.
    pretty = kind.replace("_", " ").strip().capitalize()
    group_hint = "Weekly" if kind.startswith("weekly") else pretty
    return group_hint, pretty


def _limits_from_payload(payload: dict) -> list[Limit]:
    entries = payload.get("limits")
    limits: list[Limit] = []

    if isinstance(entries, list) and entries:
        for entry in entries:
            if not isinstance(entry, dict):
                continue
            kind = entry.get("kind") or "unknown"
            percent = entry.get("percent")
            legacy = payload.get(_LEGACY_BLOCK_FOR_KIND.get(kind, ""), None)
            locked = None
            if isinstance(legacy, dict):
                # The legacy block carries an unrounded utilization.
                if isinstance(legacy.get("utilization"), (int, float)):
                    percent = legacy["utilization"]
                locked = legacy.get("locked_reason")
            title, subtitle = _title_for(kind, entry.get("scope"))
            limits.append(
                Limit(
                    kind=kind,
                    group=entry.get("group") or kind,
                    title=title,
                    subtitle=subtitle,
                    percent=float(percent or 0.0),
                    severity=entry.get("severity") or "normal",
                    resets_at=_parse_timestamp(entry.get("resets_at")),
                    is_active=bool(entry.get("is_active")),
                    locked_reason=locked,
                )
            )
    else:
        # Older shape: only the named top-level blocks.
        for kind, block_name in _LEGACY_BLOCK_FOR_KIND.items():
            block = payload.get(block_name)
            if not isinstance(block, dict):
                continue
            title, subtitle = _title_for(kind, None)
            limits.append(
                Limit(
                    kind=kind,
                    group="session" if kind == "session" else "weekly",
                    title=title,
                    subtitle=subtitle,
                    percent=float(block.get("utilization") or 0.0),
                    severity="normal",
                    resets_at=_parse_timestamp(block.get("resets_at")),
                    is_active=False,
                    locked_reason=block.get("locked_reason"),
                )
            )

    limits.sort(key=_reading_order)
    return limits


def _reading_order(limit: Limit) -> tuple:
    """Order the gauges by what each window *is*, never by how full it is.

    They used to be sorted by descending usage after the session window, so
    "Weekly · All models" and a per-model window swapped places the moment one
    overtook the other - the same three gauges in a different order between one
    refresh and the next. A dashboard is read by position: you look at the
    second tile because that is where the weekly total lives, and a tile that
    moves when the number moves is the one thing a gauge must not do.

    The order is the order they matter in: the five-hour window that decides
    whether you can keep working now, then the whole account's week, then the
    per-model windows. Ties are broken by subtitle so the row is identical on
    every refresh, and an unrecognised kind from a newer server sorts last
    rather than displacing anything known.
    """
    if limit.kind == "session" or limit.group == "session":
        rank = 0
    elif limit.kind == "weekly_all":
        rank = 1
    elif limit.kind.startswith("weekly"):
        rank = 2
    else:
        rank = 3
    return (rank, limit.subtitle.lower(), limit.kind)


def _spend_from_payload(payload: dict) -> Spend | None:
    block = payload.get("spend")
    if not isinstance(block, dict):
        return None
    used = block.get("used") or {}
    limit = block.get("limit") or {}
    return Spend(
        enabled=bool(block.get("enabled")),
        used_minor=int(used.get("amount_minor") or 0),
        currency=used.get("currency") or "USD",
        exponent=int(used.get("exponent") or 2),
        percent=float(block.get("percent") or 0.0),
        limit_minor=limit.get("amount_minor") if isinstance(limit, dict) else None,
    )


def fetch_usage(creds: credentials.Credentials, cancel=None) -> UsageSnapshot:
    payload = _get(USAGE_PATH, creds.access_token, cancel)
    return UsageSnapshot(
        limits=_limits_from_payload(payload),
        spend=_spend_from_payload(payload),
        fetched_at=dt.datetime.now(dt.timezone.utc),
        raw=payload,
    )


def fetch_account(creds: credentials.Credentials, cancel=None) -> Account:
    payload = _get(PROFILE_PATH, creds.access_token, cancel)
    account = payload.get("account") or {}
    org = payload.get("organization") or {}
    plan = org.get("organization_type") or creds.subscription_type
    if isinstance(plan, str):
        plan = plan.replace("claude_", "").replace("_", " ").title()
    return Account(
        full_name=account.get("full_name"),
        email=account.get("email"),
        organization=org.get("name"),
        plan=plan,
        rate_limit_tier=org.get("rate_limit_tier") or creds.rate_limit_tier,
    )
