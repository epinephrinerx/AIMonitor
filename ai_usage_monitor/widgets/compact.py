"""Widget mode: the whole app inside 300x300 pixels.

Option A from the design canvas - a row of 270° radial arcs, the same meter
language as the dashboard gauges, shrunk. Everything the dashboard says is
dropped except how full each window is and when it resets.

Painted in a single `paintEvent` rather than assembled from child widgets: at
this size a layout of QLabels costs more memory and more layout passes than
drawing directly, and there is nothing here to interact with. The whole surface
is a drag handle (frameless windows have no title bar) except for a thin band
at the edges, which resizes; double-click returns to the dashboard, and
right-click opens the mode menu.
"""

from __future__ import annotations

from PySide6.QtCore import QPointF, QRectF, Qt, Signal
from PySide6.QtGui import QFont, QFontMetrics, QPainter, QPen
from PySide6.QtWidgets import QSizePolicy, QWidget

from ..settings import WIDGET_MIN_H, WIDGET_MIN_W
from .. import formatting
from ..providers import Meter, ProviderSnapshot
from ..theme import (
    Theme,
    qcolor,
    severity_color,
    severity_for,
    severity_word,
)

# The arc opens at the bottom: 270 degrees swept clockwise from lower-left.
START_ANGLE = 225
SWEEP = -270

ARC_MAX = 76.0
ARC_MIN = 44.0
GAP = 6.0

# Below this the centred figure no longer fits inside the ring, so the
# percentage moves out to the caption instead of overprinting the arc.
INLINE_VALUE_MIN = 46.0

# A frameless window has no border to grab, so one is carved out of the
# widget's own edge. Wide enough to hit without aiming, narrow enough that
# the middle still reads as a drag handle at the 150x96 floor.
RESIZE_MARGIN = 7


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
        self.setMinimumSize(WIDGET_MIN_W, WIDGET_MIN_H)
        # Needed for the hover cursor over the resize band; without it Qt
        # only delivers moves while a button is held.
        self.setMouseTracking(True)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setCursor(Qt.CursorShape.SizeAllCursor)
        self.setToolTip(
            "Drag to move · drag an edge to resize · double-click to expand"
            " · right-click for options"
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

        gauges = [m for m in self._meters() if m.percent is not None]
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
        reset_lines = small_metrics.height() * 2 + 4
        available_h = self.height() - y - margin - footer_height - reset_lines

        # Fit as many arcs across as the width allows, then size them to
        # whichever of width or height binds. Meters that cannot reach a
        # legible size are dropped rather than shrunk into a smear.
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

        cell = (width - GAP * (len(shown) - 1)) / len(shown)
        inline_value = arc >= INLINE_VALUE_MIN
        value_font = QFont(self.font())
        value_font.setPointSizeF(max(6.0, arc * 0.20))
        value_font.setWeight(QFont.Weight.DemiBold)

        for index, meter in enumerate(shown):
            centre_x = margin + (cell + GAP) * index + cell / 2
            self._draw_arc(painter, centre_x, y, arc, meter, value_font, inline_value)

            text_top = y + arc + 2
            painter.setFont(small_font)
            painter.setPen(qcolor(self.theme.ink))
            caption = meter.title
            if not inline_value:
                caption = f"{meter.title} {self._percent_text(meter)}"
            painter.drawText(
                QRectF(centre_x - cell / 2, text_top, cell, small_metrics.height()),
                Qt.AlignmentFlag.AlignCenter,
                small_metrics.elidedText(
                    caption, Qt.TextElideMode.ElideRight, int(cell)
                ),
            )
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(
                    centre_x - cell / 2,
                    text_top + small_metrics.height(),
                    cell,
                    small_metrics.height(),
                ),
                Qt.AlignmentFlag.AlignCenter,
                small_metrics.elidedText(
                    meter.subtitle, Qt.TextElideMode.ElideRight, int(cell)
                ),
            )

        cursor = y + arc + 2 + label_block + 4
        footer_top = self.height() - margin - (
            small_metrics.height() if self.status else 0
        )

        # The reset line belongs to the window that matters most - the fullest
        # one - and carries both the countdown and the wall-clock time.
        lead = max(shown, key=lambda m: m.percent or 0.0)
        severity = severity_for(lead.percent, lead.severity)
        painter.setFont(small_font)

        head = lead.title
        if severity != "normal":
            glyph, word = severity_word(severity)
            head = f"{glyph} {lead.title} {word}"
        if cursor + small_metrics.height() <= footer_top:
            painter.setPen(
                qcolor(self.theme.ink_secondary)
                if severity == "normal"
                else qcolor(Theme.status(severity))
            )
            painter.drawText(
                QRectF(margin, cursor, width, small_metrics.height()),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                small_metrics.elidedText(head, Qt.TextElideMode.ElideRight, int(width)),
            )
            cursor += small_metrics.height()

        reset_text = self._reset_text(lead)
        if reset_text and cursor + small_metrics.height() <= footer_top:
            painter.setPen(qcolor(self.theme.ink_muted))
            painter.drawText(
                QRectF(margin, cursor, width, small_metrics.height()),
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                small_metrics.elidedText(
                    reset_text, Qt.TextElideMode.ElideRight, int(width)
                ),
            )
            cursor += small_metrics.height()

        # Every line above is guarded against the footer; the footer itself was
        # not, so at the smallest sizes "updated just now" printed on top of the
        # meter caption. When the content already reaches the bottom, the age of
        # the reading is the least valuable line on screen - drop it.
        if self.status and cursor <= footer_top:
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

    # -- pieces -----------------------------------------------------------

    @staticmethod
    def _percent_text(meter: Meter) -> str:
        percent = max(0.0, min(100.0, meter.percent or 0.0))
        near_whole = abs(percent - round(percent)) < 0.05
        return f"{percent:.0f}%" if percent >= 10 or near_whole else f"{percent:.1f}%"

    @staticmethod
    def _reset_text(meter: Meter) -> str:
        """Countdown *and* wall-clock time - one answers 'how long have I got',
        the other 'can I go to lunch first', and neither substitutes."""
        if meter.locked_reason:
            return str(meter.locked_reason)
        remaining = meter.resets_in
        if meter.resets_at is None or remaining is None:
            return "no reset scheduled"
        if remaining.total_seconds() <= 0:
            return "resetting now"
        return (
            f"resets in {formatting.duration(remaining)}"
            f"  ·  {formatting.local_time(meter.resets_at)}"
        )

    def _draw_arc(
        self,
        painter: QPainter,
        centre_x: float,
        top: float,
        size: float,
        meter: Meter,
        value_font: QFont,
        draw_value: bool,
    ) -> None:
        thickness = max(4.0, size * 0.11)
        inset = thickness / 2 + 1
        box = QRectF(
            centre_x - size / 2 + inset, top + inset, size - inset * 2, size - inset * 2
        )

        track = QPen(qcolor(self.theme.track), thickness)
        track.setCapStyle(Qt.PenCapStyle.RoundCap)
        painter.setPen(track)
        painter.drawArc(box, START_ANGLE * 16, SWEEP * 16)

        percent = max(0.0, min(100.0, meter.percent or 0.0))
        if percent > 0:
            severity = severity_for(meter.percent, meter.severity)
            fill = QPen(qcolor(severity_color(self.theme, severity)), thickness)
            fill.setCapStyle(Qt.PenCapStyle.RoundCap)
            painter.setPen(fill)
            painter.drawArc(box, START_ANGLE * 16, int(SWEEP * percent / 100.0 * 16))

        if not draw_value:
            return
        painter.setFont(value_font)
        painter.setPen(qcolor(self.theme.ink))
        metrics = QFontMetrics(value_font)
        painter.drawText(
            QRectF(
                centre_x - size / 2,
                top + (size - metrics.height()) / 2,
                size,
                metrics.height(),
            ),
            Qt.AlignmentFlag.AlignCenter,
            self._percent_text(meter),
        )

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

    def _meters(self) -> list[Meter]:
        if self.snapshot is None:
            return []
        return list(self.snapshot.meters)

    # -- interaction ------------------------------------------------------

    # -- resizing ----------------------------------------------------------

    def _edges_at(self, pos) -> Qt.Edge:
        """Which window edges, if any, the pointer is over."""
        edges = Qt.Edge(0)
        x, y = pos.x(), pos.y()
        if x <= RESIZE_MARGIN:
            edges |= Qt.Edge.LeftEdge
        elif x >= self.width() - RESIZE_MARGIN:
            edges |= Qt.Edge.RightEdge
        if y <= RESIZE_MARGIN:
            edges |= Qt.Edge.TopEdge
        elif y >= self.height() - RESIZE_MARGIN:
            edges |= Qt.Edge.BottomEdge
        return edges

    @staticmethod
    def _cursor_for(edges: Qt.Edge) -> Qt.CursorShape:
        left = bool(edges & Qt.Edge.LeftEdge)
        right = bool(edges & Qt.Edge.RightEdge)
        top = bool(edges & Qt.Edge.TopEdge)
        bottom = bool(edges & Qt.Edge.BottomEdge)
        if (left and top) or (right and bottom):
            return Qt.CursorShape.SizeFDiagCursor
        if (right and top) or (left and bottom):
            return Qt.CursorShape.SizeBDiagCursor
        if left or right:
            return Qt.CursorShape.SizeHorCursor
        if top or bottom:
            return Qt.CursorShape.SizeVerCursor
        return Qt.CursorShape.SizeAllCursor

    # -- mouse -------------------------------------------------------------

    def mousePressEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if event.button() != Qt.MouseButton.LeftButton:
            return
        edges = self._edges_at(event.position())
        if edges:
            # Hand the drag to the window manager: it honours the window's
            # minimum and maximum, snaps, and keeps the opposite corner fixed,
            # none of which a hand-rolled geometry loop gets right for free.
            handle = self.window().windowHandle()
            if handle is not None:
                self._drag_origin = None
                handle.startSystemResize(edges)
                event.accept()
                return
        window = self.window()
        self._drag_origin = (
            event.globalPosition() - window.frameGeometry().topLeft().toPointF()
        )
        event.accept()

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if self._drag_origin is None:
            # Hovering: show what the edge under the pointer would do.
            self.setCursor(self._cursor_for(self._edges_at(event.position())))
            return
        if not event.buttons() & Qt.MouseButton.LeftButton:
            self._drag_origin = None
            return
        # Once this view is no longer the visible page the window belongs to
        # the dashboard, and dragging it by an offset measured against the
        # widget would fling the restored window to the pointer.
        if not self.isVisible():
            self._drag_origin = None
            return
        self.window().move((event.globalPosition() - self._drag_origin).toPoint())
        event.accept()

    def mouseReleaseEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self._drag_origin = None

    def mouseDoubleClickEvent(self, event) -> None:  # noqa: N802 - Qt naming
        # End the drag the second click started. Qt delivers press -> release
        # -> press -> double-click, so a drag is always in flight here; leaving
        # it armed let the next move event drag the newly restored dashboard
        # window to the mouse pointer.
        self._drag_origin = None
        self.expand_requested.emit()
        event.accept()

    def contextMenuEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self.menu_requested.emit(QPointF(event.globalPos()))
        event.accept()
