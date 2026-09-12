"""Stacked column and horizontal bar charts, painted directly with QPainter.

Mark specs held to throughout: bars capped at 24px with a 4px rounded data-end
square at the baseline, hairline solid gridlines a step off the surface, a 2px
surface gap separating every touching fill, and text in ink tokens rather than
the series colour. A legend is drawn whenever two or more series are present.
"""

from __future__ import annotations

import math

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QFont, QFontMetrics, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QSizePolicy, QToolTip, QWidget

from .. import formatting
from ..theme import Theme, qcolor
from ..usage_log import DayBucket

BAR_MAX_THICKNESS = 24.0
DATA_END_RADIUS = 4.0
SURFACE_GAP = 2.0
GRID_DIVISIONS = 4


def _nice_axis_max(value: float, divisions: int) -> float:
    """An axis maximum whose divisions land on round tick values.

    Rounding the *step* rather than the maximum is what keeps the ticks clean:
    a peak of 7.1M over four divisions becomes 0 / 2M / 4M / 6M / 8M, not the
    0 / 1.9M / 3.8M / 5.6M / 7.5M that ceiling-then-divide produces.
    """
    if value <= 0:
        return float(divisions)
    raw_step = value / divisions
    exponent = math.floor(math.log10(raw_step))
    base = 10.0**exponent
    for step in (1, 2, 2.5, 5, 10):
        if raw_step <= step * base:
            return step * base * divisions
    return 10 * base * divisions


def _bar_path(rect: QRectF, radius: float, rounded_top: bool) -> QPainterPath:
    """A bar with an optionally rounded data-end and a square baseline."""
    path = QPainterPath()
    radius = min(radius, rect.width() / 2, rect.height())
    if not rounded_top or radius <= 0.5:
        path.addRect(rect)
        return path
    path.moveTo(rect.left(), rect.bottom())
    path.lineTo(rect.left(), rect.top() + radius)
    path.quadTo(rect.left(), rect.top(), rect.left() + radius, rect.top())
    path.lineTo(rect.right() - radius, rect.top())
    path.quadTo(rect.right(), rect.top(), rect.right(), rect.top() + radius)
    path.lineTo(rect.right(), rect.bottom())
    path.closeSubpath()
    return path


def _bar_path_horizontal(rect: QRectF, radius: float) -> QPainterPath:
    """A horizontal bar: rounded at the tip, square at the baseline."""
    path = QPainterPath()
    radius = min(radius, rect.height() / 2, rect.width())
    if radius <= 0.5:
        path.addRect(rect)
        return path
    path.moveTo(rect.left(), rect.top())
    path.lineTo(rect.right() - radius, rect.top())
    path.quadTo(rect.right(), rect.top(), rect.right(), rect.top() + radius)
    path.lineTo(rect.right(), rect.bottom() - radius)
    path.quadTo(rect.right(), rect.bottom(), rect.right() - radius, rect.bottom())
    path.lineTo(rect.left(), rect.bottom())
    path.closeSubpath()
    return path


class StackedColumnChart(QWidget):
    """Per-day usage, stacked by model."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.buckets: list[DayBucket] = []
        self.series: list[str] = []
        self.metric_name = "Total tokens"
        self._bands: list[tuple[float, float]] = []
        self.setMinimumHeight(220)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setMouseTracking(True)

    def set_data(
        self, buckets: list[DayBucket], series: list[str], metric: str
    ) -> None:
        self.buckets = buckets
        self.series = series
        self.metric_name = metric
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def _colour_for(self, name: str) -> str:
        if name == "Other":
            return self.theme.ink_muted
        try:
            return self.theme.series(self.series.index(name))
        except ValueError:
            return self.theme.accent

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setFont(self.font())
        metrics = QFontMetrics(self.font())

        if not self.buckets or not self.series:
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                self.rect(),
                Qt.AlignmentFlag.AlignCenter,
                "No usage recorded in this range",
            )
            painter.end()
            return

        legend_height = self._draw_legend(painter, metrics) if len(self.series) >= 2 else 0

        peak = max((bucket.total for bucket in self.buckets), default=0.0)
        axis_max = _nice_axis_max(peak, GRID_DIVISIONS)

        tick_texts = [
            formatting.axis_tick(axis_max * index / GRID_DIVISIONS, self.metric_name)
            for index in range(GRID_DIVISIONS + 1)
        ]
        left = max(metrics.horizontalAdvance(text) for text in tick_texts) + 12
        right = 6.0
        top = legend_height + 8.0
        bottom = metrics.height() + 10.0

        plot = QRectF(
            left, top, max(1.0, self.width() - left - right),
            max(1.0, self.height() - top - bottom),
        )

        # Gridlines: hairline, solid, one step off the surface.
        grid_pen = QPen(qcolor(self.theme.grid), 1)
        grid_pen.setCosmetic(True)
        for index in range(GRID_DIVISIONS + 1):
            y = plot.bottom() - plot.height() * index / GRID_DIVISIONS
            painter.setPen(
                QPen(qcolor(self.theme.baseline), 1)
                if index == 0
                else grid_pen
            )
            painter.drawLine(QPointF(plot.left(), y), QPointF(plot.right(), y))
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(0, y - metrics.height() / 2, left - 8, metrics.height()),
                Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                tick_texts[index],
            )

        band_width = plot.width() / len(self.buckets)
        thickness = max(3.0, min(BAR_MAX_THICKNESS, band_width - 4.0))
        self._bands = []

        for index, bucket in enumerate(self.buckets):
            centre = plot.left() + band_width * (index + 0.5)
            self._bands.append((centre - band_width / 2, centre + band_width / 2))
            if bucket.total <= 0:
                continue

            stack = [
                (name, bucket.per_model[name])
                for name in self.series
                if bucket.per_model.get(name, 0.0) > 0
            ]
            cursor = plot.bottom()
            for position, (name, value) in enumerate(stack):
                height = plot.height() * (value / axis_max)
                rect = QRectF(
                    centre - thickness / 2, cursor - height, thickness, height
                )
                # A 2px surface gap separates touching segments; the bottom-most
                # segment keeps its edge on the baseline.
                if position > 0 and rect.height() > SURFACE_GAP:
                    rect.setBottom(rect.bottom() - SURFACE_GAP)
                cursor -= height
                if rect.height() <= 0.4:
                    continue
                painter.setPen(Qt.PenStyle.NoPen)
                painter.setBrush(qcolor(self._colour_for(name)))
                painter.drawPath(
                    _bar_path(
                        rect, DATA_END_RADIUS, rounded_top=(position == len(stack) - 1)
                    )
                )

        # X labels: thinned so they never collide.
        painter.setPen(qcolor(self.theme.ink_muted))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        sample = formatting.day_label(self.buckets[0].day)
        needed = metrics.horizontalAdvance(sample) + 14
        stride = max(1, math.ceil(needed / max(band_width, 1.0)))
        for index, bucket in enumerate(self.buckets):
            if index % stride and index != len(self.buckets) - 1:
                continue
            centre = plot.left() + band_width * (index + 0.5)
            painter.drawText(
                QRectF(centre - band_width * stride / 2, plot.bottom() + 4,
                       band_width * stride, metrics.height()),
                Qt.AlignmentFlag.AlignCenter,
                formatting.day_label(bucket.day),
            )
        painter.end()

    def _draw_legend(self, painter: QPainter, metrics: QFontMetrics) -> float:
        """Swatch + ink label per series; identity is never colour-alone."""
        x = 0.0
        y = 4.0
        row_height = metrics.height() + 4
        swatch = 9.0
        for name in self.series:
            width = swatch + 6 + metrics.horizontalAdvance(name) + 16
            if x + width > self.width() and x > 0:
                x = 0.0
                y += row_height
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(qcolor(self._colour_for(name)))
            painter.drawRoundedRect(
                QRectF(x, y + (metrics.height() - swatch) / 2, swatch, swatch), 2, 2
            )
            painter.setPen(qcolor(self.theme.ink_secondary))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawText(
                QRectF(x + swatch + 6, y, width - swatch - 6, metrics.height()),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                name,
            )
            x += width
        return y + row_height

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        position = event.position()
        for index, (start, end) in enumerate(self._bands):
            if start <= position.x() <= end and index < len(self.buckets):
                bucket = self.buckets[index]
                lines = [f"<b>{formatting.day_label(bucket.day)}</b>"]
                if bucket.total <= 0:
                    lines.append("No usage")
                else:
                    for name in self.series:
                        value = bucket.per_model.get(name, 0.0)
                        if value > 0:
                            lines.append(
                                f"{name}: "
                                f"{formatting.metric_label(value, self.metric_name)}"
                            )
                    lines.append(
                        f"<b>Total: "
                        f"{formatting.metric_label(bucket.total, self.metric_name)}</b>"
                    )
                QToolTip.showText(event.globalPosition().toPoint(), "<br>".join(lines), self)
                return
        QToolTip.hideText()


class HorizontalBarChart(QWidget):
    """Ranked breakdown with the value labelled at each bar's tip."""

    ROW_HEIGHT = 30.0
    BAR_THICKNESS = 14.0

    def __init__(
        self, theme: Theme, coloured: bool = False, parent: QWidget | None = None
    ) -> None:
        super().__init__(parent)
        self.theme = theme
        self.rows: list[tuple[str, float]] = []
        self.metric_name = "Total tokens"
        self.coloured = coloured
        self.max_rows = 8
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        self.setMinimumHeight(int(self.ROW_HEIGHT * 3))

    def set_data(self, rows: list[tuple[str, float]], metric: str) -> None:
        rows = list(rows)
        if len(rows) > self.max_rows:
            spill = sum(value for _, value in rows[self.max_rows - 1 :])
            rows = rows[: self.max_rows - 1] + [("Other", spill)]
        self.rows = rows
        self.metric_name = metric
        self.setMinimumHeight(int(self.ROW_HEIGHT * max(len(rows), 1)) + 8)
        self.updateGeometry()
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        metrics = QFontMetrics(self.font())

        if not self.rows:
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                self.rect(), Qt.AlignmentFlag.AlignCenter, "Nothing recorded yet"
            )
            painter.end()
            return

        label_font = QFont(self.font())
        label_width = min(
            140.0,
            max(
                metrics.horizontalAdvance(name) for name, _ in self.rows
            )
            + 10.0,
        )
        value_texts = [
            formatting.metric_label(value, self.metric_name) for _, value in self.rows
        ]
        value_width = max(metrics.horizontalAdvance(text) for text in value_texts) + 10
        track_left = label_width + 10
        track_width = max(20.0, self.width() - track_left - value_width)
        peak = max(value for _, value in self.rows) or 1.0

        for index, (name, value) in enumerate(self.rows):
            y = index * self.ROW_HEIGHT
            centre = y + self.ROW_HEIGHT / 2

            painter.setFont(label_font)
            painter.setPen(qcolor(self.theme.ink_secondary))
            painter.drawText(
                QRectF(0, y, label_width, self.ROW_HEIGHT),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                metrics.elidedText(
                    name, Qt.TextElideMode.ElideRight, int(label_width)
                ),
            )

            width = track_width * (value / peak)
            colour = (
                self.theme.series(index)
                if self.coloured and name != "Other"
                else (self.theme.ink_muted if name == "Other" else self.theme.accent)
            )
            rect = QRectF(
                track_left,
                centre - self.BAR_THICKNESS / 2,
                max(2.0, width),
                self.BAR_THICKNESS,
            )
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(qcolor(colour))
            painter.drawPath(_bar_path_horizontal(rect, DATA_END_RADIUS))

            painter.setPen(qcolor(self.theme.ink))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawText(
                QRectF(rect.right() + 6, y, value_width, self.ROW_HEIGHT),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                value_texts[index],
            )
        painter.end()
