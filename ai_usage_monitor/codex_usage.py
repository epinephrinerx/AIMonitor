"""Read Codex quota through the documented App Server protocol.

Use an isolated temporary home and externally supplied access tokens in memory.
No refresh token, user config, tools, threads, or prompts are sent to the child.
The original Codex login is never changed. Each poll closes its child process.
Protocol: https://learn.chatgpt.com/docs/app-server
"""

from __future__ import annotations

import datetime as dt
import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time

from .detection import Credential, epoch

TIMEOUT_SECONDS = 25


class CodexError(Exception):
    def __init__(self, message: str, *, unauthorized: bool = False) -> None:
        super().__init__(message)
        self.unauthorized = unauthorized


def executable() -> str | None:
    # Windows GUI launches need not inherit the terminal's PATH.
    found = shutil.which("codex.exe" if os.name == "nt" else "codex")
    if found:
        return found
    local = os.environ.get("LOCALAPPDATA")
    if local:
        root = Path(local) / "OpenAI" / "Codex" / "bin"
        candidates = list(root.glob("*/codex.exe"))
        if candidates:
            return str(max(candidates, key=lambda p: p.stat().st_mtime))
    return None


class _Client:
    def __init__(self, process: subprocess.Popen) -> None:
        self.process = process
        self.messages: queue.Queue = queue.Queue(maxsize=256)
        self.sequence = 0
        self.stopped = threading.Event()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self) -> None:
        try:
            for line in self.process.stdout:
                try:
                    message = json.loads(line)
                except (ValueError, UnicodeError):
                    continue
                if not isinstance(message, dict):
                    continue
                while not self.stopped.is_set():
                    try:
                        self.messages.put(message, timeout=0.1)
                        break
                    except queue.Full:
                        continue
                if self.stopped.is_set():
                    return
        finally:
            try:
                self.messages.put_nowait(None)
            except queue.Full:
                pass

    def send(self, message: dict) -> None:
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def call(self, method: str, params: dict | None = None) -> dict:
        self.sequence += 1
        request_id = self.sequence
        request = {"id": request_id, "method": method}
        if params is not None:
            request["params"] = params
        self.send(request)
        deadline = time.monotonic() + TIMEOUT_SECONDS
        while True:
            try:
                message = self.messages.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty:
                raise CodexError("Codex usage request timed out. Try refreshing again.") from None
            if message is None:
                raise CodexError("Codex App Server stopped. Update Codex and try again.")
            if "method" in message and "id" in message:
                # Explicitly refuse refresh and every other server request.
                self.send({"id": message["id"], "error": {
                    "code": -32601, "message": "Read-only monitor; sign in with Codex again."
                }})
                if message["method"] == "account/chatgptAuthTokens/refresh":
                    raise CodexError("Codex login expired or was rejected. Open Codex to "
                                     "refresh the login, then refresh here.", unauthorized=True)
                continue
            if message.get("id") != request_id:
                continue
            if "error" in message:
                # Never expose raw server messages: they can include credentials.
                raise CodexError(f"Codex could not complete {method}. Check your connection, "
                                 "update Codex, and sign in again if needed.")
            result = message.get("result")
            if not isinstance(result, dict):
                raise CodexError("Codex returned an invalid usage response.")
            return result


def fetch(credential: Credential, want_history: bool) -> tuple[dict, dict | None, str | None]:
    if credential.expired:
        raise CodexError("Codex login expired. Open Codex to refresh the login, then "
                         "refresh here.", unauthorized=True)
    command = executable()
    if not command:
        raise CodexError("Codex executable was not found. Install Codex CLI or the "
                         "Codex desktop app, then restart this monitor.")
    if not credential.account_id or not credential.value:
        raise CodexError("Codex login is incomplete. Sign in to Codex again.", unauthorized=True)
    with tempfile.TemporaryDirectory(prefix="ai-monitor-codex-") as root:
        env = os.environ.copy()
        # Do not pass unrelated provider credentials into the isolated client.
        for key in list(env):
            if key.startswith(("OPENAI_", "CODEX_", "ANTHROPIC_")):
                env.pop(key)
        env["CODEX_HOME"] = root
        process = None
        client = None
        try:
            process = subprocess.Popen(
                [command, "app-server", "--listen", "stdio://"],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding="utf-8", errors="replace", bufsize=1,
                cwd=root, env=env,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
            client = _Client(process)
            client.call("initialize", {
                "clientInfo": {"name": "ai_usage_monitor", "version": "1.1.0"},
                "capabilities": {"experimentalApi": True},
            })
            client.send({"method": "initialized"})
            client.call("account/login/start", {
                "type": "chatgptAuthTokens", "accessToken": credential.value,
                "chatgptAccountId": credential.account_id,
            })
            limits = client.call("account/rateLimits/read")
            history, history_error = None, None
            if want_history:
                try:
                    history = client.call("account/usage/read")
                except CodexError as exc:
                    history_error = "Token history unavailable. " + str(exc)
            return limits, history, history_error
        except OSError:
            raise CodexError("Could not start or communicate with Codex App Server.") from None
        finally:
            if client is not None:
                client.stopped.set()
            if process is not None:
                if process.poll() is None:
                    process.terminate()
                try:
                    process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=3)
                if client is not None:
                    client.reader.join(timeout=1)
                process.stdin.close()
                process.stdout.close()


def meters(payload: dict) -> list:
    from .providers.base import Meter

    groups = payload.get("rateLimitsByLimitId")
    groups = dict(groups) if isinstance(groups, dict) else {}
    legacy = payload.get("rateLimits")
    if isinstance(legacy, dict):
        groups.setdefault(legacy.get("limitId") or "codex", legacy)
    result = []
    for limit_id in sorted(groups, key=lambda key: (key != "codex", key)):
        group = groups[limit_id]
        if not isinstance(group, dict):
            continue
        label = group.get("limitName") or limit_id
        for kind in ("primary", "secondary"):
            window = group.get(kind)
            if not isinstance(window, dict):
                continue
            percent = window.get("usedPercent")
            if (isinstance(percent, bool) or not isinstance(percent, (int, float))
                    or not math.isfinite(percent) or percent < 0):
                continue
            duration = window.get("windowDurationMins")
            title = "Session" if kind == "primary" else "Secondary"
            subtitle = "Codex quota"
            if isinstance(duration, (int, float)) and not isinstance(duration, bool) and duration > 0:
                title = "Weekly" if duration == 10080 else title
                subtitle = f"{duration / 60:g}-hour window" if duration % 60 == 0 else f"{duration:g}-minute window"
            if limit_id != "codex":
                title = f"{label} · {title}"
            reason = group.get("rateLimitReachedType")
            result.append(Meter(
                key=f"codex_{limit_id}_{kind}", title=title, subtitle=subtitle,
                percent=min(100.0, float(percent)),
                severity="critical" if reason or percent >= 90 else "warning" if percent >= 75 else "normal",
                resets_at=epoch(window.get("resetsAt")),
                locked_reason=str(reason).replace("_", " ") if reason else None,
            ))
    return result


def history(payload: dict, days: int, metric: str) -> tuple[object | None, list]:
    from . import formatting
    from .providers.base import HistoryView, Stat
    from .usage_log import DayBucket

    stats = []
    summary = payload.get("summary")
    if isinstance(summary, dict):
        for key, label in (("lifetimeTokens", "Lifetime tokens"), ("peakDailyTokens", "Peak daily tokens")):
            value = summary.get(key)
            if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
                stats.append(Stat(label, formatting.compact(value), "Reported by Codex"))
    # The server exposes total tokens only, not output tokens or dollar values.
    rows = payload.get("dailyUsageBuckets")
    if metric != "Total tokens" or not isinstance(rows, list):
        return None, stats
    days = max(1, min(days, 90))
    today = dt.datetime.now().astimezone().date()
    start = today - dt.timedelta(days=days - 1)
    values = {}
    for row in rows:
        if not isinstance(row, dict):
            continue
        try:
            day = dt.date.fromisoformat(row.get("startDate", ""))
        except (ValueError, TypeError):
            continue
        value = row.get("tokens")
        if start <= day <= today and isinstance(value, int) and not isinstance(value, bool) and value >= 0:
            values[day] = value
    if not values:
        return None, stats
    buckets = [DayBucket(day, {"Codex": value}) for day, value in sorted(values.items())]
    total = sum(values.values())
    stats.insert(0, Stat("Reported tokens in range", formatting.compact(total), "Available daily buckets"))
    return HistoryView(buckets=buckets, series=["Codex"], days=days, metric=metric), stats
