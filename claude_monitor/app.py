"""Application entry point."""

from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtCore import QLocale, Qt
from PySide6.QtGui import QFont, QIcon
from PySide6.QtWidgets import QApplication

from .main_window import MainWindow
from .settings import APP, ORG, Settings


def asset_path(name: str) -> Path:
    """Resolve a bundled asset, whether running from source or a PyInstaller exe."""
    base = getattr(sys, "_MEIPASS", None)
    root = Path(base) if base else Path(__file__).resolve().parent.parent
    return root / "assets" / name


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
    app.setStyle("Fusion")  # honours the palette consistently across Windows themes
    app.setFont(QFont("Segoe UI", 9))

    icon_file = asset_path("icon.ico")
    if icon_file.exists():
        app.setWindowIcon(QIcon(str(icon_file)))

    window = MainWindow(Settings())
    if icon_file.exists():
        window.setWindowIcon(QIcon(str(icon_file)))
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
