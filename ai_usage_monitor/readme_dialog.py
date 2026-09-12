"""The README, shown inside the app.

Rendered with `QTextBrowser.setMarkdown`, which Qt has built in - no markdown
library, nothing extra in the build. The document ships beside the executable so
the copy you read in the app is the same file the repository carries; there is
no second, drifting copy of the text.
"""

from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtCore import Qt, QUrl
from PySide6.QtGui import QDesktopServices, QFont, QPalette
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QPushButton,
    QTextBrowser,
    QVBoxLayout,
    QWidget,
)

from .theme import Theme, qcolor

DOC_NAME = "README.md"


def readme_path() -> Path | None:
    """Find the README whether running frozen or from source."""
    frozen = getattr(sys, "_MEIPASS", None)
    roots = []
    if frozen:
        # PyInstaller: one-file unpacks to _MEIPASS. One-dir is the awkward one
        # - since PyInstaller 6 the payload lives in `_internal\` beside the
        # exe, not next to it, so both have to be searched. The installer also
        # drops a copy in the app root for the Start Menu shortcut to open.
        exe_dir = Path(sys.executable).parent
        roots += [
            Path(frozen),
            Path(frozen) / "assets",
            exe_dir,
            exe_dir / "_internal",
        ]
    package_root = Path(__file__).resolve().parent.parent
    roots += [package_root, package_root / "assets"]
    for root in roots:
        candidate = root / DOC_NAME
        if candidate.is_file():
            return candidate
    return None


class ReadmeDialog(QDialog):
    """A scrollable, themed view of the project README."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.setWindowTitle("AI Usage Monitor — Readme")
        self.resize(860, 720)
        self.setSizeGripEnabled(True)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.view = QTextBrowser()
        self.view.setOpenExternalLinks(False)
        self.view.setOpenLinks(False)
        # In-document anchors are handled by the browser; real URLs go to the
        # user's browser rather than trying to render a web page in here.
        self.view.anchorClicked.connect(self._on_anchor)
        self.view.setFrameShape(QTextBrowser.Shape.NoFrame)
        layout.addWidget(self.view, 1)

        buttons = QDialogButtonBox()
        self.open_file = QPushButton("Open in editor")
        self.open_file.setCursor(Qt.CursorShape.PointingHandCursor)
        self.open_file.clicked.connect(self._open_externally)
        buttons.addButton(self.open_file, QDialogButtonBox.ButtonRole.ActionRole)
        close = buttons.addButton(QDialogButtonBox.StandardButton.Close)
        close.setCursor(Qt.CursorShape.PointingHandCursor)
        buttons.rejected.connect(self.reject)
        close.clicked.connect(self.reject)
        buttons.setContentsMargins(16, 10, 16, 12)
        layout.addWidget(buttons)

        self._path = readme_path()
        self._load()
        self.apply_theme(theme)

    def _load(self) -> None:
        if self._path is None:
            self.view.setMarkdown(
                "# Readme not found\n\n"
                f"`{DOC_NAME}` was not installed beside the application.\n\n"
                "Reinstalling should restore it."
            )
            self.open_file.setEnabled(False)
            return
        try:
            text = self._path.read_text(encoding="utf-8")
        except OSError as exc:
            self.view.setMarkdown(f"# Could not read the readme\n\n`{exc}`")
            self.open_file.setEnabled(False)
            return
        self.view.setMarkdown(text)
        self.view.moveCursor(self.view.textCursor().MoveOperation.Start)

    def _on_anchor(self, url: QUrl) -> None:
        if url.scheme() in ("http", "https"):
            QDesktopServices.openUrl(url)
            return
        # A "#section" link: let the browser jump within the document.
        fragment = url.fragment() or url.toString().lstrip("#")
        if fragment:
            self.view.scrollToAnchor(fragment)

    def _open_externally(self) -> None:
        if self._path is not None:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(self._path)))

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        body = QFont("Segoe UI", 10)
        self.view.setFont(body)
        # Qt's markdown renderer uses the document stylesheet for block
        # elements; tables and code need explicit colours or they render on a
        # white ground in dark mode.
        rule = qcolor(theme.border)
        ring = (
            f"rgba({rule.red()}, {rule.green()}, {rule.blue()},"
            f" {rule.alphaF():.2f})"
        )
        self.view.document().setDefaultStyleSheet(
            f"""
            body {{ color: {theme.ink}; }}
            h1, h2, h3 {{ color: {theme.ink}; }}
            a {{ color: {theme.accent}; }}
            code, pre {{
                font-family: Consolas, 'Courier New', monospace;
                color: {theme.ink};
                background-color: {theme.plane};
            }}
            th {{ color: {theme.ink_secondary}; }}
            td {{ color: {theme.ink_secondary}; }}
            """
        )
        self.view.setStyleSheet(
            f"""
            QTextBrowser {{
                background-color: {theme.surface};
                color: {theme.ink};
                padding: 20px 26px;
                selection-background-color: {theme.accent};
                selection-color: #ffffff;
            }}
            """
        )
        # Qt's rich-text engine takes link colour from the palette and ignores
        # the document stylesheet's `a { color }`, which left links washed out
        # against the dark surface.
        palette = self.view.palette()
        palette.setColor(QPalette.ColorRole.Link, qcolor(theme.accent))
        palette.setColor(QPalette.ColorRole.LinkVisited, qcolor(theme.accent))
        palette.setColor(QPalette.ColorRole.Text, qcolor(theme.ink))
        palette.setColor(QPalette.ColorRole.Base, qcolor(theme.surface))
        self.view.setPalette(palette)
        self.setStyleSheet(
            f"""
            QDialog {{ background-color: {theme.surface}; }}
            QPushButton {{
                background-color: {theme.surface};
                border: 1px solid {ring};
                border-radius: 6px;
                padding: 5px 12px;
                color: {theme.ink};
                font-size: 12px;
                min-height: 18px;
            }}
            QPushButton:hover {{ border-color: {theme.accent}; }}
            """
        )
        # Re-render so the new stylesheet is applied to the existing document.
        self._load()
