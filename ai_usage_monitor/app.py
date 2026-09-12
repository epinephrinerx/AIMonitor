"""Application entry point."""

from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtCore import QLocale, Qt
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

from . import fonts
from .main_window import MainWindow
from .settings import APP, ORG, Settings
from .single_instance import SingleInstance


def asset_path(name: str) -> Path:
    """Resolve a bundled asset, whether running from source or a PyInstaller exe."""
    base = getattr(sys, "_MEIPASS", None)
    root = Path(base) if base else Path(__file__).resolve().parent.parent
    return root / "assets" / name


def app_icon() -> QIcon:
    """The window and Dock/taskbar icon.

    PNG first: it is the one container every platform's Qt build reads without
    an image plugin. The .ico stays as the fallback for an older asset folder
    that predates the PNG, and .icns is not read here at all - macOS takes the
    Dock tile from the bundle, not from the running app.
    """
    for name in ("icon.png", "icon.ico"):
        path = asset_path(name)
        if path.exists():
            return QIcon(str(path))
    return QIcon()


def main() -> int:
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    # Qt's number widgets follow the system locale, which on a non-Latin locale
    # renders spin boxes in local digits while every other figure in this app is
    # formatted in English by Python. Pin one locale so they agree.
    QLocale.setDefault(QLocale(QLocale.Language.English, QLocale.Country.UnitedStates))

    app = QApplication(sys.argv)
    app.setApplicationName(APP)
    app.setOrganizationName(ORG)
    # Fusion honours the palette consistently across platform themes; the
    # app paints its own surfaces anyway, so the native style would only
    # add inconsistency between the widgets it draws and the ones we do.
    app.setStyle("Fusion")
    app.setFont(fonts.ui_font(9))
    # Hiding the window to the tray must not end the process; MainWindow calls
    # QApplication.quit() itself on a real exit.
    app.setQuitOnLastWindowClosed(False)

    # Claim the app before building anything expensive: a second launch should
    # cost a pipe connect, not a window, a tray icon and a network thread.
    guard = SingleInstance()
    if not guard.try_acquire():
        # A copy is already running and has been told to come to the front.
        return 0

    icon = app_icon()
    if not icon.isNull():
        app.setWindowIcon(icon)

    window = MainWindow(Settings())
    if not icon.isNull():
        window.setWindowIcon(icon)
    # Launching the app again is the same request as double-clicking the tray
    # icon: show me the window I already have.
    guard.activated.connect(window.resume_from_tray)
    window.show()
    try:
        return app.exec()
    finally:
        guard.release()


if __name__ == "__main__":
    raise SystemExit(main())
