"""The UI typeface, per platform.

The app was written against Segoe UI, which does not exist on macOS. Naming a
missing family is not an error in Qt - it silently substitutes, and the
substitute is picked per string rather than per app, so a window ends up in two
or three faces at once. It also costs real time: Qt logs "Populating font
family aliases took 119 ms" the first time it has to resolve a name that is not
installed. Asking the platform for its own UI font instead keeps one face
throughout, matches every other app on the machine, and skips that lookup.

Sizes are given in Windows points, because that is what the call sites were
tuned in. macOS UI text runs larger at the same nominal point size (13pt is the
system default there against Segoe UI's 9), so the offset below converts rather
than letting the Mac build render everything a third too small.
"""

from __future__ import annotations

import sys

from PySide6.QtGui import QFont, QFontInfo

WINDOWS = sys.platform == "win32"

WINDOWS_FAMILY = "Segoe UI"

#: 9pt Segoe UI and 12pt on macOS are about the same apparent size.
MACOS_POINT_OFFSET = 3


def ui_font(point_size: float | None = None) -> QFont:
    """The UI font, optionally at a Windows-calibrated point size.

    Off Windows this deliberately names no family at all: a default-constructed
    QFont carries the application font, which Qt has already set to the
    platform's UI face. Asking `QFontDatabase.systemFont()` instead looks more
    explicit but is worse - under the offscreen platform the tests run on, it
    answers with the generic "Sans Serif", a family that does not exist and
    that Qt then has to resolve by scanning aliases.
    """
    if WINDOWS:
        font = QFont(WINDOWS_FAMILY)
        if point_size is not None:
            font.setPointSizeF(point_size)
        return font

    font = QFont()
    if point_size is not None:
        font.setPointSizeF(point_size + MACOS_POINT_OFFSET)
    return font


def family() -> str:
    """The resolved UI family name, for anything that needs the string.

    Resolved through QFontInfo rather than read off the QFont, so the answer is
    a family that exists (".AppleSystemUIFont") and never a generic placeholder
    the caller would go on to name back at Qt.
    """
    return WINDOWS_FAMILY if WINDOWS else QFontInfo(QFont()).family()


def css_family() -> str:
    """A CSS `font-family` value for the rich text and the printed report.

    Per-platform rather than one shared stack: naming Segoe UI first is right
    on Windows and is exactly what triggers the alias scan on macOS, so each
    platform names only faces it actually has.
    """
    if WINDOWS:
        return "Segoe UI, sans-serif"
    return "-apple-system, system-ui, sans-serif"
