"""Start-at-sign-in registration.

Windows uses the per-user Run key rather than a scheduled task or a
Startup-folder shortcut: it needs no admin rights, survives an app move as long
as we rewrite it, and is the entry Windows' own Startup Apps settings page
shows - so a user who turns it off there is not silently overridden by us
turning it back on.

macOS uses a LaunchAgent plist in ~/Library/LaunchAgents, for the same reasons:
no admin rights, and it is what System Settings' Login Items page lists, so the
user's choice there is visible to us.

That last point drives `sync()`: the app only *writes* the entry when its own
setting changed, never on every launch, so an external "off" sticks.
"""

from __future__ import annotations

import os
import plistlib
import subprocess
import sys
from pathlib import Path

WINDOWS = sys.platform == "win32"
MACOS = sys.platform == "darwin"

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = "AIUsageMonitor"

LABEL = "net.apichart.aiusagemonitor"


def supported() -> bool:
    return WINDOWS or MACOS


def describe() -> str:
    """What this setting is called on this platform, for UI copy."""
    if WINDOWS:
        return "Start with Windows"
    return "Open at login"


# -- macOS: LaunchAgent -----------------------------------------------------


def agent_path() -> Path:
    return Path.home() / "Library" / "LaunchAgents" / f"{LABEL}.plist"


def launch_arguments() -> list[str]:
    """The argv launchd should run at login."""
    executable = Path(sys.executable)
    if getattr(sys, "frozen", False):
        # Inside a .app this is Contents/MacOS/AIUsageMonitor, which is exactly
        # what launchd should exec - going through `open -a` would hand the
        # launch to LaunchServices and leave launchd supervising nothing.
        return [str(executable)]

    # Running from source: pass the project root explicitly. launchd starts
    # agents from /, where `-m ai_usage_monitor` would not resolve.
    root = Path(__file__).resolve().parent.parent
    snippet = (
        f"import sys;sys.path.insert(0,{str(root)!r});"
        "from ai_usage_monitor.app import main;sys.exit(main())"
    )
    return [str(executable), "-c", snippet]


def _read_agent() -> list[str] | None:
    path = agent_path()
    try:
        with path.open("rb") as handle:
            data = plistlib.load(handle)
    except (OSError, plistlib.InvalidFileException, ValueError):
        return None
    arguments = data.get("ProgramArguments")
    return [str(part) for part in arguments] if isinstance(arguments, list) else None


def _disabled_in_login_items() -> bool:
    """True if the user switched this agent off in System Settings.

    Login Items writes a disabled override rather than deleting our plist, so
    the file alone would report "on" for an agent macOS will never start.
    Best-effort: any surprise from launchctl means we fall back to the file.
    """
    try:
        done = subprocess.run(
            ["/bin/launchctl", "print-disabled", f"gui/{os.getuid()}"],
            capture_output=True, text=True, timeout=10,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    if done.returncode != 0:
        return False
    for line in done.stdout.splitlines():
        if f'"{LABEL}"' in line:
            return line.strip().endswith("true")
    return False


def _write_agent() -> bool:
    path = agent_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("wb") as handle:
            plistlib.dump(
                {
                    "Label": LABEL,
                    "ProgramArguments": launch_arguments(),
                    "RunAtLoad": True,
                    # Aqua only: this is a GUI app, and loading it into a
                    # background session would start a copy with no display.
                    "LimitLoadToSessionType": "Aqua",
                },
                handle,
            )
        return True
    except OSError:
        return False


def _remove_agent() -> bool:
    """Delete the plist, without unloading it.

    Deliberately no `launchctl bootout`: if this very process was started by
    the agent, booting it out kills the app the user is standing in. Removing
    the file is enough - the entry is gone from the next login onwards.
    """
    try:
        agent_path().unlink(missing_ok=True)
        return True
    except OSError:
        return False


# -- Windows: Run key -------------------------------------------------------


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


# -- the shared API ---------------------------------------------------------


def current_command() -> str | None:
    """What the OS holds right now, or None if there is no entry.

    A string on both platforms so `reconcile` can compare old against new
    without caring which store it came from.
    """
    if MACOS:
        if _disabled_in_login_items():
            return None
        arguments = _read_agent()
        return "\n".join(arguments) if arguments else None

    if not WINDOWS:
        return None
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, VALUE_NAME)
        return str(value)
    except OSError:
        return None


def wanted_command() -> str:
    return "\n".join(launch_arguments()) if MACOS else launch_command()


def is_enabled() -> bool:
    return current_command() is not None


def set_enabled(enabled: bool) -> bool:
    """Add or remove the entry. Returns True if the OS now matches."""
    if not supported():
        return not enabled

    if MACOS:
        return _write_agent() if enabled else _remove_agent()

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
    """Settle who wins between the stored preference and the OS.

    On the very first launch the app applies its own default. After that the
    **OS wins**: Windows' Startup Apps page and macOS' Login Items both edit
    the entry directly, and an app that re-adds itself on every launch is one a
    user cannot turn off. The one write we still make afterwards is a path
    refresh when the entry is present but points at an old location - same
    intent, stale target.

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

    if existing != wanted_command():
        set_enabled(True)
    return True


def apply(enabled: bool) -> None:
    """Write an explicit user choice straight through to the OS."""
    set_enabled(enabled)
