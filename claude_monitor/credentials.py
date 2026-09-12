"""Read-only access to the Claude Code OAuth credentials on this machine.

Deliberately read-only: Claude Code owns the refresh cycle for these tokens, and
a second process writing the file can invalidate the user's login. When the
token has expired we say so and ask the user to start Claude Code, rather than
attempting a refresh ourselves.
"""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass
from pathlib import Path


class CredentialsError(Exception):
    """Credentials are missing, unreadable, or expired."""


@dataclass(frozen=True)
class Credentials:
    access_token: str
    expires_at: float | None  # epoch seconds, or None if unknown
    subscription_type: str | None
    rate_limit_tier: str | None
    source: Path

    @property
    def expired(self) -> bool:
        return self.expires_at is not None and self.expires_at <= time.time()

    @property
    def seconds_remaining(self) -> float | None:
        if self.expires_at is None:
            return None
        return self.expires_at - time.time()


def config_dir() -> Path:
    """The directory Claude Code keeps its state in."""
    override = os.environ.get("CLAUDE_CONFIG_DIR")
    if override:
        return Path(override)
    return Path.home() / ".claude"


def credentials_path() -> Path:
    return config_dir() / ".credentials.json"


def _as_epoch_seconds(value: object) -> float | None:
    """Accept either seconds or milliseconds since the epoch."""
    if not isinstance(value, (int, float)):
        return None
    # Anything past ~year 2286 in seconds is really milliseconds.
    return float(value) / 1000.0 if value > 10_000_000_000 else float(value)


def load() -> Credentials:
    path = credentials_path()
    if not path.exists():
        raise CredentialsError(
            f"No Claude Code credentials found at {path}.\n"
            "Sign in by running `claude` in a terminal, then refresh."
        )
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise CredentialsError(f"Could not read {path}: {exc}") from exc

    oauth = raw.get("claudeAiOauth") or {}
    token = oauth.get("accessToken")
    if not token:
        raise CredentialsError(
            f"{path} has no OAuth access token. Run `claude` to sign in."
        )

    return Credentials(
        access_token=token,
        expires_at=_as_epoch_seconds(oauth.get("expiresAt")),
        subscription_type=oauth.get("subscriptionType"),
        rate_limit_tier=oauth.get("rateLimitTier"),
        source=path,
    )
