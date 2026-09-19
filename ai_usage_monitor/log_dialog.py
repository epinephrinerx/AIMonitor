"""File > Save to Log: the usage log, on screen before it is anywhere else.

The window shows what every service used per day, who is signed in and on what
plan, and then offers the same document as a file or on paper. Reading it
first is the point: a log you cannot see before saving is a file you have to
open somewhere else to find out whether it was worth saving.

The report is rebuilt from the main window's latest snapshots, so leaving this
open while the dashboard refreshes keeps it current rather than freezing it at
the moment it was opened.
"""

from __future__ import annotations

import datetime as dt
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtGui import QPalette, QTextDocument
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QMessageBox,
    QPushButton,
    QTextBrowser,
    QVBoxLayout,
    QWidget,
)

from . import fonts
from . import report as reporting
from .theme import Theme, qcolor

# Qt's print support is a separate module. It is not excluded from the build,
# but a stripped one would fail here rather than at start-up, so degrade to a
# disabled button instead of taking the window down with it.
try:  # pragma: no cover - import shape, not logic
    from PySide6.QtPrintSupport import QPrinter, QPrintPreviewDialog

    PRINTING = True
except ImportError:  # pragma: no cover
    PRINTING = False

FILTERS = {
    "csv": "Spreadsheet (*.csv)",
    "md": "Markdown (*.md)",
    "html": "Web page (*.html)",
}


def default_filename(when: dt.datetime) -> str:
    return f"ai-usage-log-{when.astimezone().strftime('%Y-%m-%d')}.csv"


def render(report: reporting.Report, suffix: str) -> str:
    """The document in the format the chosen extension implies."""
    if suffix == ".csv":
        return reporting.to_csv(report)
    if suffix in (".html", ".htm"):
        return reporting.to_html(report, dark=False)
    return reporting.to_markdown(report)


def printable(report: reporting.Report) -> QTextDocument:
    """The printed page. Always the light palette: paper is white."""
    document = QTextDocument()
    document.setDefaultFont(fonts.ui_font(9))
    document.setHtml(reporting.to_html(report, dark=False))
    return document


def print_preview(report: reporting.Report, parent: QWidget | None = None) -> bool:
    """Show the print preview for one report. False when this build cannot print.

    A free function rather than a method: File > Print Report must not have to
    build a log window it is not going to show, and parenting a modal preview
    to an unshown dialog is how you get a preview behind the main window.
    """
    if not PRINTING:
        return False
    printer = QPrinter(QPrinter.PrinterMode.HighResolution)
    printer.setDocName("AI Usage Monitor usage log")
    preview = QPrintPreviewDialog(printer, parent)
    preview.setWindowTitle("AI Usage Monitor — Print report")
    preview.resize(900, 800)
    preview.paintRequested.connect(lambda target: printable(report).print_(target))
    preview.exec()
    return True


class UsageLogDialog(QDialog):
    """The usage log, with Save as… and Print."""

    def __init__(
        self,
        report: reporting.Report,
        theme: Theme,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self.theme = theme
        self.report = report
        self.setWindowTitle("AI Usage Monitor — Usage log")
        self.resize(760, 700)
        self.setSizeGripEnabled(True)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.view = QTextBrowser()
        self.view.setFrameShape(QTextBrowser.Shape.NoFrame)
        self.view.setOpenExternalLinks(False)
        layout.addWidget(self.view, 1)

        buttons = QDialogButtonBox()
        self.save_button = QPushButton("Save as…")
        self.save_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.save_button.clicked.connect(self.save_as)
        buttons.addButton(self.save_button, QDialogButtonBox.ButtonRole.ActionRole)

        self.print_button = QPushButton("Print…")
        self.print_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.print_button.clicked.connect(self.print_report)
        if not PRINTING:
            self.print_button.setEnabled(False)
            self.print_button.setToolTip("Printing is unavailable in this build.")
        buttons.addButton(self.print_button, QDialogButtonBox.ButtonRole.ActionRole)

        close = buttons.addButton(QDialogButtonBox.StandardButton.Close)
        close.setCursor(Qt.CursorShape.PointingHandCursor)
        close.clicked.connect(self.reject)
        buttons.rejected.connect(self.reject)
        buttons.setContentsMargins(16, 10, 16, 12)
        layout.addWidget(buttons)

        self.set_report(report)
        self.apply_theme(theme)

    def set_report(self, report: reporting.Report) -> None:
        """Swap in a newer report, keeping the reader where they were."""
        self.report = report
        scroll = self.view.verticalScrollBar().value()
        self.view.setHtml(reporting.to_html(report, dark=self.theme.dark))
        self.view.verticalScrollBar().setValue(scroll)

    # -- output ---------------------------------------------------------

    def save_as(self) -> None:
        start = Path.home() / "Documents" / default_filename(self.report.generated_at)
        path, chosen = QFileDialog.getSaveFileName(
            self,
            "Save usage log",
            str(start),
            ";;".join((FILTERS["csv"], FILTERS["md"], FILTERS["html"])),
        )
        if not path:
            return
        target = Path(path)
        if not target.suffix:
            # The user typed a bare name; take the extension from the filter
            # they picked rather than silently writing CSV called "log".
            for suffix, label in FILTERS.items():
                if label == chosen:
                    target = target.with_suffix(f".{suffix}")
                    break
            else:
                target = target.with_suffix(".csv")
        try:
            target.write_text(
                render(self.report, target.suffix.lower()), encoding="utf-8"
            )
        except OSError as exc:
            QMessageBox.warning(
                self, "Could not save the log", f"{target}\n\n{exc}"
            )
            return
        self.save_button.setText(f"Saved to {target.name}")

    def print_report(self) -> None:
        print_preview(self.report, self)

    # -- appearance ------------------------------------------------------

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.view.setFont(fonts.ui_font(10))
        palette = self.view.palette()
        palette.setColor(QPalette.ColorRole.Text, qcolor(theme.ink))
        palette.setColor(QPalette.ColorRole.Base, qcolor(theme.surface))
        self.view.setPalette(palette)
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
        self.set_report(self.report)
