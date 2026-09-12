"""The first page after start: every AI service, detected and rendered alike.

One `ConnectionCard` class draws every service. Nothing here branches on which
provider it is - the badge letter, tagline, status word and buttons all come
from provider metadata and the `Detection` result - so Claude, OpenAI and
Gemini are literally the same widget with different data.

Status is never colour alone: each card pairs its chip colour with the state
word ("Connected", "Limited", "Expired", "Not connected").
"""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QFont, QPainter
from PySide6.QtWidgets import (
    QCheckBox,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from .detection import CONNECTED, EXPIRED, MISSING, PARTIAL, Detection
from .providers import Provider
from .theme import Theme, qcolor
from .widgets.cards import Card

# Status colour per state. Paired with the state word on every card.
_STATE_STATUS = {
    CONNECTED: "good",
    PARTIAL: "warning",
    EXPIRED: "serious",
    MISSING: "",  # muted ink; absence is not an error
}


class _Badge(QWidget):
    """The round service initial - the only per-service decoration."""

    def __init__(
        self, letter: str, colour: str, theme: Theme, parent: QWidget | None = None
    ) -> None:
        super().__init__(parent)
        self.letter = letter
        self.colour = colour
        self.theme = theme
        self.setFixedSize(38, 38)

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(qcolor(self.colour, 0.16))
        painter.drawEllipse(self.rect())
        font = QFont(self.font())
        font.setPointSizeF(14.0)
        font.setWeight(QFont.Weight.DemiBold)
        painter.setFont(font)
        painter.setPen(qcolor(self.colour))
        painter.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, self.letter)
        painter.end()


class ConnectionCard(Card):
    """One service. Identical structure for every provider."""

    connect_requested = Signal(str)
    redetect_requested = Signal(str)

    def __init__(
        self,
        provider: Provider,
        colour: str,
        theme: Theme,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(theme, parent)
        self.provider = provider
        self.colour = colour
        self.detection: Detection | None = None
        self.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Maximum)

        outer = QHBoxLayout(self)
        outer.setContentsMargins(16, 14, 16, 14)
        outer.setSpacing(14)

        self.badge = _Badge(provider.display_name[:1].upper(), colour, theme)
        outer.addWidget(self.badge, 0, Qt.AlignmentFlag.AlignTop)

        text = QVBoxLayout()
        text.setSpacing(2)
        top = QHBoxLayout()
        top.setSpacing(8)
        self.name = QLabel(provider.display_name)
        self.chip = QLabel("")
        top.addWidget(self.name)
        top.addWidget(self.chip)
        top.addStretch(1)
        text.addLayout(top)

        self.account = QLabel("")
        self.account.setWordWrap(True)
        self.source = QLabel("")
        self.source.setWordWrap(True)
        text.addWidget(self.account)
        text.addWidget(self.source)
        outer.addLayout(text, 1)

        actions = QVBoxLayout()
        actions.setSpacing(6)
        self.connect_button = QPushButton("Connect...")
        self.connect_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.connect_button.clicked.connect(
            lambda: self.connect_requested.emit(provider.id)
        )
        self.redetect_button = QPushButton("Re-detect")
        self.redetect_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.redetect_button.clicked.connect(
            lambda: self.redetect_requested.emit(provider.id)
        )
        for button in (self.connect_button, self.redetect_button):
            button.setFixedWidth(112)
            actions.addWidget(button)
        actions.addStretch(1)
        outer.addLayout(actions, 0)

        self.apply_theme(theme)

    def set_detection(self, detection: Detection) -> None:
        self.detection = detection
        self.chip.setText(detection.word)

        if detection.account:
            self.account.setText(detection.account)
        elif detection.state == MISSING:
            self.account.setText("Not signed in")
        else:
            self.account.setText(self.provider.tagline or "")

        parts = []
        if detection.source_label:
            parts.append(f"Detected from {detection.source_label}")
        if detection.hint:
            parts.append(detection.hint)
        self.source.setText("  -  ".join(parts) if parts else self.provider.tagline)

        # Every service shows the same button. Services you sign in to
        # elsewhere (Claude Code) open the same dialog, which explains how
        # instead of offering a field - the shape stays identical.
        if not self.provider.needs_key:
            self.connect_button.setText("Sign-in help")
        else:
            self.connect_button.setText(
                "Change..." if detection.state == CONNECTED else "Connect..."
            )
        self._restyle()

    def apply_theme(self, theme: Theme) -> None:
        super().apply_theme(theme)
        self.theme = theme
        if not hasattr(self, "name"):
            return
        self.name.setStyleSheet(
            f"color: {theme.ink}; font-size: 14px; font-weight: 600;"
        )
        self.account.setStyleSheet(f"color: {theme.ink_secondary}; font-size: 12px;")
        self.source.setStyleSheet(f"color: {theme.ink_muted}; font-size: 11px;")
        self.badge.apply_theme(theme)
        self._restyle()

    def _restyle(self) -> None:
        state = self.detection.state if self.detection else MISSING
        status = _STATE_STATUS.get(state, "")
        colour = Theme.status(status) if status else self.theme.ink_muted
        tint = qcolor(colour)
        self.chip.setStyleSheet(
            f"color: {colour}; font-size: 11px; font-weight: 600;"
            f" background-color: rgba({tint.red()}, {tint.green()},"
            f" {tint.blue()}, 0.14);"
            f" border-radius: 8px; padding: 2px 8px;"
        )


class ConnectionsPage(QWidget):
    """The landing page: all services, one detection pass, one look."""

    connect_requested = Signal(str)
    redetect_requested = Signal(str)
    continue_requested = Signal()
    startup_pref_changed = Signal(bool)

    def __init__(
        self, providers: list[Provider], theme: Theme, parent: QWidget | None = None
    ) -> None:
        super().__init__(parent)
        self.theme = theme
        self.cards: dict[str, ConnectionCard] = {}

        layout = QVBoxLayout(self)
        layout.setContentsMargins(28, 24, 28, 20)
        layout.setSpacing(14)

        self.title = QLabel("Your AI services")
        self.subtitle = QLabel(
            "Each service is detected the same way: this app looks for a sign-in "
            "you already have - a CLI login, an environment variable, or a key "
            "saved here - and never writes to another tool's credentials."
        )
        self.subtitle.setWordWrap(True)
        layout.addWidget(self.title)
        layout.addWidget(self.subtitle)
        layout.addSpacing(4)

        for index, provider in enumerate(providers):
            card = ConnectionCard(provider, theme.series(index), theme)
            card.connect_requested.connect(self.connect_requested)
            card.redetect_requested.connect(self.redetect_requested)
            self.cards[provider.id] = card
            layout.addWidget(card)

        layout.addStretch(1)

        footer = QHBoxLayout()
        footer.setSpacing(10)
        self.startup_check = QCheckBox("Show this page at startup")
        self.startup_check.toggled.connect(self.startup_pref_changed)
        footer.addWidget(self.startup_check)
        footer.addStretch(1)

        self.refresh_all = QPushButton("Re-detect all")
        self.refresh_all.setCursor(Qt.CursorShape.PointingHandCursor)
        self.refresh_all.clicked.connect(lambda: self.redetect_requested.emit(""))
        footer.addWidget(self.refresh_all)

        self.continue_button = QPushButton("Open dashboard")
        self.continue_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.continue_button.setProperty("accent", True)
        self.continue_button.clicked.connect(self.continue_requested)
        footer.addWidget(self.continue_button)
        layout.addLayout(footer)

        self.apply_theme(theme)

    def set_detections(self, detections: dict[str, Detection]) -> None:
        for provider_id, card in self.cards.items():
            detection = detections.get(provider_id)
            if detection is not None:
                card.set_detection(detection)

    def set_startup_preference(self, show: bool) -> None:
        self.startup_check.blockSignals(True)
        self.startup_check.setChecked(show)
        self.startup_check.blockSignals(False)

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.title.setStyleSheet(
            f"color: {theme.ink}; font-size: 20px; font-weight: 600;"
        )
        self.subtitle.setStyleSheet(
            f"color: {theme.ink_secondary}; font-size: 12px;"
        )
        for card in self.cards.values():
            card.apply_theme(theme)
