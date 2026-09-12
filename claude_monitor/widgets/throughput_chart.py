"""Throughput trace: average output tokens/sec, one point per interval.

Drawn stepped rather than smoothed, because each point *is* a flat interval
average - joining them with a slope would imply readings the app never took.
Marks follow the same rules as the other charts: a 2px line, hairline solid
gridlines one step off the surface, one 8px head marker with a 2px surface
ring, and a single y-axis. The peak reference line wears the orange
categorical slot rather than a dash, so it can never read as a gridline.
"""

from __future__ import annotations

import math

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QFontMetrics, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QSizePolicy, QToolTip, QWidget

from .. import formatting
from ..theme import Theme, qcolor
from ..throughput import Sample

GRID_DIVISIONS = 4
AREA_OPACITY = 0.10


def _nice_axis_max(value: float, divisions: int) -> float:
    """An axis maximum whose divisions land on round tick values."""
    if value <= 0:
        return float(divisions)
    raw_step = value / divisions
    exponent = math.floor(math.log10(raw_step))
    base = 10.0**exponent
    for step in (1, 2, 2.5, 5, 10):
        if raw_step <= step * base:
            return step * base * divisions
    return 10 * base * divisions


class RateMeter(QWidget):
    """Current rate against the session's own peak.

    Scaling to the observed peak avoids inventing a ceiling: there is no
    published maximum tokens/sec, so any fixed denominator would be a guess.
    The fill wears the accent, not a status colour - a fast rate is not a
    warning.
    """

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.current = 0.0
        self.peak = 0.0
        # Caption line + track + scale labels; measured rather than guessed so
        # the bottom labels are never clipped at a larger UI font.
        rows = QFontMetrics(self.font()).height() * 2
        self.setFixedHeight(int(rows + 10 + 10))
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)

    def set_values(self, current: float, peak: float) -> None:
        self.current = max(0.0, current)
        self.peak = max(0.0, peak)
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        metrics = QFontMetrics(self.font())

        fraction = (self.current / self.peak) if self.peak > 0 else 0.0
        fraction = max(0.0, min(1.0, fraction))

        painter.setPen(qcolor(self.theme.ink_muted))
        painter.drawText(
            QRectF(0, 0, self.width() * 0.6, metrics.height()),
            Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            "vs session peak",
        )
        painter.setPen(qcolor(self.theme.ink))
        painter.drawText(
            QRectF(self.width() * 0.6, 0, self.width() * 0.4, metrics.height()),
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
            f"{fraction * 100:.0f}%",
        )

        track_top = metrics.height() + 4
        track = QRectF(0, track_top, self.width(), 10)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(qcolor(self.theme.track))
        painter.drawRoundedRect(track, 5, 5)
        if fraction > 0:
            fill = QRectF(track)
            fill.setWidth(max(10.0, track.width() * fraction))
            painter.setBrush(qcolor(self.theme.accent))
            painter.drawRoundedRect(fill, 5, 5)

        painter.setPen(qcolor(self.theme.ink_muted))
        label_top = track_top + 13
        painter.drawText(
            QRectF(0, label_top, self.width() * 0.4, metrics.height()),
            Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            "0",
        )
        painter.drawText(
            QRectF(self.width() * 0.4, label_top, self.width() * 0.6, metrics.height()),
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
            f"peak {formatting.compact(self.peak)}" if self.peak else "peak —",
        )
        painter.end()


class ThroughputChart(QWidget):
    """Stepped area/line of per-interval token rates."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.samples: list[Sample] = []
        self.peak = 0.0
        self.interval_seconds = 180
        self.setMinimumHeight(150)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setMouseTracking(True)
        self._plot = QRectF()

    def set_data(
        self, samples: list[Sample], peak: float, interval_seconds: int
    ) -> None:
        self.samples = samples
        self.peak = peak
        self.interval_seconds = interval_seconds
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        metrics = QFontMetrics(self.font())

        if not self.samples:
            painter.setPen(qcolor(self.theme.ink_muted))
            minutes = max(1, self.interval_seconds // 60)
            painter.drawText(
                self.rect(),
                Qt.AlignmentFlag.AlignCenter,
                f"Collecting — first point in up to {minutes} min",
            )
            painter.end()
            return

        values = [s.tokens_per_second for s in self.samples]
        axis_max = _nice_axis_max(max(max(values), self.peak), GRID_DIVISIONS)
        ticks = [
            formatting.compact(axis_max * i / GRID_DIVISIONS)
            for i in range(GRID_DIVISIONS + 1)
        ]
        left = max(metrics.horizontalAdvance(t) for t in ticks) + 12
        bottom = metrics.height() + 6
        plot = QRectF(
            left,
            6.0,
            max(1.0, self.width() - left - 6),
            max(1.0, self.height() - bottom - 6),
        )
        self._plot = plot

        grid_pen = QPen(qcolor(self.theme.grid), 1)
        grid_pen.setCosmetic(True)
        for index in range(GRID_DIVISIONS + 1):
            y = plot.bottom() - plot.height() * index / GRID_DIVISIONS
            painter.setPen(
                QPen(qcolor(self.theme.baseline), 1) if index == 0 else grid_pen
            )
            painter.drawLine(QPointF(plot.left(), y), QPointF(plot.right(), y))
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(0, y - metrics.height() / 2, left - 8, metrics.height()),
                Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                ticks[index],
            )

        count = len(values)
        span = plot.width() / max(1, count - 1) if count > 1 else plot.width()

        def x_at(i: int) -> float:
            return plot.left() + span * i if count > 1 else plot.center().x()

        def y_at(v: float) -> float:
            return plot.bottom() - plot.height() * (min(v, axis_max) / axis_max)

        # Stepped path: hold each interval's value, then step to the next.
        path = QPainterPath()
        path.moveTo(x_at(0), y_at(values[0]))
        for i in range(1, count):
            mid = (x_at(i - 1) + x_at(i)) / 2
            path.lineTo(mid, y_at(values[i - 1]))
            path.lineTo(mid, y_at(values[i]))
            path.lineTo(x_at(i), y_at(values[i]))

        area = QPainterPath(path)
        area.lineTo(x_at(count - 1), plot.bottom())
        area.lineTo(x_at(0), plot.bottom())
        area.closeSubpath()
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(qcolor(self.theme.accent, AREA_OPACITY))
        painter.drawPath(area)

        line = QPen(qcolor(self.theme.accent), 2)
        line.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        line.setCapStyle(Qt.PenCapStyle.RoundCap)
        painter.setPen(line)
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(path)

        # Peak reference: colour, not a dash, so it never reads as a gridline.
        if self.peak > 0:
            peak_y = y_at(self.peak)
            painter.setPen(QPen(qcolor(self.theme.series(1)), 1))
            painter.drawLine(
                QPointF(plot.left(), peak_y), QPointF(plot.right(), peak_y)
            )
            painter.drawText(
                QRectF(plot.left() + 5, peak_y - metrics.height() - 1, plot.width(), metrics.height()),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                f"peak {formatting.compact(self.peak)} tok/s",
            )

        # Head marker: the one point that gets a dot.
        painter.setPen(QPen(qcolor(self.theme.surface), 2))
        painter.setBrush(qcolor(self.theme.accent))
        painter.drawEllipse(QPointF(x_at(count - 1), y_at(values[-1])), 4.0, 4.0)

        painter.setPen(qcolor(self.theme.ink_muted))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        # Span from the real timestamps: a bucket runs long when the app was in
        # widget mode or the machine slept, so count x interval would misstate it.
        covered = sum(s.seconds for s in self.samples)
        painter.drawText(
            QRectF(plot.left(), plot.bottom() + 3, plot.width() / 2, metrics.height()),
            Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            f"−{covered / 60:.0f} min",
        )
        painter.drawText(
            QRectF(plot.center().x(), plot.bottom() + 3, plot.width() / 2, metrics.height()),
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
            "now",
        )
        painter.end()

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if not self.samples or self._plot.width() <= 0:
            return
        count = len(self.samples)
        span = self._plot.width() / max(1, count - 1) if count > 1 else self._plot.width()
        offset = event.position().x() - self._plot.left()
        index = int(round(offset / span)) if span > 0 else 0
        if 0 <= index < count and self._plot.contains(event.position()):
            sample = self.samples[index]
            minutes = sample.seconds / 60
            QToolTip.showText(
                event.globalPosition().toPoint(),
                f"<b>{formatting.local_time(sample.at)}</b><br>"
                f"{formatting.compact(sample.tokens_per_second)} tok/s<br>"
                f"{formatting.compact(sample.tokens)} tokens over {minutes:.0f} min",
                self,
            )
            return
        QToolTip.hideText()
