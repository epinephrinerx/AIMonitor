"""About > Version and About > the developer.

The update check runs on the global thread pool rather than the GUI thread: a
GitHub round trip over a bad link takes as long as its timeout, and a version
box that freezes the whole window while it asks is worse than one that says
"checking...". Nothing is downloaded and nothing is installed - the check
reports what the newest release is and opens the releases page in the user's
browser if they want it.
"""

from __future__ import annotations

from PySide6.QtCore import QObject, QRunnable, Qt, QThreadPool, QUrl, Signal
from PySide6.QtGui import QDesktopServices
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from . import version as versioning
from .theme import Theme, qcolor

DEVELOPER = "Apichart Chantanis"
DEVELOPER_EMAIL = "apichart@apichart.net"
PROJECT_URL = f"https://github.com/{versioning.REPO}"


class _CheckSignals(QObject):
    done = Signal(object)   # version.Release
    failed = Signal(str)


class _CheckTask(QRunnable):
    """One GitHub release lookup, off the GUI thread."""

    def __init__(self) -> None:
        super().__init__()
        self.signals = _CheckSignals()
        self.setAutoDelete(True)

    def run(self) -> None:  # noqa: D102 - QRunnable contract
        try:
            release = versioning.check_latest()
        except versioning.UpdateCheckError as exc:
            self.signals.failed.emit(str(exc))
        except Exception as exc:  # noqa: BLE001 - a thread-pool task must not raise
            self.signals.failed.emit(f"Unexpected error: {exc}")
        else:
            self.signals.done.emit(release)


class _BaseAboutDialog(QDialog):
    """Shared chrome so the two About windows look like one family."""

    def __init__(self, theme: Theme, title: str, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.setWindowTitle(title)
        self.setMinimumWidth(460)

        self.body = QVBoxLayout(self)
        self.body.setContentsMargins(24, 22, 24, 16)
        self.body.setSpacing(10)

    def _heading(self, text: str) -> QLabel:
        label = QLabel(text)
        label.setStyleSheet(
            f"color: {self.theme.ink}; font-size: 17px; font-weight: 600;"
        )
        return label

    def _line(self, text: str, muted: bool = False, rich: bool = False) -> QLabel:
        label = QLabel(text)
        label.setWordWrap(True)
        # A word-wrapping label in a vertical layout claims the slack for
        # itself and leaves the box looking airy and arbitrary.
        label.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Maximum)
        if rich:
            label.setTextFormat(Qt.TextFormat.RichText)
            label.setOpenExternalLinks(True)
        else:
            label.setTextInteractionFlags(
                Qt.TextInteractionFlag.TextBrowserInteraction
            )
        colour = self.theme.ink_muted if muted else self.theme.ink_secondary
        label.setStyleSheet(f"color: {colour}; font-size: 12px;")
        return label

    def _close_row(self) -> QDialogButtonBox:
        buttons = QDialogButtonBox()
        close = buttons.addButton(QDialogButtonBox.StandardButton.Close)
        close.setCursor(Qt.CursorShape.PointingHandCursor)
        close.clicked.connect(self.reject)
        buttons.rejected.connect(self.reject)
        return buttons

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        rule = qcolor(theme.border)
        ring = (
            f"rgba({rule.red()}, {rule.green()}, {rule.blue()},"
            f" {rule.alphaF():.2f})"
        )
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
            QPushButton:disabled {{ color: {theme.ink_muted}; }}
            """
        )


class VersionDialog(_BaseAboutDialog):
    """The installed version, and an on-demand check against GitHub releases."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(theme, "AI Usage Monitor — Version", parent)
        self._latest_url = versioning.RELEASES_URL

        self.body.addWidget(self._heading("AI Usage Monitor"))
        self.body.addWidget(self._line(f"Version {versioning.VERSION}"))
        self.body.addWidget(
            self._line(
                f'<a href="{PROJECT_URL}">{PROJECT_URL}</a>', muted=True, rich=True
            )
        )
        self.body.addSpacing(6)

        self.result = self._line(
            "Checking for updates is a read-only request to GitHub. Nothing is "
            "downloaded or installed.",
            muted=True,
        )
        self.body.addWidget(self.result)
        self.body.addSpacing(4)

        row = QHBoxLayout()
        row.setSpacing(8)
        self.check_button = QPushButton("Check for updates")
        self.check_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.check_button.clicked.connect(self.check)
        row.addWidget(self.check_button)

        self.open_button = QPushButton("Open releases page")
        self.open_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.open_button.clicked.connect(self._open_releases)
        row.addWidget(self.open_button)
        row.addStretch(1)
        self.body.addLayout(row)

        self.body.addStretch(1)
        self.body.addSpacing(6)
        self.body.addWidget(self._close_row())
        self.apply_theme(theme)

    def check(self) -> None:
        self.check_button.setEnabled(False)
        self.check_button.setText("Checking…")
        self.result.setText("Asking GitHub for the newest release…")
        task = _CheckTask()
        # Queued across threads by Qt, so these land on the GUI thread.
        task.signals.done.connect(self._on_release)
        task.signals.failed.connect(self._on_failed)
        QThreadPool.globalInstance().start(task)

    def _reset_button(self) -> None:
        self.check_button.setEnabled(True)
        self.check_button.setText("Check for updates")

    def _on_release(self, release) -> None:
        self._reset_button()
        self._latest_url = release.url or versioning.RELEASES_URL
        when = f" (published {release.published})" if release.published else ""
        if release.newer:
            self.result.setText(
                f"Update available: {release.tag}{when}. "
                f"You have {versioning.VERSION}."
            )
            self.open_button.setText("Get the update")
        else:
            self.result.setText(
                f"You are up to date. The newest release is {release.tag}{when}."
            )

    def _on_failed(self, message: str) -> None:
        self._reset_button()
        self.result.setText(message)

    def _open_releases(self) -> None:
        QDesktopServices.openUrl(QUrl(self._latest_url))


class DeveloperDialog(_BaseAboutDialog):
    """Who wrote this, and under what licence it is given to you."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(theme, "AI Usage Monitor — Developer", parent)

        self.body.addWidget(self._heading(DEVELOPER))
        self.body.addWidget(
            self._line(
                f'<a href="mailto:{DEVELOPER_EMAIL}">{DEVELOPER_EMAIL}</a>'
                f' &nbsp;·&nbsp; <a href="{PROJECT_URL}">github.com/{versioning.REPO}</a>',
                rich=True,
            )
        )
        self.body.addSpacing(8)
        self.body.addWidget(
            self._line(
                "AI Usage Monitor reads the AI sign-ins you already have on "
                "this machine and shows what each service says you have used. "
                "It never writes to another tool's credentials and never sends "
                "your usage anywhere."
            )
        )
        self.body.addSpacing(8)
        self.body.addWidget(
            self._line(
                "Written and maintained by the author above, with help from "
                "two coding assistants: <b>Claude Code</b> (Anthropic) and "
                "<b>Codex</b> (OpenAI). They are tools, in the same sense as "
                "the compiler; the design decisions and the releases are the "
                "author's.",
                rich=True,
            )
        )
        self.body.addSpacing(8)
        self.body.addWidget(
            self._line(
                f"Version {versioning.VERSION}  ·  GPL-3.0-or-later. "
                "The full licence is under About > License Agreement, and the "
                "notices for redistributed components sit beside it.",
                muted=True,
            )
        )
        self.body.addStretch(1)
        self.body.addSpacing(6)
        self.body.addWidget(self._close_row())
        self.apply_theme(theme)
