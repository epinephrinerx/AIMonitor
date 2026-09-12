"""Read-only access to the Claude Code OAuth credentials on this machine.

Deliberately read-only: Claude Code owns the refresh cycle for these tokens, and
a second process writing the file can invalidate the user's login. When the
token has expired we say so and ask the user to start Claude Code, rather than
attempting a refresh ourselves.

Where the login lives is platform-specific. On Windows it is a JSON file under
the Claude config directory; on macOS Claude Code files the same JSON in the
login Keychain instead, and the file is usually absent. Both are tried, file
first, so an unusual setup - a `CLAUDE_CONFIG_DIR` on a Mac, say - still works.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

MACOS = sys.platform == "darwin"

#: What Claude Code calls its Keychain item.
KEYCHAIN_SERVICE = "Claude Code-credentials"


class CredentialsError(Exception):
    """Credentials are missing, unreadable, or expired."""


@dataclass(frozen=True)
class Credentials:
    access_token: str
    expires_at: float | None  # epoch seconds, or None if unknown
    subscription_type: str | None
    rate_limit_tier: str | None
    source: str  # where it was read from, for display in errors

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


# -- macOS Keychain ---------------------------------------------------------


def _keychain_item_exists() -> bool:
    """Whether Claude Code has a Keychain item, without reading the secret.

    An attribute-only query does not touch the item's data, so it never raises
    the "allow access?" prompt. That matters because this is called to decide
    whether to *show* the Claude card, on the UI thread - a prompt there would
    freeze the window behind it.
    """
    if not MACOS:
        return False
    try:
        done = subprocess.run(
            ["/usr/bin/security", "find-generic-password", "-s", KEYCHAIN_SERVICE],
            capture_output=True, text=True, timeout=15,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return done.returncode == 0


def _keychain_json() -> str:
    """The stored credential JSON, or '' if there is none to read.

    The first read shows a Keychain prompt, because the item's ACL names Claude
    Code and not this app. Answering "Always Allow" is a one-time cost; the
    generous timeout is there so a user who takes a moment over that dialog
    does not have the read killed underneath them.
    """
    if not MACOS:
        return ""
    try:
        done = subprocess.run(
            ["/usr/bin/security", "find-generic-password", "-s", KEYCHAIN_SERVICE, "-w"],
            capture_output=True, text=True, timeout=120,
        )
    except (OSError, subprocess.SubprocessError):
        return ""
    return done.stdout.strip() if done.returncode == 0 else ""


def available() -> bool:
    """Whether a Claude Code login exists here at all. Never prompts."""
    return credentials_path().exists() or _keychain_item_exists()


def where() -> str:
    """A human description of where the login is expected to be."""
    if MACOS and not credentials_path().exists():
        return f"the login Keychain (service \"{KEYCHAIN_SERVICE}\")"
    return str(credentials_path())


def probe() -> tuple[dict | None, bool]:
    """What detection can learn without prompting.

    Returns the parsed document if it could be read for free, plus whether a
    Keychain login exists. Detection runs on the GUI thread, so it must never
    reach for the Keychain's *data* - that is what raises the consent dialog.
    Presence is enough for the connections card to say "connected"; the token
    itself is read later, on the worker thread, by `load`.
    """
    path = credentials_path()
    if path.exists():
        try:
            data = json.loads(path.read_text(encoding="utf-8", errors="replace"))
        except (OSError, json.JSONDecodeError):
            data = None
        if isinstance(data, dict):
            return data, False
    return None, _keychain_item_exists()


def load_raw() -> tuple[dict, str] | None:
    """The raw credential document and where it came from, or None.

    Shared with the detection layer, so the connections page and the fetch path
    agree about whether a login exists instead of probing differently.
    """
    path = credentials_path()
    if path.exists():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise CredentialsError(f"Could not read {path}: {exc}") from exc
        return (data, str(path)) if isinstance(data, dict) else None

    blob = _keychain_json()
    if not blob:
        return None
    try:
        data = json.loads(blob)
    except json.JSONDecodeError as exc:
        raise CredentialsError(
            f"The Claude Code Keychain item is not valid JSON: {exc}"
        ) from exc
    return (data, where()) if isinstance(data, dict) else None


def _as_epoch_seconds(value: object) -> float | None:
    """Accept either seconds or milliseconds since the epoch."""
    if not isinstance(value, (int, float)):
        return None
    # Anything past ~year 2286 in seconds is really milliseconds.
    return float(value) / 1000.0 if value > 10_000_000_000 else float(value)


def load() -> Credentials:
    found = load_raw()
    if found is None:
        raise CredentialsError(
            f"No Claude Code credentials found in {where()}.\n"
            "Sign in by running `claude` in a terminal, then refresh."
        )
    raw, source = found

    oauth = raw.get("claudeAiOauth") or {}
    token = oauth.get("accessToken")
    if not token:
        raise CredentialsError(
            f"{source} has no OAuth access token. Run `claude` to sign in."
        )

    return Credentials(
        access_token=token,
        expires_at=_as_epoch_seconds(oauth.get("expiresAt")),
        subscription_type=oauth.get("subscriptionType"),
        rate_limit_tier=oauth.get("rateLimitTier"),
        source=source,
    )
