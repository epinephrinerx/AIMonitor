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

def lead_meter(meters: list) -> Meter:
    """The window the caption speaks for: the five-hour one where there is one.

    Shared with the tray through `tray.is_five_hour`, so the dashboard, the
    widget and the tray icon all answer "which window matters most" the same
    way. Falls back to the first meter, which after `api._reading_order` is
    the shortest window the service reported.
    """
    from ..tray import is_five_hour

    return next((meter for meter in meters if is_five_hour(meter)), meters[0])


# The arc opens at the bottom: 270 degrees swept clockwise from lower-left.
START_ANGLE = 225
SWEEP = -270

ARC_MAX = 76.0
ARC_MIN = 44.0

# The size below which a ring stops being readable at all. Reached only by
# giving up the subtitle first; nothing is ever drawn outside the widget to
# hold on to it.
ARC_FLOOR = 28.0
GAP = 6.0

# Below this the centred figure no longer fits inside the ring, so the
# percentage moves out to the caption instead of overprinting the arc.
INLINE_VALUE_MIN = 46.0

# A frameless window has no border to grab, so one is carved out of the
# widget's own edge. Wide enough to hit without aiming, narrow enough that
# the middle still reads as a drag handle at the 150x96 floor.
RESIZE_MARGIN = 7

# The two chevrons that step between services. Inset by the content margin,
# which is wider than RESIZE_MARGIN, so they never sit on the resize band.
PAGER_SIZE = 18.0
PAGER_GAP = 2.0

# The padding around everything drawn. Wider than RESIZE_MARGIN, so content
# never sits on the band that resizes the window.
MARGIN = 11.0


class CompactView(QWidget):
    """A row of radial meters plus a header naming the active provider."""

    expand_requested = Signal()
    menu_requested = Signal(QPointF)
    page_requested = Signal(int)

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.title = "Claude"
        self.snapshot: ProviderSnapshot | None = None
        self.status = ""
        self._drag_origin: QPointF | None = None
        self.can_page = False
        self._hover_page: int | None = None
        # Laid out during paint and hit-tested afterwards, so the rectangle a
        # click is measured against is the one that was actually drawn rather
        # than a second copy of the same arithmetic.
        self._pager: tuple[QRectF, QRectF] | None = None
        self.setMinimumSize(WIDGET_MIN_W, WIDGET_MIN_H)
        # Needed for the hover cursor over the resize band; without it Qt
        # only delivers moves while a button is held.
        self.setMouseTracking(True)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setCursor(Qt.CursorShape.SizeAllCursor)
        self._base_tip = (
            "Drag to move · drag an edge to resize · double-click to expand"
            " · right-click for options"
        )
        self.setToolTip(self._base_tip)

    def set_snapshot(
        self, title: str, snapshot: ProviderSnapshot | None, status: str
    ) -> None:
        self.title = title
        self.snapshot = snapshot
        self.status = status
        self.update()

    def set_pageable(self, pageable: bool) -> None:
        """Show the chevrons only when there is somewhere to page to."""
        if pageable == self.can_page:
            return
        self.can_page = pageable
        self.setToolTip(
            f"{self._base_tip} · ‹ › for the next service"
            if pageable
            else self._base_tip
        )
        self.update()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self.update()

    def _fonts(self) -> tuple[QFont, QFont]:
        """The two fonts everything here is measured in.

        Built in one place because the minimum useful size is worked out from
        the same metrics the painting uses; two copies of the arithmetic would
        drift, and the size that drifted would be the one the user can drag to.
        """
        header = QFont(self.font())
        header.setPointSizeF(9.5)
        header.setWeight(QFont.Weight.DemiBold)
        small = QFont(self.font())
        small.setPointSizeF(7.5)
        return header, small

    def minimum_useful_height(self) -> int:
        """The shortest this can be and still draw one labelled ring.

        A fixed floor was a guess about font metrics, and on a real desktop it
        guessed low: at 96px the system font left no room, so the enforced
        minimum sat inside the "too small" branch and the widget refused to
        draw at a size the user was allowed to drag to. Asking the fonts
        instead makes the floor follow the text scale.
        """
        header, small = self._fonts()
        line = QFontMetrics(small).height()
        return int(
            MARGIN                              # top padding
            + QFontMetrics(header).height() + 5  # the title row
            + ARC_FLOOR                          # the smallest legible ring
            + line + 3                           # its caption
            + line + 4                           # the "updated" line
            + MARGIN                             # bottom padding
            + 1                                  # never land exactly on it
        )

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

        margin = MARGIN
        width = self.width() - margin * 2
        y = margin

        header_font, small_font = self._fonts()
        small_metrics = QFontMetrics(small_font)

        painter.setFont(header_font)
        header_metrics = QFontMetrics(header_font)

        self._pager = None
        title_width = width
        if self.can_page:
            self._pager = self._lay_out_pager(
                margin, y, width, float(header_metrics.height())
            )
            title_width = self._pager[0].left() - margin - 6

        painter.setPen(qcolor(self.theme.ink))
        painter.drawText(
            QRectF(margin, y, max(0.0, title_width), header_metrics.height()),
            Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            header_metrics.elidedText(
                self.title, Qt.TextElideMode.ElideRight, int(max(0.0, title_width))
            ),
        )
        if self._pager is not None:
            self._draw_pager(painter)
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
        line = small_metrics.height()

        # How many arcs fit is a question about *width* alone, because width is
        # the only thing dropping a meter buys back. Answering it with height
        # as well is what left a short widget showing a single gauge no matter
        # how wide it was dragged - each meter dropped freed width the layout
        # was not short of, the height it was short of never moved, and the
        # loop walked all the way down to one.
        count = len(gauges)
        while count > 1 and (width - GAP * (count - 1)) / count < ARC_MIN:
            count -= 1
        shown = gauges[:count]
        by_width = (width - GAP * (count - 1)) / count

        # Height is answered by giving up caption lines instead. The room left
        # over is not reduced by the reset lines below: those already decline
        # to draw when they do not fit, so reserving their 30px here only
        # squeezed the arcs on behalf of text that would have yielded anyway.
        # That reservation is why a widget could hold three gauges while fresh
        # and one once a status line appeared - a 17px change deciding it.
        #
        # The subtitle goes first; the title never does. A ring identified by
        # nothing but its colour breaks the rule this project holds everywhere
        # else, and below INLINE_VALUE_MIN the caption is also where the
        # percentage has moved to.
        room = self.height() - y - margin - footer_height
        caption_lines = 2
        label_block = line * caption_lines + 3
        arc = min(ARC_MAX, by_width, room - label_block)
        if arc < ARC_MIN and caption_lines > 1:
            caption_lines = 1
            label_block = line * caption_lines + 3
            arc = min(ARC_MAX, by_width, room - label_block)

        # Smaller than this is a smear rather than a meter. Nothing is drawn
        # outside the widget to reach it: if the floor and one caption cannot
        # both fit, the arcs are what is honest to leave out.
        if arc < ARC_FLOOR:
            if room < ARC_FLOOR + label_block:
                self._draw_message(
                    painter, margin, y, width, "Too small to show the meters."
                )
                painter.end()
                return
            arc = ARC_FLOOR

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
            if caption_lines > 1:
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

        # The reset line belongs to the five-hour window, and carries both the
        # countdown and the wall-clock time.
        #
        # It used to belong to whichever window was fullest, which meant the
        # caption changed which window it was describing as the numbers moved
        # - the same complaint that had the gauges themselves reordering. It
        # could also disagree with the arc above it: at the smallest size only
        # the first arc survives, and that is the five-hour window, while the
        # caption might be describing the weekly one that was no longer drawn.
        # The tray answers this question with `is_five_hour`; so does this.
        lead = lead_meter(shown)
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

    # -- paging ------------------------------------------------------------

    @staticmethod
    def _lay_out_pager(
        margin: float, top: float, width: float, height: float
    ) -> tuple[QRectF, QRectF]:
        size = min(PAGER_SIZE, max(12.0, height))
        y = top + (height - size) / 2
        right = margin + width - size
        left = right - size - PAGER_GAP
        return QRectF(left, y, size, size), QRectF(right, y, size, size)

    def _draw_pager(self, painter: QPainter) -> None:
        assert self._pager is not None
        for step, rect in ((-1, self._pager[0]), (1, self._pager[1])):
            hot = self._hover_page == step
            if hot:
                painter.setPen(Qt.PenStyle.NoPen)
                painter.setBrush(qcolor(self.theme.ink, 0.12))
                painter.drawRoundedRect(rect, 4, 4)
            painter.setBrush(Qt.BrushStyle.NoBrush)
            pen = QPen(
                qcolor(self.theme.ink if hot else self.theme.ink_secondary), 1.6
            )
            pen.setCapStyle(Qt.PenCapStyle.RoundCap)
            pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
            painter.setPen(pen)
            centre = rect.center()
            reach = rect.width() * 0.19
            tip = centre.x() + reach * step
            back = centre.x() - reach * step
            painter.drawLine(
                QPointF(back, centre.y() - reach), QPointF(tip, centre.y())
            )
            painter.drawLine(
                QPointF(tip, centre.y()), QPointF(back, centre.y() + reach)
            )

    def _page_at(self, pos) -> int | None:
        """Which chevron is under the pointer: -1, 1, or neither."""
        if self._pager is None or not self.can_page:
            return None
        for step, rect in ((-1, self._pager[0]), (1, self._pager[1])):
            if rect.contains(pos):
                return step
        return None

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
        step = self._page_at(event.position())
        if step is not None:
            # Not the start of a drag: the whole face of the widget moves the
            # window, so the chevrons have to claim the press outright or a
            # click on one would be read as a one-pixel drag.
            self._drag_origin = None
            self.page_requested.emit(step)
            event.accept()
            return
        window = self.window()
        self._drag_origin = (
            event.globalPosition() - window.frameGeometry().topLeft().toPointF()
        )
        event.accept()

    def leaveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if self._hover_page is not None:
            self._hover_page = None
            self.update()
        super().leaveEvent(event)

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 - Qt naming
        if self._drag_origin is None:
            step = self._page_at(event.position())
            if step != self._hover_page:
                self._hover_page = step
                self.update()
            if step is not None:
                self.setCursor(Qt.CursorShape.PointingHandCursor)
                return
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
        step = self._page_at(event.position())
        if step is not None:
            # Qt sends this *instead of* the second press, so the step has to
            # be taken here or two quick clicks on a chevron would move one
            # place. It must not expand either: these are buttons, and
            # clicking a button twice means doing it twice.
            self.page_requested.emit(step)
            event.accept()
            return
        self.expand_requested.emit()
        event.accept()

    def contextMenuEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self.menu_requested.emit(QPointF(event.globalPos()))
        event.accept()
