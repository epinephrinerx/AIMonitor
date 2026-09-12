"""Start-with-Windows registration.

Uses the per-user Run key rather than a scheduled task or a Startup-folder
shortcut: it needs no admin rights, survives an app move as long as we rewrite
it, and is the entry Windows' own Startup Apps settings page shows - so a user
who turns it off there is not silently overridden by us turning it back on.

That last point drives `sync()`: the app only *writes* the key when its own
setting changed, never on every launch, so an external "off" sticks.
"""

from __future__ import annotations

import sys
from pathlib import Path

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = "AIUsageMonitor"


def supported() -> bool:
    return sys.platform == "win32"


def launch_command() -> str:
    """The command Windows should run at sign-in, correctly quoted."""
    executable = Path(sys.executable)
    if getattr(sys, "frozen", False):
        # A PyInstaller build: the executable IS the app.
        return f'"{executable}"'

    # Running from source. Two things to get right: prefer pythonw.exe so
    # sign-in does not flash a console, and do not rely on the working
    # directory - Windows runs Run entries from system32, where `-m
    # ai_usage_monitor` would not resolve. Passing the project root explicitly
    # via -c avoids that. The snippet uses only single quotes, so the one pair
    # of double quotes delimiting it parses cleanly.
    windowed = executable.with_name("pythonw.exe")
    interpreter = windowed if windowed.exists() else executable
    root = Path(__file__).resolve().parent.parent
    snippet = (
        f"import sys;sys.path.insert(0,r'{root}');"
        "from ai_usage_monitor.app import main;sys.exit(main())"
    )
    return f'"{interpreter}" -c "{snippet}"'


def current_command() -> str | None:
    """What the Run key holds right now, or None if the value is absent."""
    if not supported():
        return None
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, VALUE_NAME)
        return str(value)
    except OSError:
        return None


def is_enabled() -> bool:
    return current_command() is not None


def set_enabled(enabled: bool) -> bool:
    """Add or remove the Run entry. Returns True if the registry now matches."""
    if not supported():
        return not enabled
    try:
        import winreg

        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE
        ) as key:
            if enabled:
                winreg.SetValueEx(
                    key, VALUE_NAME, 0, winreg.REG_SZ, launch_command()
                )
            else:
                try:
                    winreg.DeleteValue(key, VALUE_NAME)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        return False


def reconcile(default_on: bool, first_run: bool) -> bool:
    """Settle who wins between the stored preference and the registry.

    On the very first launch the app applies its own default. After that the
    **registry wins**: Windows' Startup Apps page edits that key directly, and
    an app that re-adds itself on every launch is one a user cannot turn off.
    The one write we still make afterwards is a path refresh when the entry is
    present but points at an old location - same intent, stale target.

    Returns the effective setting, for the caller to store back.
    """
    if not supported():
        return False

    if first_run:
        set_enabled(default_on)
        return default_on

    existing = current_command()
    if existing is None:
        return False  # switched off outside the app; respect it

    wanted = launch_command()
    if existing != wanted:
        set_enabled(True)
    return True


def apply(enabled: bool) -> None:
    """Write an explicit user choice straight through to the registry."""
    set_enabled(enabled)
