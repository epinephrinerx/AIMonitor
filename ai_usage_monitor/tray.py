"""System tray icon.

The icon is drawn, not loaded: it shows the active service's session window as
a fill level with the percentage written across it, so the number is readable
without opening anything. Colour follows the same 75/90 thresholds as the
meters, and the tooltip always names the service - the icon alone could not say
which one it is showing, and it rotates.

Rotation is the point: with several services connected there is no room for
several icons, so one icon cycles through them every couple of seconds. It only
cycles over services that actually have a percentage to show, and stops
rotating entirely when there is just one - a still icon is easier to read than a
flickering one.
"""

from __future__ import annotations

from PySide6.QtCore import QObject, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import QAction, QColor, QFont, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import QMenu, QSystemTrayIcon

from . import fonts, formatting
from .providers import Provider, ProviderSnapshot
from .theme import Theme, qcolor, severity_color, severity_for, severity_word

ROTATE_MS = 2000
ICON_PX = 64  # drawn large; Windows scales down to whatever the tray asks for


class TrayController(QObject):
    """Owns the tray icon, its rotation timer, and its menu."""

    resume_requested = Signal()      # restore the full dashboard window
    widget_requested = Signal()      # show the compact desk widget
    settings_requested = Signal()
    exit_requested = Signal()
    refresh_requested = Signal()

    def __init__(
        self,
        providers: list[Provider],
        theme: Theme,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self.providers = providers
        self.theme = theme
        self.snapshots: dict[str, ProviderSnapshot] = {}
        self._order: list[str] = []
        self._index = 0

        self.icon = QSystemTrayIcon(self)
        self.icon.setToolTip("AI Usage Monitor")
        self.icon.activated.connect(self._on_activated)

        self._menu = QMenu()
        # Rebuilt every time it opens: the quota rows carry live countdowns, and
        # a menu assembled once would show the time remaining when the app
        # started rather than now.
        self._menu.aboutToShow.connect(self._rebuild_menu)
        self._rebuild_menu()
        self.icon.setContextMenu(self._menu)

        self._rotate = QTimer(self)
        self._rotate.setInterval(ROTATE_MS)
        self._rotate.timeout.connect(self._advance)

        self._render()

    # -- menu -------------------------------------------------------------

    def _rebuild_menu(self) -> None:
        """Every quota line for every signed-in service, at the top level.

        Nesting them under a submenu meant the numbers you opened the menu for
        were always one hover away; inline they are readable the moment the
        menu appears.
        """
        self._menu.clear()

        resume = QAction("Resume", self._menu)
        resume.setToolTip("Restore the full window")
        resume.triggered.connect(self.resume_requested.emit)
        self._menu.addAction(resume)

        widget = QAction("Show Widget", self._menu)
        widget.triggered.connect(self.widget_requested.emit)
        self._menu.addAction(widget)

        self._menu.addSeparator()
        if not self._add_quota_rows():
            empty = QAction("No quota data yet", self._menu)
            empty.setEnabled(False)
            self._menu.addAction(empty)

        refresh = QAction("Refresh now", self._menu)
        refresh.triggered.connect(self.refresh_requested.emit)
        self._menu.addAction(refresh)

        self._menu.addSeparator()
        settings = QAction("Setting", self._menu)
        settings.triggered.connect(self.settings_requested.emit)
        self._menu.addAction(settings)

        quit_action = QAction("Exit", self._menu)
        quit_action.triggered.connect(self.exit_requested.emit)
        self._menu.addAction(quit_action)

    def _add_quota_rows(self) -> bool:
        """One disabled header per signed-in service, then its meters."""
        added = False
        for provider in self.providers:
            snapshot = self.snapshots.get(provider.id)
            if snapshot is None or not snapshot.configured or not snapshot.meters:
                continue

            header = QAction(provider.display_name, self._menu)
            header.setEnabled(False)
            self._menu.addAction(header)

            for meter in snapshot.meters:
                row = QAction(f"      {self._meter_line(meter)}", self._menu)
                row.setEnabled(False)
                self._menu.addAction(row)
                added = True

            self._menu.addSeparator()
        return added

    @staticmethod
    def _meter_line(meter) -> str:
        value = (
            f"{meter.percent:.0f}%" if meter.percent is not None else (meter.detail or "—")
        )
        name = f"{meter.title} · {meter.subtitle}" if meter.subtitle else meter.title

        remaining = meter.resets_in
        if (
            meter.resets_at is not None
            and remaining is not None
            and remaining.total_seconds() > 0
        ):
            when = (
                f"resets in {formatting.duration(remaining)}"
                f" ({formatting.local_time(meter.resets_at)})"
            )
        elif meter.locked_reason:
            when = str(meter.locked_reason)
        else:
            when = "no reset scheduled"
        return f"{name} — {value} · {when}"

    def _on_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        # Double-click restores the full window. Right-click still opens the
        # menu, which is the platform convention and where everything else is.
        if reason == QSystemTrayIcon.ActivationReason.DoubleClick:
            self.resume_requested.emit()

    # -- data -------------------------------------------------------------

    def set_snapshots(self, snapshots: dict[str, ProviderSnapshot]) -> None:
        self.snapshots = snapshots
        order = [
            provider.id
            for provider in self.providers
            if self._lead(snapshots.get(provider.id)) is not None
        ]
        if order != self._order:
            self._order = order
            self._index = 0
        # A single service has nothing to rotate through; leave it still.
        if len(self._order) > 1:
            if not self._rotate.isActive():
                self._rotate.start()
        else:
            self._rotate.stop()
        self._render()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self._render()

    def show(self) -> None:
        self.icon.show()

    def hide(self) -> None:
        self._rotate.stop()
        self.icon.hide()

    @staticmethod
    def available() -> bool:
        return QSystemTrayIcon.isSystemTrayAvailable()

    def notify(self, title: str, message: str) -> None:
        if self.icon.isVisible():
            self.icon.showMessage(title, message, QSystemTrayIcon.MessageIcon.Information, 4000)

    # -- rendering --------------------------------------------------------

    @staticmethod
    def _lead(snapshot: ProviderSnapshot | None):
        """The meter the icon speaks for: the session window if there is one."""
        if snapshot is None or not snapshot.configured:
            return None
        for meter in snapshot.meters:
            if meter.percent is not None:
                return meter
        return None

    def _advance(self) -> None:
        if len(self._order) <= 1:
            return
        self._index = (self._index + 1) % len(self._order)
        self._render()

    def _render(self) -> None:
        if not self._order:
            self.icon.setIcon(self._draw(None, "normal"))
            self.icon.setToolTip("AI Usage Monitor — no quota data yet")
            return

        self._index %= len(self._order)
        provider_id = self._order[self._index]
        provider = next((p for p in self.providers if p.id == provider_id), None)
        snapshot = self.snapshots.get(provider_id)
        meter = self._lead(snapshot)
        if provider is None or meter is None:
            return

        severity = severity_for(meter.percent, meter.severity)
        self.icon.setIcon(self._draw(meter.percent, severity))

        _, word = severity_word(severity)
        lines = [f"{provider.display_name} — {meter.title}"]
        if meter.subtitle:
            lines[0] += f" ({meter.subtitle})"
        lines.append(f"{meter.percent:.0f}% · {word}")
        remaining = meter.resets_in
        if meter.resets_at is not None and remaining is not None and remaining.total_seconds() > 0:
            lines.append(
                f"resets in {formatting.duration(remaining)}"
                f" ({formatting.local_time(meter.resets_at)})"
            )
        if len(self._order) > 1:
            lines.append(f"{self._index + 1} of {len(self._order)} services")
        self.icon.setToolTip("\n".join(lines))

    def _draw(self, percent: float | None, severity: str) -> QIcon:
        """A rounded tile filled from the bottom, with the number across it."""
        pixmap = QPixmap(ICON_PX, ICON_PX)
        pixmap.fill(Qt.GlobalColor.transparent)

        painter = QPainter(pixmap)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        box = QRectF(2, 2, ICON_PX - 4, ICON_PX - 4)
        radius = ICON_PX * 0.22

        # Unfilled track first, then the level on top of it.
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(qcolor(self.theme.track))
        painter.drawRoundedRect(box, radius, radius)

        fraction = 0.0 if percent is None else max(0.0, min(100.0, percent)) / 100.0
        if fraction > 0:
            painter.save()
            clip = QRectF(box)
            clip.setTop(box.bottom() - box.height() * fraction)
            painter.setClipRect(clip)
            painter.setBrush(qcolor(severity_color(self.theme, severity)))
            painter.drawRoundedRect(box, radius, radius)
            painter.restore()

        text = "—" if percent is None else f"{percent:.0f}"
        font = fonts.ui_font()
        font.setPixelSize(int(ICON_PX * (0.46 if len(text) < 3 else 0.36)))
        font.setWeight(QFont.Weight.Bold)
        painter.setFont(font)

        # The fill line usually crosses the digits, so the number is drawn
        # twice: ink above the line, white below it, each clipped to its own
        # half. One flat colour would sit on the wrong background for part of
        # every glyph at the levels you look at most.
        split = box.bottom() - box.height() * fraction

        above = QRectF(box)
        above.setBottom(split)
        painter.save()
        painter.setClipRect(above)
        painter.setPen(qcolor(self.theme.ink))
        painter.drawText(box, Qt.AlignmentFlag.AlignCenter, text)
        painter.restore()

        if fraction > 0:
            below = QRectF(box)
            below.setTop(split)
            painter.save()
            painter.setClipRect(below)
            painter.setPen(QColor("#ffffff"))
            painter.drawText(box, Qt.AlignmentFlag.AlignCenter, text)
            painter.restore()

        painter.end()
        return QIcon(pixmap)
