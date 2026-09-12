"""Colour tokens and Qt palette construction.

The palette is the validated data-viz reference instance: categorical hues are
assigned by fixed slot order (never cycled), status colours are reserved and
always ship beside an icon + label, and the dark column is its own selected
set of steps rather than an automatic inversion of the light one.
"""

from __future__ import annotations

import subprocess
import sys
from dataclasses import dataclass, field

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor, QGuiApplication, QPalette


# Categorical slots, in fixed assignment order.
_CATEGORICAL_LIGHT = [
    "#2a78d6",  # 1 blue
    "#eb6834",  # 2 orange
    "#1baf7a",  # 3 aqua
    "#eda100",  # 4 yellow
    "#e87ba4",  # 5 magenta
    "#008300",  # 6 green
    "#4a3aa7",  # 7 violet
    "#e34948",  # 8 red
]
_CATEGORICAL_DARK = [
    "#3987e5",
    "#d95926",
    "#199e70",
    "#c98500",
    "#d55181",
    "#008300",
    "#9085e9",
    "#e66767",
]

# Status palette: fixed, never themed, never reused as a series colour.
STATUS = {
    "good": "#0ca30c",
    "warning": "#fab219",
    "serious": "#ec835a",
    "critical": "#d03b3b",
}

# Blue sequential ramp, used for meter tracks (a lighter step of the fill's ramp).
_BLUE_RAMP = {
    100: "#cde2fb",
    150: "#b7d3f6",
    200: "#9ec5f4",
    250: "#86b6ef",
    300: "#6da7ec",
    350: "#5598e7",
    400: "#3987e5",
    450: "#2a78d6",
    500: "#256abf",
    550: "#1c5cab",
    600: "#184f95",
    650: "#104281",
    700: "#0d366b",
}


@dataclass(frozen=True)
class Theme:
    """One resolved colour scheme."""

    dark: bool
    surface: str  # chart / card surface
    plane: str  # page behind the cards
    ink: str  # primary text
    ink_secondary: str
    ink_muted: str  # axis labels, ticks
    grid: str  # hairline gridline
    baseline: str  # axis line, meter track fallback
    border: str  # hairline ring around cards
    track: str  # meter unfilled track
    accent: str
    categorical: list[str] = field(default_factory=list)

    def series(self, index: int) -> str:
        """Categorical hue for slot `index`, folded to the last slot past eight.

        Callers are expected to fold surplus entities into an "Other" bucket
        before reaching that point; this is only a guard against a crash.
        """
        return self.categorical[min(index, len(self.categorical) - 1)]

    @staticmethod
    def status(name: str) -> str:
        return STATUS.get(name, STATUS["good"])


LIGHT = Theme(
    dark=False,
    surface="#fcfcfb",
    plane="#f9f9f7",
    ink="#0b0b0b",
    ink_secondary="#52514e",
    ink_muted="#898781",
    grid="#e1e0d9",
    baseline="#c3c2b7",
    border="rgba(11,11,11,0.10)",
    track=_BLUE_RAMP[150],
    accent="#2a78d6",
    categorical=_CATEGORICAL_LIGHT,
)

DARK = Theme(
    dark=True,
    surface="#1a1a19",
    plane="#0d0d0d",
    ink="#ffffff",
    ink_secondary="#c3c2b7",
    ink_muted="#898781",
    grid="#2c2c2a",
    baseline="#383835",
    border="rgba(255,255,255,0.10)",
    track=_BLUE_RAMP[650],
    accent="#3987e5",
    categorical=_CATEGORICAL_DARK,
)


def system_prefers_dark() -> bool:
    """Whether the OS is currently in dark mode; default to light.

    Qt 6.5+ reports this itself on both Windows and macOS, which is one answer
    instead of two platform readers - and it is the same value Qt uses for its
    own default palette, so the app never disagrees with its own widgets. The
    per-platform readers below are the fallback for a call made before
    QGuiApplication exists (a screenshot harness, a unit test).
    """
    app = QGuiApplication.instance()
    if app is not None:
        scheme = app.styleHints().colorScheme()
        if scheme == Qt.ColorScheme.Dark:
            return True
        if scheme == Qt.ColorScheme.Light:
            return False

    if sys.platform == "darwin":
        try:
            done = subprocess.run(
                ["/usr/bin/defaults", "read", "-g", "AppleInterfaceStyle"],
                capture_output=True, text=True, timeout=10,
            )
        except (OSError, subprocess.SubprocessError):
            return False
        # The key is absent in light mode, which is an error exit, not "Light".
        return done.returncode == 0 and done.stdout.strip() == "Dark"

    if sys.platform != "win32":
        return False
    try:
        import winreg

        key = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        )
        with key:
            value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        return value == 0
    except OSError:
        return False


def resolve(name: str) -> Theme:
    """`name` is 'light', 'dark', or 'system'."""
    if name == "system":
        return DARK if system_prefers_dark() else LIGHT
    return DARK if name == "dark" else LIGHT


# Local warning thresholds. The usage API reports its own severity, but it stays
# "normal" well past the point a user wants a nudge, so the meters apply these
# on top: yellow from 75%, red from 90%.
WARNING_PERCENT = 75.0
CRITICAL_PERCENT = 90.0

_SEVERITY_RANK = {"normal": 0, "warning": 1, "serious": 2, "critical": 3}


def severity_for(percent: float | None, api_severity: str = "normal") -> str:
    """Combine the local 75/90 thresholds with whatever the server reported.

    Takes whichever is *worse*, so a server-side lock or an unusual severity is
    never masked by a low percentage, and a high percentage still colours even
    when the server calls it normal.
    """
    local = "normal"
    if percent is not None:
        if percent >= CRITICAL_PERCENT:
            local = "critical"
        elif percent >= WARNING_PERCENT:
            local = "warning"
    reported = api_severity or "normal"
    if _SEVERITY_RANK.get(reported, 0) > _SEVERITY_RANK[local]:
        return reported
    return local


def severity_word(severity: str) -> tuple[str, str]:
    """Glyph + word for a severity, so colour never carries the state alone."""
    return {
        "normal": ("✓", "Normal"),
        "warning": ("⚠", "High"),
        "serious": ("⚠", "Very high"),
        "critical": ("⚠", "Critical"),
    }.get(severity, ("", severity.title()))


def severity_color(theme: Theme, severity: str) -> str:
    """Meter fill for a quota severity.

    Below the warning threshold the meter wears the accent hue; past it the
    reserved status colours take over. Every caller pairs this with a visible
    severity word so colour never carries the state on its own.
    """
    if severity in ("warning", "serious", "critical"):
        return STATUS[severity]
    return theme.accent


def qcolor(value: str, alpha: float | None = None) -> QColor:
    """Parse a token into a QColor, optionally overriding alpha (0..1)."""
    if value.startswith("rgba"):
        parts = value[value.index("(") + 1 : value.rindex(")")].split(",")
        color = QColor(int(parts[0]), int(parts[1]), int(parts[2]))
        color.setAlphaF(float(parts[3]))
    else:
        color = QColor(value)
    if alpha is not None:
        color.setAlphaF(alpha)
    return color


def build_qpalette(theme: Theme) -> QPalette:
    """A QPalette so native widgets (scrollbars, tooltips) match the theme."""
    palette = QPalette()
    palette.setColor(QPalette.ColorRole.Window, qcolor(theme.plane))
    palette.setColor(QPalette.ColorRole.WindowText, qcolor(theme.ink))
    palette.setColor(QPalette.ColorRole.Base, qcolor(theme.surface))
    palette.setColor(QPalette.ColorRole.AlternateBase, qcolor(theme.plane))
    palette.setColor(QPalette.ColorRole.Text, qcolor(theme.ink))
    palette.setColor(QPalette.ColorRole.Button, qcolor(theme.surface))
    palette.setColor(QPalette.ColorRole.ButtonText, qcolor(theme.ink))
    palette.setColor(QPalette.ColorRole.ToolTipBase, qcolor(theme.surface))
    palette.setColor(QPalette.ColorRole.ToolTipText, qcolor(theme.ink))
    palette.setColor(QPalette.ColorRole.Highlight, qcolor(theme.accent))
    palette.setColor(QPalette.ColorRole.HighlightedText, QColor("#ffffff"))
    palette.setColor(QPalette.ColorRole.PlaceholderText, qcolor(theme.ink_muted))
    return palette
