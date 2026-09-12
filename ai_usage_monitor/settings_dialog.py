"""App preferences: which services to monitor, and widget appearance.

Credentials deliberately do not live here. Signing in to any service goes
through the Connections page and its one shared dialog, so there is a
single place - and a single UI - for every AI.
"""

from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFormLayout,
    QGroupBox,
    QLabel,
    QScrollArea,
    QVBoxLayout,
    QWidget,
)

from . import secrets, startup
from .providers import Provider
from .settings import (
    DEFAULT_WINDOW_SIZE,
    Settings,
    THEME_OPTIONS,
    WINDOW_SIZE_OPTIONS,
)
from .theme import Theme

class ProviderSettingsDialog(QDialog):
    """Per-service enablement and widget appearance."""

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
        self.setMinimumWidth(580)
        self.resize(600, 720)

        outer = QVBoxLayout(self)
        outer.setSpacing(12)

        # The dialog now carries General + one group per service, which
        # overflows a short screen; scroll rather than grow off the desktop.
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QScrollArea.Shape.NoFrame)
        body = QWidget()
        scroll.setWidget(body)
        layout = QVBoxLayout(body)
        layout.setContentsMargins(0, 0, 8, 0)
        layout.setSpacing(12)
        outer.addWidget(scroll, 1)

        if not secrets.available():
            warning = QLabel(
                "⚠ Encrypted key storage needs Windows DPAPI. Keys cannot be "
                "saved on this platform."
            )
            warning.setWordWrap(True)
            layout.addWidget(warning)

        layout.addWidget(self._build_general_group())

        for provider in providers:
            layout.addWidget(self._build_provider_group(provider))

        layout.addWidget(self._build_appearance_group(theme))
        layout.addStretch(1)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Save
            | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self._save)
        buttons.rejected.connect(self.reject)
        outer.addWidget(buttons)

        if focus_provider and focus_provider in self._rows:
            field = self._rows[focus_provider].get("key")
            if field is not None:
                field.setFocus()

    def _build_general_group(self) -> QGroupBox:
        box = QGroupBox("General")
        form = QFormLayout(box)
        form.setSpacing(8)

        self.start_with_windows = QCheckBox("Start with Windows")
        self.start_with_windows.setChecked(self.settings.start_with_windows)
        if not startup.supported():
            self.start_with_windows.setEnabled(False)
            self.start_with_windows.setToolTip("Windows only.")
        else:
            self.start_with_windows.setToolTip(
                "Adds a per-user entry under Run. Windows' own Startup Apps "
                "page edits the same entry, and whatever you set there wins on "
                "the next launch."
            )
        form.addRow(self.start_with_windows)

        self.minimize_to_tray = QCheckBox("Minimize to tray when closed")
        self.minimize_to_tray.setChecked(self.settings.minimize_to_tray)
        self.minimize_to_tray.setToolTip(
            "Closing the window parks it in the notification area and keeps "
            "watching. Exit in the tray menu quits for real."
        )
        form.addRow(self.minimize_to_tray)

        self.theme_combo = QComboBox()
        for label, _ in THEME_OPTIONS:
            self.theme_combo.addItem(label)
        self.theme_combo.setCurrentIndex(
            next(
                (i for i, (_, v) in enumerate(THEME_OPTIONS) if v == self.settings.theme),
                0,
            )
        )
        form.addRow("Theme", self.theme_combo)

        self.window_size_combo = QComboBox()
        for label, _ in WINDOW_SIZE_OPTIONS:
            self.window_size_combo.addItem(label)
        current = self.settings.window_size
        self.window_size_combo.setCurrentIndex(
            next(
                (i for i, (_, v) in enumerate(WINDOW_SIZE_OPTIONS) if v == current),
                next(
                    i
                    for i, (_, v) in enumerate(WINDOW_SIZE_OPTIONS)
                    if v == DEFAULT_WINDOW_SIZE
                ),
            )
        )
        form.addRow("Default window size", self.window_size_combo)

        note = QLabel(
            "The default size applies the next time the full window opens. "
            "\"Remember last size\" keeps whatever you dragged it to."
        )
        note.setWordWrap(True)
        note.setStyleSheet("font-size: 11px;")
        form.addRow(note)
        return box

    def _build_provider_group(self, provider: Provider) -> QGroupBox:
        """Enablement only. Credentials live on the Connections page, so there
        is exactly one place - and one UI - for signing in to any service."""
        box = QGroupBox(provider.display_name)
        form = QFormLayout(box)
        form.setSpacing(8)
        row: dict = {}

        enabled = QCheckBox("Monitor this service")
        enabled.setChecked(self.settings.provider_enabled(provider.id))
        form.addRow(enabled)
        row["enabled"] = enabled

        hint = QLabel(
            provider.tagline
            or "Sign-in is managed on the Connections page."
        )
        hint.setWordWrap(True)
        hint.setStyleSheet("font-size: 11px;")
        form.addRow(hint)

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
            self.settings.set_provider_enabled(provider.id, row["enabled"].isChecked())

        self.settings.start_with_windows = self.start_with_windows.isChecked()
        self.settings.minimize_to_tray = self.minimize_to_tray.isChecked()
        self.settings.theme = THEME_OPTIONS[self.theme_combo.currentIndex()][1]
        self.settings.window_size = WINDOW_SIZE_OPTIONS[
            self.window_size_combo.currentIndex()
        ][1]

        # An explicit choice in this dialog goes straight to the registry;
        # `reconcile` at launch only ever defers to what it finds there.
        startup.apply(self.settings.start_with_windows)

        self.settings.always_on_top = self.on_top.isChecked()
        self.settings.opacity = self.opacity.value()
        self.changed.emit()
        self.accept()
