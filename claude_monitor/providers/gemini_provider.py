"""Gemini provider - quota usage via Google Cloud Monitoring.

Google publishes **no** dedicated usage endpoint for the Gemini API, and none at
all for Gemini Advanced subscriptions. The documented programmatic route is
Cloud Monitoring, reading the `serviceruntime.googleapis.com/api/consumer/
quota_used_count` and `request_count` metrics filtered to the
`generativelanguage.googleapis.com` service.

That API is OAuth-only - a plain Gemini API key cannot read it - so this
provider authenticates with a **service account JSON key file**, exchanging a
self-signed JWT for an access token (the standard two-legged OAuth flow):

    POST https://oauth2.googleapis.com/token
        grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
        assertion=<RS256 JWT signed with the service account private key>

The service account needs the **Monitoring Viewer** role on the project.

Caveat worth knowing: Cloud Monitoring reports metrics for *billable* API usage
on a Google Cloud project. Free-tier AI Studio keys that are not attached to a
project surface little or nothing here.
"""

from __future__ import annotations

import base64
import datetime as dt
import json
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from .. import formatting
from ..usage_log import DayBucket
from .base import HistoryView, Meter, Provider, ProviderSnapshot, Stat

TOKEN_URL = "https://oauth2.googleapis.com/token"
MONITORING_URL = "https://monitoring.googleapis.com/v3/projects/{project}/timeSeries"
SCOPE = "https://www.googleapis.com/auth/monitoring.read"
SERVICE = "generativelanguage.googleapis.com"
TIMEOUT_SECONDS = 20
USER_AGENT = "ClaudeUsageMonitor/1.1"


class GeminiProvider(Provider):
    id = "gemini"
    display_name = "Gemini"
    key_label = "Service account JSON"
    key_placeholder = r"C:\path\to\service-account.json"
    setup_hint = (
        "Google exposes no usage endpoint for Gemini, so usage is read from "
        "<b>Cloud Monitoring</b>, which is OAuth-only.<br><br>"
        "Create a service account in your Google Cloud project, grant it "
        "<b>Monitoring Viewer</b>, download its JSON key, and point this at the "
        "file. Enable the Generative Language API on the same project.<br><br>"
        "Gemini Advanced <i>subscription</i> limits are not available from any "
        "public API, and free-tier AI Studio keys with no project report little."
    )

    def __init__(self) -> None:
        self._path = ""
        self._token = ""
        self._token_expiry = 0.0
        self._project = ""
        self._email = ""

    def configure(self, key: str, extra: str = "") -> None:
        path = (key or "").strip().strip('"')
        if path != self._path:
            self._token = ""
            self._token_expiry = 0.0
        self._path = path
        self._project = (extra or "").strip()

    def is_configured(self) -> bool:
        return bool(self._path) and Path(self._path).exists()

    def fetch(self, days: int, metric: str, want_history: bool) -> ProviderSnapshot:
        snapshot = ProviderSnapshot(
            provider_id=self.id,
            configured=self.is_configured(),
            setup_hint=self.setup_hint,
            value_note="Request counts from Cloud Monitoring; Google reports no token-level cost here.",
        )
        if not snapshot.configured:
            if self._path:
                snapshot.error = f"Service account file not found: {self._path}"
            return snapshot

        try:
            credentials = self._load_key_file()
            project = self._project or credentials.get("project_id") or ""
            if not project:
                raise _GeminiError(
                    "No project id. Add one in settings, or use a key file that "
                    "contains project_id."
                )
            self._email = credentials.get("client_email", "")
            token = self._access_token(credentials)
        except _GeminiError as exc:
            snapshot.error = str(exc)
            snapshot.unauthorized = exc.unauthorized
            return snapshot

        now = dt.datetime.now(dt.timezone.utc)
        snapshot.fetched_at = now
        snapshot.account = f"{self._email or 'service account'} · project {project}"

        try:
            day_start = now.replace(hour=0, minute=0, second=0, microsecond=0)
            today = self._series_total(token, project, day_start, now)
            snapshot.meters.append(
                Meter(
                    key="requests_today",
                    title="Today",
                    subtitle="API requests",
                    percent=None,
                    detail=formatting.compact(today),
                    resets_at=day_start + dt.timedelta(days=1),
                )
            )

            if want_history:
                buckets, total = self._history(token, project, days)
                snapshot.history = HistoryView(
                    buckets=buckets,
                    series=["Requests"],
                    by_model=[("Requests", float(total))] if total else [],
                    by_project=[],
                    days=days,
                    metric=metric,
                    project_label="By project",
                )
                snapshot.stats = [
                    Stat("Requests in range", formatting.compact(total)),
                    Stat("Requests today", formatting.compact(today)),
                    Stat("Project", project),
                ]
        except _GeminiError as exc:
            snapshot.error = str(exc)
            snapshot.unauthorized = exc.unauthorized
        return snapshot

    # -- auth -------------------------------------------------------------

    def _load_key_file(self) -> dict:
        try:
            data = json.loads(Path(self._path).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise _GeminiError(f"Could not read the service account file: {exc}") from exc
        if data.get("type") != "service_account":
            raise _GeminiError(
                "That JSON is not a service account key (expected "
                '"type": "service_account").'
            )
        for required in ("client_email", "private_key"):
            if not data.get(required):
                raise _GeminiError(f"Service account key is missing {required}.")
        return data

    def _access_token(self, credentials: dict) -> str:
        if self._token and time.time() < self._token_expiry - 60:
            return self._token

        try:
            import rsa  # pure-python RS256 signer
        except ImportError as exc:  # pragma: no cover - packaged build includes it
            raise _GeminiError(
                "The 'rsa' package is required for Google service account auth."
            ) from exc

        now = int(time.time())
        claims = {
            "iss": credentials["client_email"],
            "scope": SCOPE,
            "aud": TOKEN_URL,
            "iat": now,
            "exp": now + 3600,
        }
        header = {"alg": "RS256", "typ": "JWT"}
        signing_input = b".".join(
            (_b64url(json.dumps(header).encode()), _b64url(json.dumps(claims).encode()))
        )
        try:
            private_key = rsa.PrivateKey.load_pkcs1(
                credentials["private_key"].encode("utf-8")
            )
        except Exception:
            try:
                private_key = rsa.PrivateKey.load_pkcs1_openssl_pem(
                    credentials["private_key"].encode("utf-8")
                )
            except Exception as exc:
                raise _GeminiError(
                    "Could not parse the service account private key."
                ) from exc
        signature = rsa.sign(signing_input, private_key, "SHA-256")
        assertion = b".".join((signing_input, _b64url(signature))).decode("ascii")

        body = urllib.parse.urlencode(
            {
                "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
                "assertion": assertion,
            }
        ).encode("ascii")
        request = urllib.request.Request(
            TOKEN_URL,
            data=body,
            method="POST",
            headers={
                "Content-Type": "application/x-www-form-urlencoded",
                "User-Agent": USER_AGENT,
            },
        )
        payload = _send(request, "oauth2.googleapis.com")
        token = payload.get("access_token")
        if not token:
            raise _GeminiError("Google returned no access token.", unauthorized=True)
        self._token = token
        self._token_expiry = time.time() + float(payload.get("expires_in") or 3600)
        return token

    # -- monitoring -------------------------------------------------------

    def _query(
        self, token: str, project: str, start: dt.datetime, end: dt.datetime, period: int
    ) -> list[dict]:
        params = {
            "filter": (
                'metric.type="serviceruntime.googleapis.com/api/request_count" '
                f'AND resource.labels.service="{SERVICE}"'
            ),
            "interval.startTime": start.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "interval.endTime": end.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "aggregation.alignmentPeriod": f"{period}s",
            "aggregation.perSeriesAligner": "ALIGN_SUM",
            "aggregation.crossSeriesReducer": "REDUCE_SUM",
        }
        url = MONITORING_URL.format(project=urllib.parse.quote(project))
        request = urllib.request.Request(
            f"{url}?{urllib.parse.urlencode(params)}",
            method="GET",
            headers={
                "Authorization": f"Bearer {token}",
                "User-Agent": USER_AGENT,
            },
        )
        payload = _send(request, "monitoring.googleapis.com")
        return payload.get("timeSeries") or []

    def _series_total(
        self, token: str, project: str, start: dt.datetime, end: dt.datetime
    ) -> int:
        span = max(60, int((end - start).total_seconds()))
        total = 0
        for series in self._query(token, project, start, end, span):
            for point in series.get("points") or []:
                total += _point_value(point)
        return total

    def _history(
        self, token: str, project: str, days: int
    ) -> tuple[list[DayBucket], int]:
        end = dt.datetime.now(dt.timezone.utc)
        start = (end - dt.timedelta(days=days - 1)).replace(
            hour=0, minute=0, second=0, microsecond=0
        )
        by_day: dict[dt.date, int] = {}
        for series in self._query(token, project, start, end, 86_400):
            for point in series.get("points") or []:
                stamp = ((point.get("interval") or {}).get("endTime")) or ""
                try:
                    when = dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))
                except ValueError:
                    continue
                day = when.astimezone().date()
                by_day[day] = by_day.get(day, 0) + _point_value(point)

        today = dt.datetime.now().astimezone().date()
        first = today - dt.timedelta(days=days - 1)
        buckets = []
        for offset in range(days):
            day = first + dt.timedelta(days=offset)
            bucket = DayBucket(day)
            value = by_day.get(day, 0)
            if value:
                bucket.per_model["Requests"] = float(value)
            buckets.append(bucket)
        return buckets, sum(by_day.values())


class _GeminiError(Exception):
    def __init__(self, message: str, *, unauthorized: bool = False) -> None:
        super().__init__(message)
        self.unauthorized = unauthorized


def _b64url(data: bytes) -> bytes:
    return base64.urlsafe_b64encode(data).rstrip(b"=")


def _point_value(point: dict) -> int:
    value = point.get("value") or {}
    for field in ("int64Value", "doubleValue"):
        if field in value:
            try:
                return int(float(value[field]))
            except (TypeError, ValueError):
                return 0
    return 0


def _send(request: urllib.request.Request, host: str) -> dict:
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
            raise _GeminiError(
                "Google rejected the service account. Check it has the "
                "Monitoring Viewer role on the project."
                + (f" ({detail})" if detail else ""),
                unauthorized=True,
            ) from exc
        raise _GeminiError(
            f"{host} returned HTTP {exc.code}" + (f": {detail}" if detail else ".")
        ) from exc
    except urllib.error.URLError as exc:
        raise _GeminiError(f"Could not reach {host}: {exc.reason}") from exc
    except (TimeoutError, json.JSONDecodeError) as exc:
        raise _GeminiError(f"Bad response from {host}: {exc}") from exc


def _detail(body: str) -> str:
    try:
        parsed = json.loads(body)
    except (json.JSONDecodeError, TypeError):
        return body.strip()[:160]
    error = parsed.get("error") if isinstance(parsed, dict) else None
    if isinstance(error, dict):
        message = error.get("message")
        if isinstance(message, str):
            return message
    return ""
