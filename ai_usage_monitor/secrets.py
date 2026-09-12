"""Encrypted storage for provider API keys.

Two backends, one API. On Windows keys are sealed with DPAPI
(`CryptProtectData`) and the ciphertext is what gets written to settings. On
macOS the plaintext never goes near the settings file at all: it is handed to
the login Keychain and settings holds only a reference to the item.

Both are the same bargain - the OS owns the key material, there is no master
password to invent, and the stored value is bound to the signed-in user. The
`handle` argument is what lets the Keychain find its item again; DPAPI ignores
it, because a DPAPI blob carries everything needed to open it.

Where neither backend exists, storage refuses rather than silently writing
plaintext.
"""

from __future__ import annotations

import base64
import os
import subprocess
import sys

WINDOWS = sys.platform == "win32"
MACOS = sys.platform == "darwin"


class SecretsUnavailable(RuntimeError):
    """No OS-backed secret store on this platform."""


# -- Windows: DPAPI --------------------------------------------------------

if WINDOWS:
    import ctypes
    from ctypes import wintypes

    class _Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

        @classmethod
        def of(cls, data: bytes) -> "_Blob":
            buffer = ctypes.create_string_buffer(data, len(data))
            return cls(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char)))

        def value(self) -> bytes:
            return ctypes.string_at(self.pbData, self.cbData)

    _CRYPT32 = ctypes.WinDLL("crypt32", use_last_error=True)
    _KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _ENTROPY = b"AIUsageMonitor.providerKeys.v1"
    # The app was renamed from ClaudeUsageMonitor. DPAPI entropy is part of
    # the ciphertext, so keys sealed under the old name only open with the
    # old value; unseal falls back to it and the caller re-seals on save.
    _LEGACY_ENTROPY = b"ClaudeUsageMonitor.providerKeys.v1"


# -- macOS: login Keychain -------------------------------------------------

#: Settings holds this plus the handle, never the key itself.
_KEYCHAIN_MARKER = "keychain:v1:"

#: Mirrors `settings.SCOPE_ENV_VAR`, spelled out here because settings imports
#: this module. A scoped test run must not overwrite the real user's Keychain
#: item any more than it overwrites their settings.
_SCOPE_ENV_VAR = "AI_USAGE_MONITOR_SETTINGS_SCOPE"


def _service() -> str:
    scope = os.environ.get(_SCOPE_ENV_VAR, "").strip()
    return f"AIUsageMonitor.{scope}" if scope else "AIUsageMonitor"


def _quote(value: str) -> str:
    """Quote a value for `security -i`, which parses a shell-like mini syntax."""
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def _security_batch(command: str) -> int:
    """Run one `security` command with its arguments fed in on stdin.

    Batch mode (`-i`) rather than plain argv: an API key passed as `-w <key>`
    would sit in this process's command line for any other process of this user
    to read out of `ps`.
    """
    try:
        done = subprocess.run(
            ["/usr/bin/security", "-i"],
            input=command + "\n",
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise SecretsUnavailable(f"Could not run /usr/bin/security: {exc}") from exc
    return done.returncode


def _keychain_read(handle: str) -> str:
    """The stored secret, or '' if there is no item (or it cannot be read)."""
    try:
        done = subprocess.run(
            [
                "/usr/bin/security", "find-generic-password",
                "-a", handle, "-s", _service(), "-w",
            ],
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (OSError, subprocess.SubprocessError):
        return ""
    if done.returncode != 0:
        return ""
    # `security` appends a newline of its own; a key never ends in whitespace.
    return done.stdout.rstrip("\n")


# -- the shared API --------------------------------------------------------


def available() -> bool:
    return WINDOWS or MACOS


def backend_name() -> str:
    """How to describe the store in the UI."""
    if WINDOWS:
        return "Windows DPAPI"
    if MACOS:
        return "the macOS Keychain"
    return ""


def seal(plaintext: str, handle: str = "default") -> str:
    """Put a secret into the OS store. Returns the text to write to settings."""
    if not available():
        raise SecretsUnavailable(
            "Encrypted key storage needs Windows or macOS; refusing to store "
            "the key in plaintext."
        )

    if MACOS:
        # -U updates an existing item instead of failing, so re-entering a key
        # replaces it rather than leaving two items the reader picks between.
        code = _security_batch(
            "add-generic-password -U "
            f"-a {_quote(handle)} -s {_quote(_service())} "
            f"-l {_quote('AI Usage Monitor')} "
            f"-w {_quote(plaintext)}"
        )
        if code != 0:
            raise SecretsUnavailable(
                f"The Keychain refused to store the key (security exit {code})."
            )
        return f"{_KEYCHAIN_MARKER}{handle}"

    data = _Blob.of(plaintext.encode("utf-8"))
    entropy = _Blob.of(_ENTROPY)
    out = _Blob()
    ok = _CRYPT32.CryptProtectData(
        ctypes.byref(data), None, ctypes.byref(entropy), None, None, 0, ctypes.byref(out)
    )
    if not ok:
        raise SecretsUnavailable(
            f"CryptProtectData failed (error {ctypes.get_last_error()})"
        )
    try:
        return base64.b64encode(out.value()).decode("ascii")
    finally:
        _KERNEL32.LocalFree(out.pbData)


def unseal(stored: str, handle: str = "default") -> str:
    """Read back a secret written by `seal`. Returns '' if it cannot be read."""
    if not stored or not available():
        return ""

    if stored.startswith(_KEYCHAIN_MARKER):
        if not MACOS:
            return ""  # a settings file carried over from a Mac
        # The handle recorded at seal time wins: it is the one the item was
        # actually filed under.
        return _keychain_read(stored[len(_KEYCHAIN_MARKER):] or handle)

    if not WINDOWS:
        return ""  # a DPAPI blob, on a machine with no DPAPI to open it

    try:
        raw = base64.b64decode(stored.encode("ascii"), validate=True)
    except (ValueError, UnicodeEncodeError):
        return ""

    for entropy_value in (_ENTROPY, _LEGACY_ENTROPY):
        data = _Blob.of(raw)
        entropy = _Blob.of(entropy_value)
        out = _Blob()
        ok = _CRYPT32.CryptUnprotectData(
            ctypes.byref(data), None, ctypes.byref(entropy), None, None, 0,
            ctypes.byref(out),
        )
        if not ok:
            continue
        try:
            return out.value().decode("utf-8", "replace")
        finally:
            _KERNEL32.LocalFree(out.pbData)

    # Written by a different Windows user, or the blob was tampered with.
    return ""


def forget(handle: str = "default") -> None:
    """Drop the stored secret for `handle`.

    Only the Keychain needs this: clearing a key on Windows means deleting the
    settings value, and the blob goes with it. On macOS the settings value is
    just a pointer, so the item behind it has to be removed too or the key
    outlives the "clear" the user asked for.
    """
    if not MACOS:
        return
    # A missing item is exit 44; either way the secret is gone, which is all
    # the caller asked for.
    _security_batch(
        f"delete-generic-password -a {_quote(handle)} -s {_quote(_service())}"
    )


def mask(secret: str) -> str:
    """A safe-to-display rendering of a key, e.g. 'sk-admin-...9f2a'."""
    if not secret:
        return ""
    if len(secret) <= 12:
        return "•" * len(secret)
    return f"{secret[:8]}…{secret[-4:]}"
