"""Unified sign-in detection for every AI service.

Each service is discovered the same way: a priority-ordered list of `Source`
probes is walked until one yields a credential. A probe looks in exactly one
place - a CLI's login file, an environment variable, or the key you saved in
this app - and every provider is described by the same list-of-probes shape, so
the connections page can render all of them from one code path.

Two policies apply uniformly, and both are deliberate:

* **Read-only.** We adopt another tool's login but never write to it and never
  refresh it. Claude Code, Codex and Gemini CLI each own their own token
  lifecycle, and a second process racing them can invalidate a live session.
* **Expired means "go run that CLI".** Rather than attempt a refresh with
  someone else's OAuth client credentials, an expired login is reported as such
  with the command that fixes it.

`state` uses one vocabulary across all services:

    connected  usable right now, and usage figures can be read
    partial    signed in, but this login cannot read usage (see `hint`)
    expired    found, but the token is stale
    missing    nothing found
"""

from __future__ import annotations

import base64
import binascii
import datetime as dt
import json
import os
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Callable

CONNECTED = "connected"
PARTIAL = "partial"
EXPIRED = "expired"
MISSING = "missing"

STATE_WORDS = {
    CONNECTED: "Connected",
    PARTIAL: "Limited",
    EXPIRED: "Expired",
    MISSING: "Not connected",
}

# Kinds of credential a probe can return.
OAUTH = "oauth"
API_KEY = "api_key"
SERVICE_ACCOUNT = "service_account"


@dataclass(frozen=True)
class Credential:
    """One usable credential plus where it came from."""

    kind: str
    value: str = field(default="", repr=False)  # never include secrets in repr
    account: str = ""
    expires_at: dt.datetime | None = None
    project: str = ""
    account_id: str = ""
    usage_capable: bool = True
    limited_reason: str = ""

    @property
    def expired(self) -> bool:
        if self.expires_at is None:
            return False
        return self.expires_at <= dt.datetime.now(dt.timezone.utc)


@dataclass(frozen=True)
class Source:
    """A single place to look for a login."""

    id: str
    label: str                 # "Claude Code login"
    probe: Callable[[], Credential | None]
    # Set when a credential from this source proves identity but cannot read
    # usage - the connections card explains why instead of implying failure.
    usage_capable: bool = True
    limited_reason: str = ""
    refresh_hint: str = ""     # command that renews this login


@dataclass
class Detection:
    """What we found for one service, in the vocabulary the UI renders."""

    provider_id: str
    state: str = MISSING
    credential: Credential | None = None
    source_id: str = ""
    source_label: str = ""
    account: str = ""
    hint: str = ""
    candidates: list[tuple[str, str]] = field(default_factory=list)

    @property
    def word(self) -> str:
        return STATE_WORDS.get(self.state, self.state.title())

    @property
    def usable(self) -> bool:
        return self.state == CONNECTED


def adopt(detections: dict, snapshots: dict) -> dict:
    """Fold the detection each fetch produced back into the page's map.

    Every provider re-detects on its way to fetching and reports what it found
    on the snapshot, so a refresh already knows whether a login has expired or
    been renewed. Merging that back here is what keeps the connections page as
    fresh as the figures; without it a card stays on whatever it said when the
    page was last opened by hand, and quietly goes on claiming "Connected"
    after the token behind it has expired.

    Snapshots that carry no detection - a provider that failed before it got
    that far - leave the previous entry alone rather than blanking a card.
    Mutates and returns `detections` so the caller keeps one map.
    """
    for provider_id, snapshot in snapshots.items():
        found = getattr(snapshot, "detection", None)
        if found is not None:
            detections[provider_id] = found
    return detections


def bind(sources: list[Source], source_id: str,
         probe: Callable[[], Credential | None]) -> list[Source]:
    """Return `sources` with one entry's probe swapped.

    Providers use this to attach the credential saved in settings to the
    shared `manual` entry, keeping every service's source list identical in
    shape.
    """
    return [
        replace(source, probe=probe) if source.id == source_id else source
        for source in sources
    ]


def resolve(provider_id: str, sources: list[Source]) -> Detection:
    """Walk the probes in order and report the first that yields something.

    A source that raises is skipped rather than aborting detection: a corrupt
    file left by one CLI must not stop us finding another service's login.
    """
    found = Detection(provider_id=provider_id)
    fallback: Detection | None = None

    for source in sources:
        try:
            credential = source.probe()
        except Exception:  # noqa: BLE001 - a bad file must not break detection
            continue
        if credential is None:
            continue

        found.candidates.append((source.id, source.label))
        detection = Detection(
            provider_id=provider_id,
            credential=credential,
            source_id=source.id,
            source_label=source.label,
            account=credential.account,
        )
        if credential.expired:
            detection.state = EXPIRED
            detection.hint = source.refresh_hint or "Sign in again to refresh."
        elif not source.usage_capable or not credential.usage_capable:
            detection.state = PARTIAL
            detection.hint = credential.limited_reason or source.limited_reason
        else:
            detection.state = CONNECTED

        if detection.state == CONNECTED:
            detection.candidates = found.candidates
            return detection
        if fallback is None:
            fallback = detection

    if fallback is not None:
        fallback.candidates = found.candidates
        return fallback
    return found


# -- shared helpers ------------------------------------------------------

def home() -> Path:
    return Path.home()


def read_json(path: Path) -> dict | None:
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8", errors="replace"))
    except (OSError, json.JSONDecodeError):
        return None
    return data if isinstance(data, dict) else None


def epoch(value: object) -> dt.datetime | None:
    """Accept seconds or milliseconds since the epoch."""
    if not isinstance(value, (int, float)) or value <= 0:
        return None
    seconds = value / 1000.0 if value > 10_000_000_000 else float(value)
    try:
        return dt.datetime.fromtimestamp(seconds, dt.timezone.utc)
    except (OverflowError, OSError, ValueError):
        return None


def jwt_claims(token: str) -> dict:
    """Decode a JWT payload for display only - no signature check.

    The payload is not a secret (it is base64, not encryption); we read it
    purely to show which account is signed in.
    """
    if not isinstance(token, str) or token.count(".") != 2:
        return {}
    part = token.split(".")[1]
    part += "=" * (-len(part) % 4)
    try:
        return json.loads(base64.urlsafe_b64decode(part).decode("utf-8", "replace"))
    except (binascii.Error, ValueError, UnicodeDecodeError):
        return {}


def account_from_claims(claims: dict) -> str:
    for key in ("email", "preferred_username", "name", "sub"):
        value = claims.get(key)
        if isinstance(value, str) and value:
            return value
    return ""


def env_first(*names: str) -> tuple[str, str]:
    """Return (value, which-variable) for the first env var that is set."""
    for name in names:
        value = os.environ.get(name, "").strip()
        if value:
            return value, name
    return "", ""
