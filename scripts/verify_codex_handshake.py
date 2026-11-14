#!/usr/bin/env python3
"""Standalone offline probe to verify JSON-RPC handshake with an installed Codex CLI.

This probe:
- Is strictly an offline/manual verification tool, completely separate from default tests.
- Takes an explicit path via --codex-executable.
- Executes only the JSON-RPC 2.0 `initialize` method (no account, login, or quota methods).
- Runs within an empty, isolated CODEX_HOME inside workspace artifacts.
- Uses a minimal/sanitized environment with all provider credentials stripped.
- Handles incoming JSON-RPC notifications and waits for matching response id.
- Enforces strict I/O timeouts/deadlines.
- Guarantees child process termination (kill + wait) and temp directory cleanup.
- Does not expose arbitrary subprocess stderr or credentials.
"""

from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
STRIPPED_PREFIXES = ("OPENAI_", "CODEX_", "ANTHROPIC_", "CLAUDE_", "GEMINI_", "GOOGLE_")


def build_sanitized_environment(isolated_codex_home: Path) -> dict[str, str]:
    """Build a minimal environment containing only system paths, with all provider variables stripped."""
    clean_env: dict[str, str] = {}
    for key in ("PATH", "SYSTEMROOT", "COMSPEC", "PATHEXT", "WINDIR"):
        val = os.environ.get(key)
        if val:
            clean_env[key] = val

    # Double check no credential-shaped variables leak
    for key in list(clean_env.keys()):
        if any(key.upper().startswith(p) for p in STRIPPED_PREFIXES):
            del clean_env[key]

    clean_env["CODEX_HOME"] = str(isolated_codex_home)
    return clean_env


def probe_codex_handshake(executable_path: str, timeout_seconds: float = 10.0) -> dict:
    if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise ValueError("Handshake timeout must be a positive finite number.")
    codex_exe = Path(executable_path).resolve()
    if not codex_exe.is_file():
        raise FileNotFoundError(f"Codex executable not found at: {executable_path}")

    artifacts_dir = (ROOT / "artifacts" / "verification" / "codex-handshake").resolve()
    if not artifacts_dir.is_relative_to(ROOT):
        raise ValueError("Probe directory must stay inside the workspace.")
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    temp_dir = Path(tempfile.mkdtemp(prefix="isolated-codex-home-", dir=str(artifacts_dir))).resolve()
    if not temp_dir.is_relative_to(artifacts_dir):
        raise ValueError("Isolated home must stay inside the probe directory.")

    env = build_sanitized_environment(temp_dir)
    cmd = [str(codex_exe), "app-server", "--listen", "stdio://"]

    proc = None
    reader_thread = None
    try:
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            cwd=str(temp_dir),
            env=env,
            text=True,
            encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )

        stdout_queue: queue.Queue[str] = queue.Queue()

        def reader():
            try:
                if proc.stdout:
                    for line in proc.stdout:
                        stdout_queue.put(line)
            except (ValueError, OSError, UnicodeError):
                pass

        reader_thread = threading.Thread(target=reader, daemon=True)
        reader_thread.start()

        # Send initialize request ONLY (no login, no tokens, no account endpoints)
        request_id = 1
        initialize_payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "initialize",
            "params": {
                "clientInfo": {
                    "name": "aimonitor-offline-probe",
                    "version": "2.0",
                }
            },
        }

        request_bytes = json.dumps(initialize_payload) + "\n"
        proc.stdin.write(request_bytes)
        proc.stdin.flush()

        # Wait for matching response id with deadline, handling notifications
        deadline = time.monotonic() + timeout_seconds
        notifications_received: list[str] = []
        matching_response = None

        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Codex handshake timed out after {timeout_seconds:.1f}s waiting for response id {request_id}")

            try:
                line = stdout_queue.get(timeout=remaining)
            except queue.Empty:
                raise TimeoutError(f"Codex handshake timed out after {timeout_seconds:.1f}s waiting for response id {request_id}")

            if not line:
                if proc.poll() is not None:
                    raise RuntimeError(f"Codex process terminated prematurely with exit code {proc.returncode}")
                continue

            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue

            if not isinstance(msg, dict):
                continue
            if msg.get("id") == request_id and "method" not in msg:
                matching_response = msg
                break
            elif "method" in msg:
                notifications_received.append(msg["method"])

        if "error" in matching_response:
            err_code = matching_response["error"].get("code", "unknown")
            raise RuntimeError(f"Codex initialize returned JSON-RPC error code {err_code}")

        result = matching_response.get("result")
        if not isinstance(result, dict):
            raise RuntimeError("Codex initialize returned an invalid result.")
        user_agent = result.get("userAgent", "unknown")

        return {
            "success": True,
            "executable": str(codex_exe),
            "response_id": request_id,
            "user_agent": user_agent,
            "notifications_count": len(notifications_received),
            "isolated_home": str(temp_dir),
        }

    finally:
        if proc is not None:
            try:
                if proc.poll() is None:
                    proc.kill()
                proc.wait(timeout=5)
                if reader_thread is not None:
                    reader_thread.join(timeout=2)
                    if reader_thread.is_alive():
                        raise RuntimeError("Codex probe reader did not stop after child exit.")
            finally:
                if proc.stdin:
                    try:
                        proc.stdin.close()
                    except Exception:
                        pass
                if proc.stdout:
                    try:
                        proc.stdout.close()
                    except Exception:
                        pass
                if proc.stderr:
                    try:
                        proc.stderr.close()
                    except Exception:
                        pass

        # Cleanup isolated temp directory
        if not temp_dir.is_relative_to(artifacts_dir) or temp_dir.is_symlink():
            raise RuntimeError("Refusing to clean an unexpected probe directory.")
        shutil.rmtree(temp_dir)


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify Codex App Server initialize handshake offline.")
    parser.add_argument(
        "--codex-executable",
        required=True,
        help="Explicit path to the installed codex executable binary.",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=10.0,
        help="Handshake timeout in seconds (default: 10.0).",
    )

    args = parser.parse_args()

    try:
        res = probe_codex_handshake(args.codex_executable, timeout_seconds=args.timeout)
        print("Codex handshake probe SUCCEEDED:")
        print(f"  Executable: {res['executable']}")
        print(f"  Response ID: {res['response_id']}")
        print(f"  User Agent: {res['user_agent']}")
        print(f"  Notifications handled: {res['notifications_count']}")
        print("  Isolated CODEX_HOME: cleaned up successfully")
        return 0
    except Exception as ex:
        # Do not expose raw subprocess stderr or environment variables
        print(f"Codex handshake probe FAILED: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
