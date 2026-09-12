"""Provider credential and appearance settings."""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from . import secrets
from .providers import Provider
from .settings import Settings
from .theme import Theme

_EXTRA_LABELS = {
    "openai": ("Monthly budget (USD, optional)", "e.g. 50 — a local target, not an OpenAI limit"),
    "gemini": ("Google Cloud project id", "defaults to the project in the key file"),
}


class ProviderSettingsDialog(QDialog):
    """Edit API keys, per-provider enablement, and widget appearance."""

    changed = Signal()

    def __init__(
        self,
        providers: list[Provider],
        settings: Settings,
        theme: Theme,
        focus_provider: str = "",
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self.settings = settings
        self.providers = providers
        self._rows: dict[str, dict] = {}

        self.setWindowTitle("Settings")
        self.setMinimumWidth(560)

        layout = QVBoxLayout(self)
        layout.setSpacing(12)

        if not secrets.available():
            warning = QLabel(
                "⚠ Encrypted key storage needs Windows DPAPI. Keys cannot be "
                "saved on this platform."
            )
            warning.setWordWrap(True)
            layout.addWidget(warning)

        for provider in providers:
            layout.addWidget(self._build_provider_group(provider))

        layout.addWidget(self._build_appearance_group(theme))

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Save
            | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self._save)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

        if focus_provider and focus_provider in self._rows:
            field = self._rows[focus_provider].get("key")
            if field is not None:
                field.setFocus()

    def _build_provider_group(self, provider: Provider) -> QGroupBox:
        box = QGroupBox(provider.display_name)
        form = QFormLayout(box)
        form.setSpacing(8)
        row: dict = {}

        enabled = QCheckBox("Monitor this service")
        enabled.setChecked(self.settings.provider_enabled(provider.id))
        form.addRow(enabled)
        row["enabled"] = enabled

        hint = QLabel(provider.setup_hint)
        hint.setWordWrap(True)
        hint.setTextFormat(Qt.TextFormat.RichText)
        hint.setStyleSheet("font-size: 11px;")
        form.addRow(hint)

        if provider.needs_key:
            existing = self.settings.provider_key(provider.id)
            field = QLineEdit()
            field.setEchoMode(QLineEdit.EchoMode.Password)
            field.setPlaceholderText(
                secrets.mask(existing) if existing else provider.key_placeholder
            )
            row["key"] = field
            row["existing"] = existing

            reveal = QPushButton("Show")
            reveal.setCheckable(True)
            reveal.setFixedWidth(58)
            reveal.toggled.connect(
                lambda on, f=field: f.setEchoMode(
                    QLineEdit.EchoMode.Normal if on else QLineEdit.EchoMode.Password
                )
            )
            clear = QPushButton("Clear")
            clear.setFixedWidth(58)
            clear.clicked.connect(lambda _=False, p=provider.id: self._clear(p))

            holder = QWidget()
            line = QHBoxLayout(holder)
            line.setContentsMargins(0, 0, 0, 0)
            line.setSpacing(6)
            line.addWidget(field, 1)
            line.addWidget(reveal)
            line.addWidget(clear)
            form.addRow(provider.key_label, holder)

            if provider.id in _EXTRA_LABELS:
                label, placeholder = _EXTRA_LABELS[provider.id]
                extra = QLineEdit(self.settings.provider_extra(provider.id))
                extra.setPlaceholderText(placeholder)
                form.addRow(label, extra)
                row["extra"] = extra

        self._rows[provider.id] = row
        return box

    def _build_appearance_group(self, theme: Theme) -> QGroupBox:
        box = QGroupBox("Widget mode")
        form = QFormLayout(box)
        form.setSpacing(8)

        note = QLabel(
            "Shrink the window below 380px on either edge to collapse into the "
            "compact widget. Double-click the widget to expand it again."
        )
        note.setWordWrap(True)
        note.setStyleSheet("font-size: 11px;")
        form.addRow(note)

        self.on_top = QCheckBox("Always on top")
        self.on_top.setChecked(self.settings.always_on_top)
        form.addRow(self.on_top)

        self.opacity = QDoubleSpinBox()
        self.opacity.setRange(0.25, 1.0)
        self.opacity.setSingleStep(0.05)
        self.opacity.setDecimals(2)
        self.opacity.setValue(self.settings.opacity)
        form.addRow("Opacity", self.opacity)
        return box

    def _clear(self, provider_id: str) -> None:
        self.settings.set_provider_key(provider_id, "")
        row = self._rows.get(provider_id, {})
        row["existing"] = ""
        field = row.get("key")
        if field is not None:
            field.clear()
            field.setPlaceholderText("(cleared)")

    def _save(self) -> None:
        for provider in self.providers:
            row = self._rows.get(provider.id, {})
            self.settings.set_provider_enabled(
                provider.id, row["enabled"].isChecked()
            )
            field = row.get("key")
            if field is not None:
                typed = field.text().strip()
                if typed:
                    # Blank means "leave the stored key alone".
                    self.settings.set_provider_key(provider.id, typed)
            extra = row.get("extra")
            if extra is not None:
                self.settings.set_provider_extra(provider.id, extra.text().strip())

        self.settings.always_on_top = self.on_top.isChecked()
        self.settings.opacity = self.opacity.value()
        self.changed.emit()
        self.accept()
