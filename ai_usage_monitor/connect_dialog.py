"""One credential dialog, used for every AI service.

There is deliberately no per-service dialog class. The layout is driven
entirely by metadata on the provider (`key_label`, `extra_label`, `setup_hint`)
and by the `Detection` result, so Claude, OpenAI and Gemini all present the
same three sections in the same order:

    1. What we found on this machine, and whether it can read usage
    2. Credentials you supply yourself (hidden when the service needs none)
    3. What this service can and cannot report

Saving does not test the connection here - the connections card behind the
dialog refreshes immediately and is the real answer, which avoids a blocking
network call on the GUI thread.
"""

from __future__ import annotations

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QFrame,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from . import secrets
from .detection import CONNECTED, EXPIRED, MISSING, PARTIAL, Detection
from .providers import Provider
from .settings import Settings
from .theme import Theme

_STATE_GLYPH = {
    CONNECTED: "✓",
    PARTIAL: "!",
    EXPIRED: "!",
    MISSING: "–",
}


class ConnectDialog(QDialog):
    """Connect one service. Same structure whichever service it is."""

    def __init__(
        self,
        provider: Provider,
        detection: Detection,
        settings: Settings,
        theme: Theme,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self.provider = provider
        self.settings = settings
        self.theme = theme
        self._key_field: QLineEdit | None = None
        self._extra_field: QLineEdit | None = None

        self.setWindowTitle(
            f"Connect {provider.display_name}"
            if provider.needs_key
            else f"{provider.display_name} sign-in"
        )
        self.setMinimumWidth(560)

        layout = QVBoxLayout(self)
        layout.setSpacing(14)
        layout.addWidget(self._detected_section(detection))

        if provider.needs_key:
            layout.addWidget(self._credentials_section())

        about = QLabel(provider.setup_hint)
        about.setWordWrap(True)
        about.setTextFormat(Qt.TextFormat.RichText)
        about.setStyleSheet(f"color: {theme.ink_secondary}; font-size: 11px;")
        layout.addWidget(about)
        layout.addStretch(1)

        if provider.needs_key:
            buttons = QDialogButtonBox(
                QDialogButtonBox.StandardButton.Save
                | QDialogButtonBox.StandardButton.Cancel
            )
            buttons.accepted.connect(self._save)
            buttons.rejected.connect(self.reject)
        else:
            # Nothing to save: this service is signed in through its own tool.
            buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Close)
            buttons.rejected.connect(self.reject)
            buttons.accepted.connect(self.reject)
        layout.addWidget(buttons)

    # -- sections ---------------------------------------------------------

    def _detected_section(self, detection: Detection) -> QWidget:
        box = QFrame()
        box.setObjectName("card")
        box.setStyleSheet(
            f"QFrame#card {{ background-color: {self.theme.surface};"
            f" border: 1px solid {self.theme.grid}; border-radius: 8px; }}"
        )
        layout = QVBoxLayout(box)
        layout.setContentsMargins(14, 12, 14, 12)
        layout.setSpacing(6)

        heading = QLabel("Found on this machine")
        heading.setStyleSheet(
            f"color: {self.theme.ink}; font-size: 12px; font-weight: 600;"
        )
        layout.addWidget(heading)

        glyph = _STATE_GLYPH.get(detection.state, "–")
        if detection.state == MISSING:
            line = "No existing sign-in found for this service."
        else:
            who = f" — {detection.account}" if detection.account else ""
            line = f"{glyph}  {detection.source_label}{who}  ·  {detection.word}"
        status = QLabel(line)
        status.setWordWrap(True)
        status.setStyleSheet(f"color: {self.theme.ink}; font-size: 12px;")
        layout.addWidget(status)

        if detection.hint:
            hint = QLabel(detection.hint)
            hint.setWordWrap(True)
            hint.setStyleSheet(
                f"color: {self.theme.ink_secondary}; font-size: 11px;"
            )
            layout.addWidget(hint)

        others = [
            label
            for source_id, label in detection.candidates
            if source_id != detection.source_id
        ]
        if others:
            extra = QLabel("Also present: " + ", ".join(others))
            extra.setWordWrap(True)
            extra.setStyleSheet(f"color: {self.theme.ink_muted}; font-size: 11px;")
            layout.addWidget(extra)
        return box

    def _credentials_section(self) -> QWidget:
        holder = QWidget()
        form = QFormLayout(holder)
        form.setContentsMargins(0, 0, 0, 0)
        form.setSpacing(8)

        existing = self.settings.provider_key(self.provider.id)
        field = QLineEdit()
        field.setEchoMode(QLineEdit.EchoMode.Password)
        field.setPlaceholderText(
            secrets.mask(existing) if existing else self.provider.key_placeholder
        )
        self._key_field = field

        reveal = QPushButton("Show")
        reveal.setCheckable(True)
        reveal.setFixedWidth(56)
        reveal.toggled.connect(
            lambda on: field.setEchoMode(
                QLineEdit.EchoMode.Normal if on else QLineEdit.EchoMode.Password
            )
        )
        clear = QPushButton("Clear")
        clear.setFixedWidth(56)
        clear.clicked.connect(self._clear_key)

        row = QWidget()
        line = QHBoxLayout(row)
        line.setContentsMargins(0, 0, 0, 0)
        line.setSpacing(6)
        line.addWidget(field, 1)
        # A file path deserves a picker; a pasted key does not.
        if "json" in self.provider.key_placeholder.lower():
            browse = QPushButton("Browse…")
            browse.setFixedWidth(74)
            browse.clicked.connect(self._browse)
            line.addWidget(browse)
        line.addWidget(reveal)
        line.addWidget(clear)
        form.addRow(self.provider.key_label, row)

        if self.provider.extra_label:
            extra = QLineEdit(self.settings.provider_extra(self.provider.id))
            extra.setPlaceholderText(self.provider.extra_placeholder)
            self._extra_field = extra
            form.addRow(self.provider.extra_label, extra)
        return holder

    # -- actions ----------------------------------------------------------

    def _browse(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self, "Select service account JSON", "", "JSON files (*.json)"
        )
        if path and self._key_field is not None:
            self._key_field.setEchoMode(QLineEdit.EchoMode.Normal)
            self._key_field.setText(path)

    def _clear_key(self) -> None:
        self.settings.set_provider_key(self.provider.id, "")
        if self._key_field is not None:
            self._key_field.clear()
            self._key_field.setPlaceholderText("(cleared)")

    def _save(self) -> None:
        if self._key_field is not None:
            typed = self._key_field.text().strip().strip('"')
            if typed:
                # Blank means "leave the stored key alone".
                self.settings.set_provider_key(self.provider.id, typed)
        if self._extra_field is not None:
            self.settings.set_provider_extra(
                self.provider.id, self._extra_field.text().strip()
            )
        self.accept()
