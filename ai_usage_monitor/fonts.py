"""The UI typeface, per platform.

The app was written against Segoe UI, which does not exist on macOS. Naming a
missing family is not an error in Qt - it silently substitutes, and the
substitute is picked per string rather than per app, so a window ends up in two
or three faces at once. Asking the platform for its own UI font instead keeps
one face throughout and matches what every other app on the machine uses.

Sizes are given in Windows points, because that is what the call sites were
tuned in. macOS UI text runs larger at the same nominal point size (13pt is the
system default there against Segoe UI's 9), so the offset below converts rather
than letting the Mac build render everything a third too small.
"""

from __future__ import annotations

import sys

from PySide6.QtGui import QFont, QFontDatabase

WINDOWS_FAMILY = "Segoe UI"

#: 9pt Segoe UI and 12-13pt on macOS are the same apparent size.
MACOS_POINT_OFFSET = 3


def family() -> str:
    """The system UI family name, resolved once per call from the platform."""
    if sys.platform == "win32":
        return WINDOWS_FAMILY
    # ".AppleSystemUIFont" on macOS, whatever the desktop reports elsewhere.
    return QFontDatabase.systemFont(QFontDatabase.SystemFont.GeneralFont).family()


def ui_font(point_size: float | None = None) -> QFont:
    """The UI font, optionally at a Windows-calibrated point size."""
    font = QFont(family())
    if point_size is not None:
        if sys.platform == "win32":
            font.setPointSizeF(point_size)
        else:
            font.setPointSizeF(point_size + MACOS_POINT_OFFSET)
    return font
