"""Radial quota meter.

Meter contract: the fill carries severity while the unfilled track is a lighter
step of the same ramp, so the state reads across the whole arc. The severity is
always spelled out in words beside it - colour never carries it alone.

A provider that exposes no denominator (OpenAI spend, Gemini request counts)
returns `percent=None`. Rather than invent a percentage, the card drops the arc
and shows the figure on its own.
"""

from __future__ import annotations

from PySide6.QtCore import QRectF, QSize, Qt
from PySide6.QtGui import QFont, QPainter, QPen
from PySide6.QtWidgets import QLabel, QSizePolicy, QVBoxLayout, QWidget

from .. import formatting
from ..providers import Meter
from ..theme import Theme, qcolor, severity_color, severity_for
from .cards import Card

# The arc opens at the bottom: 270 degrees swept clockwise from lower-left.
_START_ANGLE = 225
_SWEEP = -270

_SEVERITY_WORDS = {
    "normal": ("✓", "Normal"),
    "warning": ("⚠", "High"),
    "serious": ("⚠", "Very high"),
    "critical": ("⚠", "Critical"),
}


class ArcMeter(QWidget):
    """The arc itself, with the percentage as the figure in its centre."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.percent = 0.0
        self.severity = "normal"
        self.compact = False
        self.setMinimumSize(96, 96)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)

    def set_value(self, percent: float, severity: str) -> None:
        self.percent = max(0.0, percent)
        self.severity = severity
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def sizeHint(self) -> QSize:  # noqa: N802 - Qt naming
        return QSize(96, 96) if self.compact else QSize(148, 148)

    def minimumSizeHint(self) -> QSize:  # noqa: N802 - Qt naming
        return QSize(56, 56) if self.compact else QSize(96, 96)

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        side = min(self.width(), self.height())
        thickness = max(6.0, side * 0.095)
        inset = thickness / 2 + 2
        box = QRectF(
            (self.width() - side) / 2 + inset,
            (self.height() - side) / 2 + inset,
            side - inset * 2,
            side - inset * 2,
        )

        track_pen = QPen(qcolor(self.theme.track), thickness)
        track_pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        painter.setPen(track_pen)
        painter.drawArc(box, _START_ANGLE * 16, _SWEEP * 16)

        fraction = min(self.percent, 100.0) / 100.0
        if fraction > 0:
            fill_pen = QPen(
                qcolor(severity_color(self.theme, self.severity)), thickness
            )
            fill_pen.setCapStyle(Qt.PenCapStyle.RoundCap)
            painter.setPen(fill_pen)
            painter.drawArc(box, _START_ANGLE * 16, int(_SWEEP * fraction * 16))

        value_font = QFont(self.font())
        value_font.setPointSizeF(max(11.0, side * 0.21))
        value_font.setWeight(QFont.Weight.DemiBold)
        painter.setFont(value_font)
        painter.setPen(qcolor(self.theme.ink))

        # A decimal only where it says something: below 10% and not a round number.
        near_whole = abs(self.percent - round(self.percent)) < 0.05
        text = (
            f"{self.percent:.0f}%"
            if self.percent >= 10 or near_whole
            else f"{self.percent:.1f}%"
        )
        metrics = painter.fontMetrics()
        painter.drawText(
            QRectF(0, (self.height() - metrics.height()) / 2, self.width(), metrics.height()),
            Qt.AlignmentFlag.AlignCenter,
            text,
        )
        painter.end()


class QuotaGauge(Card):
    """One usage window: title, arc (or figure), severity word, reset countdown."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(theme, parent)
        self.meter: Meter | None = None

        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 14, 16, 14)
        layout.setSpacing(4)

        self._title = QLabel("-")
        self._subtitle = QLabel("")
        self._arc = ArcMeter(theme)
        self._figure = QLabel("")
        self._figure.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._figure.setVisible(False)
        self._severity = QLabel("")
        self._reset = QLabel("")

        for widget in (self._severity, self._reset):
            widget.setAlignment(Qt.AlignmentFlag.AlignCenter)

        layout.addWidget(self._title)
        layout.addWidget(self._subtitle)
        layout.addSpacing(4)
        layout.addWidget(self._arc, 1)
        layout.addWidget(self._figure, 1)
        layout.addSpacing(2)
        layout.addWidget(self._severity)
        layout.addWidget(self._reset)

        self.apply_theme(theme)

    def set_meter(self, meter: Meter) -> None:
        self.meter = meter
        self._title.setText(meter.title)
        self._subtitle.setText(meter.subtitle)

        if meter.percent is None:
            # No server-side denominator: show the figure, not a fake arc.
            self._arc.setVisible(False)
            self._figure.setVisible(True)
            self._figure.setText(meter.detail or "-")
            self._severity.setVisible(False)
        else:
            self._arc.setVisible(True)
            self._figure.setVisible(bool(meter.detail))
            self._figure.setText(meter.detail)
            # The local 75/90 thresholds apply here too, taking whichever is
            # worse. The widget and the tray have always done this; the
            # dashboard used the server's word raw, so a service reporting
            # "normal" at 95% drew a calm arc on this page and a red one
            # everywhere else. Invisible while the fill was the accent hue;
            # not invisible once a healthy meter is green.
            severity = severity_for(meter.percent, meter.severity)
            self._arc.set_value(meter.percent, severity)
            self._severity.setVisible(True)
            glyph, word = _SEVERITY_WORDS.get(
                severity, ("", severity.title())
            )
            self._severity.setText(f"{glyph} {word}".strip())

        if meter.locked_reason:
            self._reset.setText(str(meter.locked_reason))
        elif meter.resets_at is None:
            self._reset.setText("No reset scheduled")
        else:
            remaining = meter.resets_in
            if remaining is None or remaining.total_seconds() <= 0:
                self._reset.setText("Resetting now")
            else:
                self._reset.setText(
                    f"Resets in {formatting.duration(remaining)}"
                    f"  ·  {formatting.local_time(meter.resets_at)}"
                )
        self._restyle_severity()

    def refresh_countdown(self) -> None:
        """Re-render the countdown so the window ticks between fetches."""
        if self.meter is not None:
            self.set_meter(self.meter)

    def apply_theme(self, theme: Theme) -> None:
        super().apply_theme(theme)
        self.theme = theme
        if not hasattr(self, "_title"):
            return
        self._title.setStyleSheet(
            f"color: {theme.ink}; font-size: 14px; font-weight: 600;"
        )
        self._subtitle.setStyleSheet(f"color: {theme.ink_muted}; font-size: 11px;")
        self._reset.setStyleSheet(f"color: {theme.ink_secondary}; font-size: 11px;")
        self._figure.setStyleSheet(
            f"color: {theme.ink}; font-size: 26px; font-weight: 600;"
        )
        self._arc.apply_theme(theme)
        self._restyle_severity()

    def _restyle_severity(self) -> None:
        severity = self.meter.severity if self.meter else "normal"
        colour = (
            severity_color(self.theme, severity)
            if severity != "normal"
            else self.theme.ink_secondary
        )
        self._severity.setStyleSheet(
            f"color: {colour}; font-size: 12px; font-weight: 600;"
        )
