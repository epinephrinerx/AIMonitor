"""Where each AI service's login lives on this machine.

Every entry is the same `Source` shape, so detection, the connections page and
the connect dialog treat all three services identically. The only thing that
differs per service is the list itself.

Priority within a list runs most-explicit first: a key you typed into this app
beats an environment variable, which beats a CLI's own login.
"""

from __future__ import annotations

from pathlib import Path

from ..detection import (
    API_KEY,
    OAUTH,
    SERVICE_ACCOUNT,
    Credential,
    Source,
    account_from_claims,
    env_first,
    epoch,
    home,
    jwt_claims,
    read_json,
)

# Anthropic honours CLAUDE_CONFIG_DIR; the others have no equivalent.
from .. import credentials as claude_credentials


# -- Claude ---------------------------------------------------------------

def _claude_code_login() -> Credential | None:
    data, keychain = claude_credentials.probe()
    if data is None:
        # macOS keeps this login in the Keychain, and reading the token needs
        # the user's consent - a dialog this probe must not raise from the GUI
        # thread. Presence is all the card needs; the worker reads the token,
        # and reports an expired one when the fetch comes back.
        return Credential(kind=OAUTH, account="Keychain login") if keychain else None
    oauth = data.get("claudeAiOauth") or {}
    token = oauth.get("accessToken")
    if not token:
        return None
    plan = oauth.get("subscriptionType") or ""
    return Credential(
        kind=OAUTH,
        value=token,
        account=f"{plan.title()} plan" if plan else "",
        expires_at=epoch(oauth.get("expiresAt")),
    )


CLAUDE_SOURCES = [
    Source(
        id="claude_code",
        label="Claude Code login",
        probe=_claude_code_login,
        refresh_hint="Run `claude` in a terminal to refresh the login.",
    ),
]


# -- OpenAI ---------------------------------------------------------------

def _openai_env() -> Credential | None:
    value, name = env_first("OPENAI_ADMIN_KEY", "OPENAI_API_KEY")
    if not value:
        return None
    return Credential(kind=API_KEY, value=value, account=f"from ${name}")


def _codex_login() -> Credential | None:
    """Codex CLI's login (`~/.codex/auth.json`).

    Codex can be signed in two ways. `auth_mode: "api_key"` stores a usable
    key. `auth_mode: "chatgpt"` stores ChatGPT OAuth tokens, which prove who
    you are but are not accepted by the Usage/Costs endpoints - that is why
    this source is marked not usage-capable.
    """
    data = read_json(home() / ".codex" / "auth.json")
    if not data:
        return None

    tokens = data.get("tokens") or {}
    account = account_from_claims(jwt_claims(tokens.get("id_token", "")))
    if not account:
        account = tokens.get("account_id", "") or ""

    key = data.get("OPENAI_API_KEY")
    if isinstance(key, str) and key.startswith("sk-"):
        return Credential(kind=API_KEY, value=key, account=account)

    if tokens.get("access_token"):
        # Identity only - deliberately carries no value for the usage client.
        return Credential(kind=OAUTH, value="", account=account)
    return None


OPENAI_SOURCES = [
    Source(id="manual", label="Admin key saved in this app", probe=lambda: None),
    Source(id="env", label="Environment variable", probe=_openai_env),
    Source(
        id="codex_cli",
        label="Codex CLI login (ChatGPT)",
        probe=_codex_login,
        usage_capable=False,
        limited_reason=(
            "Signed in to Codex with a ChatGPT account. OpenAI publishes no "
            "usage API for ChatGPT subscriptions, so spend and token figures "
            "need an organization Admin key (sk-admin-…)."
        ),
        refresh_hint="Run `codex` and sign in again.",
    ),
]


# -- Gemini ---------------------------------------------------------------

def _gemini_service_account_env() -> Credential | None:
    value, _ = env_first("GOOGLE_APPLICATION_CREDENTIALS")
    if not value or not Path(value).is_file():
        return None
    data = read_json(Path(value)) or {}
    return Credential(
        kind=SERVICE_ACCOUNT,
        value=value,
        account=data.get("client_email", ""),
        project=data.get("project_id", ""),
    )


def _gemini_cli_login() -> Credential | None:
    """Gemini CLI's Google OAuth login (`~/.gemini/oauth_creds.json`).

    Usable for Cloud Monitoring only when the token carries the
    `cloud-platform` scope, which the Gemini CLI does request. Without it the
    token can prove identity but not read metrics, so we report it as found
    and let the provider explain.
    """
    root = home() / ".gemini"
    data = read_json(root / "oauth_creds.json")
    if not data or not data.get("access_token"):
        return None

    scopes = (data.get("scope") or "").split()
    if "https://www.googleapis.com/auth/cloud-platform" not in scopes:
        return None

    account = ""
    accounts = read_json(root / "google_accounts.json") or {}
    if isinstance(accounts.get("active"), str):
        account = accounts["active"]
    if not account:
        account = account_from_claims(jwt_claims(data.get("id_token", "")))

    return Credential(
        kind=OAUTH,
        value=data["access_token"],
        account=account,
        expires_at=epoch(data.get("expiry_date")),
    )


def _gcloud_adc() -> Credential | None:
    for candidate in (
        home() / "AppData" / "Roaming" / "gcloud" / "application_default_credentials.json",
        home() / ".config" / "gcloud" / "application_default_credentials.json",
    ):
        data = read_json(candidate)
        if not data:
            continue
        if data.get("type") == "service_account":
            return Credential(
                kind=SERVICE_ACCOUNT,
                value=str(candidate),
                account=data.get("client_email", ""),
                project=data.get("project_id", ""),
            )
    return None


GEMINI_SOURCES = [
    Source(id="manual", label="Service account saved in this app", probe=lambda: None),
    Source(id="env", label="GOOGLE_APPLICATION_CREDENTIALS", probe=_gemini_service_account_env),
    Source(
        id="gemini_cli",
        label="Gemini CLI login",
        probe=_gemini_cli_login,
        refresh_hint="Run `gemini` and sign in again to refresh the token.",
    ),
    Source(id="gcloud_adc", label="gcloud application-default credentials", probe=_gcloud_adc),
]


def describe_all() -> dict[str, list[Source]]:
    return {
        "claude": CLAUDE_SOURCES,
        "openai": OPENAI_SOURCES,
        "gemini": GEMINI_SOURCES,
    }
