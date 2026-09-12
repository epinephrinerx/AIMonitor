"""Widget mode: the whole app inside 300x300 pixels.

Option A from the design canvas - a row of 270° radial arcs, the same meter
language as the dashboard gauges, shrunk. Everything the dashboard says is
dropped except how full each window is and when it resets.

Painted in a single `paintEvent` rather than assembled from child widgets: at
this size a layout of QLabels costs more memory and more layout passes than
drawing directly, and there is nothing here to interact with. The whole surface
is a drag handle (frameless windows have no title bar), double-click returns to
the dashboard, and right-click opens the mode menu.

Widget mode carries no throughput trace by design - it is the mode that skips
transcript parsing entirely to stay near 12 MB.
"""

from __future__ import annotations

from PySide6.QtCore import QPointF, QRectF, Qt, Signal
from PySide6.QtGui import QFont, QFontMetrics, QPainter, QPen
from PySide6.QtWidgets import QSizePolicy, QWidget

from .. import formatting
from ..providers import Meter, ProviderSnapshot
from ..theme import Theme, qcolor, severity_color

# The arc opens at the bottom: 270 degrees swept clockwise from lower-left.
START_ANGLE = 225
SWEEP = -270

ARC_MAX = 76.0
ARC_MIN = 44.0
GAP = 6.0


class CompactView(QWidget):
    """A row of radial meters plus a header naming the active provider."""

    expand_requested = Signal()
    menu_requested = Signal(QPointF)

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.title = "Claude"
        self.snapshot: ProviderSnapshot | None = None
        self.status = ""
        self._drag_origin: QPointF | None = None
        self.setMinimumSize(180, 120)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setCursor(Qt.CursorShape.SizeAllCursor)
        self.setToolTip(
            "Drag to move · double-click to expand · right-click for options"
        )

    def set_snapshot(
        self, title: str, snapshot: ProviderSnapshot | None, status: str
    ) -> None:
        self.title = title
        self.snapshot = snapshot
        self.status = status
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    # -- painting ---------------------------------------------------------

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt naming
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        frame = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(qcolor(self.theme.surface))
        painter.drawRoundedRect(frame, 10, 10)
        painter.setPen(QPen(qcolor(self.theme.border), 1))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawRoundedRect(frame, 10, 10)

        margin = 11.0
        width = self.width() - margin * 2
        y = margin

        header_font = QFont(self.font())
        header_font.setPointSizeF(9.5)
        header_font.setWeight(QFont.Weight.DemiBold)
        small_font = QFont(self.font())
        small_font.setPointSizeF(7.5)
        small_metrics = QFontMetrics(small_font)

        painter.setFont(header_font)
        header_metrics = QFontMetrics(header_font)
        painter.setPen(qcolor(self.theme.ink))
        painter.drawText(
            QRectF(margin, y, width, header_metrics.height()),
            Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            self.title,
        )
        y += header_metrics.height() + 5

        meters = list(self.snapshot.meters) if self.snapshot else []
        gauges = [m for m in meters if m.percent is not None]
        if not gauges:
            message = "Waiting for data…"
            if self.snapshot is not None and self.snapshot.error:
                message = self.snapshot.error
            elif self.snapshot is not None and not self.snapshot.configured:
                message = f"{self.title} is not configured yet."
            self._draw_message(painter, margin, y, width, message)
            painter.end()
            return

        footer_height = small_metrics.height() + 4 if self.status else 0
        alert = self._alert(gauges)
        alert_height = small_metrics.height() + 3 if alert else 0
        available_h = self.height() - y - margin - footer_height - alert_height

        # Fit as many arcs across as the width allows, then size them to
        # whichever of width or height binds. Meters that do not fit are
        # dropped rather than shrunk into illegibility.
        label_block = small_metrics.height() * 2 + 3
        count = len(gauges)
        arc = 0.0
        while count > 0:
            by_width = (width - GAP * (count - 1)) / count
            by_height = available_h - label_block
            arc = min(ARC_MAX, by_width, by_height)
            if arc >= ARC_MIN or count == 1:
                break
            count -= 1
        arc = max(28.0, arc)
        shown = gauges[:count]

        cell = (width - GAP * (len(shown) - 1)) / len(shown) if shown else width
        # Below this the centred figure no longer fits inside the ring, so the
        # percentage moves out to the caption instead of overprinting the arc.
        inline_value = arc >= 46.0
        value_font = QFont(self.font())
        value_font.setPointSizeF(max(6.0, arc * 0.20))
        value_font.setWeight(QFont.Weight.DemiBold)

        for index, meter in enumerate(shown):
            cx = margin + cell * index + GAP * index + cell / 2
            self._draw_arc(
                painter, cx, y, arc, meter, value_font, draw_value=inline_value
            )

            text_top = y + arc + 2
            painter.setFont(small_font)
            painter.setPen(qcolor(self.theme.ink))
            caption = meter.title
            if not inline_value:
                caption = f"{meter.title} {self._percent_text(meter)}"
            painter.drawText(
                QRectF(cx - cell / 2, text_top, cell, small_metrics.height()),
                Qt.AlignmentFlag.AlignCenter,
                small_metrics.elidedText(
                    caption, Qt.TextElideMode.ElideRight, int(cell)
                ),
            )
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(
                    cx - cell / 2,
                    text_top + small_metrics.height(),
                    cell,
                    small_metrics.height(),
                ),
                Qt.AlignmentFlag.AlignCenter,
                small_metrics.elidedText(
                    meter.subtitle, Qt.TextElideMode.ElideRight, int(cell)
                ),
            )

        cursor = y + arc + 2 + label_block + 3
        # Never let the alert line run under the footer.
        footer_top = self.height() - margin - (small_metrics.height() if self.status else 0)
        if cursor + small_metrics.height() > footer_top:
            alert = None

        if alert:
            glyph, text, colour = alert
            painter.setFont(small_font)
            painter.setPen(qcolor(colour))
            glyph_w = small_metrics.horizontalAdvance(glyph) + 4
            painter.drawText(
                QRectF(margin, cursor, glyph_w, small_metrics.height()),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                glyph,
            )
            painter.setPen(qcolor(self.theme.ink_secondary))
            painter.drawText(
                QRectF(
                    margin + glyph_w, cursor, width - glyph_w, small_metrics.height()
                ),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                small_metrics.elidedText(
                    text, Qt.TextElideMode.ElideRight, int(width - glyph_w)
                ),
            )

        if self.status:
            painter.setFont(small_font)
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(
                    margin,
                    self.height() - margin - small_metrics.height(),
                    width,
                    small_metrics.height(),
                ),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                small_metrics.elidedText(
                    self.status, Qt.TextElideMode.ElideRight, int(width)
                ),
            )
        painter.end()

    @staticmethod
    def _percent_text(meter: Meter) -> str:
        percent = max(0.0, min(100.0, meter.percent or 0.0))
        near_whole = abs(percent - round(percent)) < 0.05
        return f"{percent:.0f}%" if percent >= 10 or near_whole else f"{percent:.1f}%"

    def _draw_arc(
        self,
        painter: QPainter,
        cx: float,
        top: float,
        size: float,
        meter: Meter,
        value_font: QFont,
        draw_value: bool = True,
    ) -> None:
        thickness = max(4.0, size * 0.11)
        inset = thickness / 2 + 1
        box = QRectF(
            cx - size / 2 + inset, top + inset, size - inset * 2, size - inset * 2
        )

        track = QPen(qcolor(self.theme.track), thickness)
        track.setCapStyle(Qt.PenCapStyle.RoundCap)
        painter.setPen(track)
        painter.drawArc(box, START_ANGLE * 16, SWEEP * 16)

        percent = max(0.0, min(100.0, meter.percent or 0.0))
        if percent > 0:
            fill = QPen(qcolor(severity_color(self.theme, meter.severity)), thickness)
            fill.setCapStyle(Qt.PenCapStyle.RoundCap)
            painter.setPen(fill)
            painter.drawArc(box, START_ANGLE * 16, int(SWEEP * percent / 100.0 * 16))

        if not draw_value:
            return
        painter.setFont(value_font)
        painter.setPen(qcolor(self.theme.ink))
        metrics = QFontMetrics(value_font)
        painter.drawText(
            QRectF(cx - size / 2, top + (size - metrics.height()) / 2, size, metrics.height()),
            Qt.AlignmentFlag.AlignCenter,
            self._percent_text(meter),
        )

    def _alert(self, gauges: list[Meter]) -> tuple[str, str, str] | None:
        """The single most pressing line: the worst window and its countdown."""
        ranked = sorted(gauges, key=lambda m: -(m.percent or 0.0))
        worst = ranked[0]
        remaining = worst.resets_in
        when = (
            f"resets in {formatting.duration(remaining)}"
            if remaining is not None and remaining.total_seconds() > 0
            else "no reset scheduled"
        )
        words = {
            "warning": "High",
            "serious": "Very high",
            "critical": "Critical",
        }
        if worst.severity in words:
            return "⚠", f"{worst.title} {words[worst.severity]} · {when}", Theme.status(
                worst.severity
            )
        return "", f"{worst.title} · {when}", self.theme.ink_secondary

    def _draw_message(
        self, painter: QPainter, x: float, y: float, width: float, text: str
    ) -> None:
        font = QFont(self.font())
        font.setPointSizeF(8.0)
        painter.setFont(font)
        painter.setPen(qcolor(self.theme.ink_muted))
        painter.drawText(
            QRectF(x, y, width, self.height() - y - 10),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignTop)
            | int(Qt.TextFlag.TextWordWrap),
            text,
        )

    # -- interaction ------------------------------------------------------

    def mousePressEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if event.button() == Qt.MouseButton.LeftButton:
            window = self.window()
            self._drag_origin = (
                event.globalPosition() - window.frameGeometry().topLeft().toPointF()
            )
            event.accept()

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if self._drag_origin is not None and event.buttons() & Qt.MouseButton.LeftButton:
            self.window().move((event.globalPosition() - self._drag_origin).toPoint())
            event.accept()

    def mouseReleaseEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self._drag_origin = None

    def mouseDoubleClickEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self.expand_requested.emit()
        event.accept()

    def contextMenuEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self.menu_requested.emit(QPointF(event.globalPos()))
        event.accept()
