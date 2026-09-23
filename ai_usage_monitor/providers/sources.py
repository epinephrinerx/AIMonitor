"""Where each AI service's login lives on this machine.

Every entry is the same `Source` shape, so detection, the connections page and
the connect dialog treat all three services identically. The only thing that
differs per service is the list itself.

Priority within a list runs most-explicit first: a key you typed into this app
beats an environment variable, which beats a CLI's own login.
"""

from __future__ import annotations

from pathlib import Path
import os

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

# Honour each CLI's configuration directory without changing its files.
from .. import credentials as claude_credentials


# -- Claude ---------------------------------------------------------------

def _claude_code_login() -> Credential | None:
    data = read_json(claude_credentials.credentials_path())
    if not data:
        return None
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
    return openai_key(value, f"from ${name}")


def openai_key(value: str, account: str = "") -> Credential:
    return Credential(
        kind=API_KEY, value=value, account=account,
        usage_capable=value.startswith("sk-admin-"),
        limited_reason="API spend requires an organization Admin key (sk-admin-…). "
        "Sign in to Codex with ChatGPT to monitor Codex quota instead.",
    )


def codex_home() -> Path:
    value = os.environ.get("CODEX_HOME", "").strip()
    return Path(value).expanduser() if value else home() / ".codex"


def _codex_login() -> Credential | None:
    """Codex CLI's login (`~/.codex/auth.json`).

    ChatGPT OAuth is used only for Codex quota via App Server. Admin keys
    are used only for the separate organization Usage/Costs endpoints.
    """
    data = read_json(codex_home() / "auth.json")
    if not data:
        return None

    tokens = data.get("tokens") or {}
    if not isinstance(tokens, dict):
        return None
    claims = jwt_claims(tokens.get("id_token", ""))
    claims = claims if isinstance(claims, dict) else {}
    account = account_from_claims(claims)
    if not account:
        account = tokens.get("account_id", "") or ""

    key = data.get("OPENAI_API_KEY")
    if isinstance(key, str) and key.startswith("sk-"):
        return openai_key(key, account)

    token = tokens.get("access_token")
    if isinstance(token, str) and token:
        access = jwt_claims(token)
        access = access if isinstance(access, dict) else {}
        auth = access.get("https://api.openai.com/auth") or {}
        auth = auth if isinstance(auth, dict) else {}
        account_id = tokens.get("account_id") or auth.get("chatgpt_account_id") or ""
        account_id = account_id if isinstance(account_id, str) else ""
        return Credential(
            kind=OAUTH, value=token, account=account,
            account_id=account_id, expires_at=epoch(access.get("exp")),
            usage_capable=bool(account_id),
            limited_reason="Codex login has no account ID. Sign in to Codex again.",
        )
    return None


OPENAI_SOURCES = [
    Source(id="manual", label="Admin key saved in this app", probe=lambda: None),
    Source(id="env", label="Environment variable", probe=_openai_env),
    Source(
        id="codex_cli",
        label="Codex CLI login (ChatGPT)",
        probe=_codex_login,
        refresh_hint="Run `codex` and sign in again.",
    ),
]


# -- Gemini ---------------------------------------------------------------

# Exactly what `GeminiProvider._load_key_file` insists on before it will sign
# a token with a key. The probes below check the same thing, from here, so the
# two cannot drift apart: a detector that says Connected about a file the
# provider then rejects turns an honest "not connected" into a refresh that
# fails with no visible cause, which is the worse of the two outcomes.
SERVICE_ACCOUNT_FIELDS = ("client_email", "private_key")


def usable_service_account(data) -> bool:
    """Total, on purpose: it is asked about whatever a file parsed into.

    `[]`, `"text"`, `1` and `null` are all valid JSON, and a question phrased
    as "is this a usable service account?" has an answer for every one of
    them. Reaching for `.get()` first turned those into an AttributeError,
    which the detection layer catches and reports as *not connected* - so a
    user who picked the wrong file was told nothing had been found, when
    their setting had been read and could not be used.
    """
    if not isinstance(data, dict):
        return False
    if data.get("type") != "service_account":
        return False
    return all(data.get(field) for field in SERVICE_ACCOUNT_FIELDS)


INCOMPLETE_KEY = (
    "This service account file is missing the fields needed to sign a "
    "token. Export the key again from the Google Cloud console, or point "
    "this at a complete service-account JSON with the Monitoring Viewer role."
)


def _gemini_service_account_env() -> Credential | None:
    value, _ = env_first("GOOGLE_APPLICATION_CREDENTIALS")
    if not value or not Path(value).is_file():
        return None
    data = read_json(Path(value)) or {}
    if not usable_service_account(data):
        # The variable is set and the file is there, so saying nothing would
        # read as "not configured" - the one thing the user knows is untrue.
        return Credential(
            kind=SERVICE_ACCOUNT,
            value="",
            account=data.get("client_email", ""),
            usage_capable=False,
            limited_reason=INCOMPLETE_KEY,
        )
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
    """A service account parked in gcloud's application-default location.

    `gcloud auth application-default login` writes an `authorized_user`
    credential, not a service account, and this app cannot use one: reading it
    would mean refreshing somebody else's OAuth token, which the credential
    policy forbids outright. That case used to return None and vanish, so a
    user who had followed the documentation saw "Not connected" and no reason
    why. It now comes back as Limited with the explanation attached, which is
    what every other unusable-but-present login does.

    Every location is looked at before anything is returned, and "found a
    service account" is not the same question as "found one this app can
    use". Returning the first file found meant a user login in the Windows
    path hid a perfectly good service account in the POSIX one - one machine
    reaching both is ordinary, between WSL and native tooling - and a key
    missing its `private_key` did the same while also reporting Connected,
    so the failure only appeared later, as a refresh error. Limited is the
    answer only when there is nothing usable anywhere.
    """
    fallback: Credential | None = None
    for candidate in (
        home() / "AppData" / "Roaming" / "gcloud" / "application_default_credentials.json",
        home() / ".config" / "gcloud" / "application_default_credentials.json",
    ):
        data = read_json(candidate)
        if not data:
            continue
        if usable_service_account(data):
            return Credential(
                kind=SERVICE_ACCOUNT,
                value=str(candidate),
                account=data.get("client_email", ""),
                project=data.get("project_id", ""),
            )
        if fallback is None:
            fallback = Credential(
                kind=SERVICE_ACCOUNT,
                value="",
                account=(
                    data.get("client_email", "")
                    or data.get("account", "")
                    or data.get("client_id", "")
                ),
                usage_capable=False,
                limited_reason=(
                    INCOMPLETE_KEY
                    if data.get("type") == "service_account"
                    else "This is a gcloud user login (application-default). "
                    "Reading usage with it would mean refreshing another "
                    "tool's OAuth token, which this app never does. Sign in "
                    "with `gemini`, or point this at a service-account JSON "
                    "with the Monitoring Viewer role."
                ),
            )
    return fallback


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
