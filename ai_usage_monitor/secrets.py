"""Encrypted storage for provider API keys.

Keys are sealed with Windows DPAPI (`CryptProtectData`) before being written to
the app's settings, so the stored blob is bound to the current Windows user
account - copying it to another machine or another user yields nothing. The
plaintext key never touches disk.

This is the right tool for a desktop app storing its own secrets: no master
password to invent, no key material of our own to manage. On a non-Windows
platform there is no DPAPI, so storage refuses rather than silently writing
plaintext.
"""

from __future__ import annotations

import base64
import ctypes
import sys
from ctypes import wintypes


class SecretsUnavailable(RuntimeError):
    """DPAPI is not available on this platform."""


if sys.platform == "win32":

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


def available() -> bool:
    return sys.platform == "win32"


def seal(plaintext: str) -> str:
    """Encrypt a secret for storage. Returns base64 text."""
    if not available():
        raise SecretsUnavailable(
            "Encrypted key storage requires Windows (DPAPI); refusing to store "
            "the key in plaintext."
        )
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


def unseal(stored: str) -> str:
    """Decrypt a secret written by `seal`. Returns '' if it cannot be read."""
    if not stored or not available():
        return ""
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


def mask(secret: str) -> str:
    """A safe-to-display rendering of a key, e.g. 'sk-admin-...9f2a'."""
    if not secret:
        return ""
    if len(secret) <= 12:
        return "•" * len(secret)
    return f"{secret[:8]}…{secret[-4:]}"
