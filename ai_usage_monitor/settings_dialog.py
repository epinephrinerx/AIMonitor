"""App preferences: which services to monitor, and widget appearance.

Credentials deliberately do not live here. Signing in to any service goes
through the Connections page and its one shared dialog, so there is a
single place - and a single UI - for every AI.

Appearance settings preview live. Theme, opacity, always-on-top and the
default window size are applied to the real window the moment you change
them, because judging any of them from a combo box label is guesswork.
Cancel puts back every value the dialog found on the way in, so a live
preview is never a decision you are stuck with; Save is what writes to
the registry.
"""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFormLayout,
    QGraphicsOpacityEffect,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QScrollArea,
    QSlider,
    QVBoxLayout,
    QWidget,
)

from . import secrets, startup
from .providers import Provider
from .settings import (
    DEFAULT_WINDOW_SIZE,
    OPACITY_MAX,
    OPACITY_MIN,
    OPACITY_STEP,
    Settings,
    THEME_OPTIONS,
    WINDOW_SIZE_OPTIONS,
)
from .theme import Theme

class ProviderSettingsDialog(QDialog):
    """Per-service enablement and widget appearance."""

    changed = Signal()
    # Emitted whenever a live-previewed appearance value has been written to
    # settings and the window should re-read it. Carries nothing: the window
    # reads the same `Settings` object this dialog writes.
    preview = Signal()

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
        self._theme = theme
        self._rows: dict[str, dict] = {}
        # Everything the dialog is allowed to change before Save, captured so
        # Cancel can put it back exactly.
        self._entry = {
            "theme": settings.theme,
            "opacity": settings.opacity,
            "always_on_top": settings.always_on_top,
            "window_size": settings.window_size,
        }
        self._saved = False

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
        self.theme_combo.currentIndexChanged.connect(self._preview_theme)
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
        self.window_size_combo.currentIndexChanged.connect(self._preview_window_size)
        form.addRow("Default window size", self.window_size_combo)

        note = QLabel(
            "A size you pick here is applied to the window straight away. "
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
        self.on_top.toggled.connect(self._preview_on_top)
        form.addRow(self.on_top)

        self.opacity = QSlider(Qt.Orientation.Horizontal)
        self.opacity.setRange(OPACITY_MIN, OPACITY_MAX)
        self.opacity.setSingleStep(OPACITY_STEP)
        self.opacity.setPageStep(OPACITY_STEP * 2)
        self.opacity.setTickInterval(OPACITY_STEP * 5)
        self.opacity.setTickPosition(QSlider.TickPosition.TicksBelow)
        self.opacity.setValue(round(self.settings.opacity * 100))
        self.opacity.setCursor(Qt.CursorShape.PointingHandCursor)
        self.opacity.valueChanged.connect(self._preview_opacity)

        self.opacity_value = QLabel()
        self.opacity_value.setMinimumWidth(38)
        self.opacity_value.setAlignment(
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        )

        # The window itself only takes opacity in widget mode, so this swatch
        # is what shows the level while the dashboard is on screen. It fades
        # with the slider rather than describing the number in words.
        self.opacity_swatch = QLabel("Widget preview")
        self.opacity_swatch.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.opacity_swatch.setMinimumHeight(30)
        self._swatch_fade = QGraphicsOpacityEffect(self.opacity_swatch)
        self.opacity_swatch.setGraphicsEffect(self._swatch_fade)

        row = QHBoxLayout()
        row.setSpacing(10)
        row.addWidget(self.opacity, 1)
        row.addWidget(self.opacity_value, 0)
        form.addRow("Opacity", row)
        form.addRow("", self.opacity_swatch)
        self._sync_opacity_labels()
        return box

    # -- live preview -----------------------------------------------------

    def _opacity_fraction(self) -> float:
        return self.opacity.value() / 100.0

    def _sync_opacity_labels(self) -> None:
        fraction = self._opacity_fraction()
        self.opacity_value.setText(f"{self.opacity.value()}%")
        self._swatch_fade.setOpacity(fraction)
        self.opacity_swatch.setStyleSheet(
            f"background-color: {self._theme.surface};"
            f" color: {self._theme.ink_secondary};"
            f" border: 1px solid {self._theme.border};"
            " border-radius: 6px; font-size: 11px;"
        )

    def _preview_theme(self) -> None:
        self.settings.theme = THEME_OPTIONS[self.theme_combo.currentIndex()][1]
        self.preview.emit()

    def _preview_window_size(self) -> None:
        self.settings.window_size = WINDOW_SIZE_OPTIONS[
            self.window_size_combo.currentIndex()
        ][1]
        self.preview.emit()

    def _preview_on_top(self) -> None:
        self.settings.always_on_top = self.on_top.isChecked()
        self.preview.emit()

    def _preview_opacity(self) -> None:
        self._sync_opacity_labels()
        self.settings.opacity = self._opacity_fraction()
        self.preview.emit()

    def reject(self) -> None:
        """Cancel: undo every live preview, then close.

        Written through the same setters the preview used, so the window sees
        the restoration exactly as it saw each change.
        """
        if not self._saved:
            self.settings.theme = self._entry["theme"]
            self.settings.opacity = self._entry["opacity"]
            self.settings.always_on_top = self._entry["always_on_top"]
            self.settings.window_size = self._entry["window_size"]
            self.preview.emit()
        super().reject()

    # `_clear` used to live here, from when this dialog still held key fields.
    # Nothing has called it since credentials moved to the Connections page,
    # and it deleted the stored key the instant it ran - the same trap that
    # `ConnectDialog._clear_key` was just fixed for. Removed rather than left
    # as a working example of the wrong shape.

    def _save(self) -> None:
        self._saved = True
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
        self.settings.opacity = self._opacity_fraction()
        self.changed.emit()
        self.accept()
