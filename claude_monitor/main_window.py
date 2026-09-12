"""The main window: a tabbed dashboard that collapses into a desk widget.

Two modes share one window.

* **Dashboard** - a normal framed window, tabs per provider, everything on show.
* **Widget** - frameless, at most 300x300, optionally always-on-top and
  translucent, drawn by `CompactView`.

Shrinking the window past `WIDGET_TRIGGER_EDGE` on either edge collapses it;
double-clicking the widget (or its context menu) expands it again. Each mode
remembers its own geometry, so switching back and forth does not lose the
window position you chose for either.

Widget mode also refreshes less work, not just less pixels: `want_history` goes
false, so no transcripts are parsed and no chart data is retained.
"""

from __future__ import annotations

import datetime as dt

from PySide6.QtCore import QPoint, QSize, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QAction, QActionGroup, QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QApplication,
    QComboBox,
    QHBoxLayout,
    QLabel,
    QMainWindow,
    QMenu,
    QPushButton,
    QStackedWidget,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from . import api, formatting, memory, theme as theming
from .dashboard import ProviderPage
from .providers import build_all
from .settings import (
    INTERVAL_OPTIONS,
    RANGE_OPTIONS,
    Settings,
    WIDGET_MAX_EDGE,
    WIDGET_TRIGGER_EDGE,
)
from .settings_dialog import ProviderSettingsDialog
from .theme import Theme, qcolor
from .usage_log import METRICS
from .widgets.compact import CompactView
from .worker import RefreshResult, RefreshWorker

DASHBOARD = "dashboard"
WIDGET = "widget"

DASHBOARD_MIN_W = 760
DASHBOARD_MIN_H = 560


class MainWindow(QMainWindow):
    request_refresh = Signal(int, str, bool, str)
    push_credentials = Signal(str, str, str)
    push_enabled = Signal(str, bool)
    push_throughput_interval = Signal(int)

    def __init__(self, settings: Settings) -> None:
        super().__init__()
        self.settings = settings
        self.theme_name = settings.theme
        self.theme: Theme = theming.resolve(self.theme_name)
        self.mode = DASHBOARD
        self.last_result: RefreshResult | None = None
        self.last_success: dt.datetime | None = None
        self._refreshing = False
        self._theme_check = 0
        self._switching = False
        self._working_set = 0.0
        self._memory_check = 0

        # Providers are mirrored here purely for display metadata; the worker
        # owns the instances that actually do the fetching.
        self.providers = build_all()
        self.pages: dict[str, ProviderPage] = {}

        self.setWindowTitle("Claude Usage Monitor")
        # The dashboard floor applies from the start. Without it a stored
        # geometry smaller than the collapse trigger is honoured verbatim and
        # the window opens as a squeezed dashboard - too small to read, and too
        # small to fix itself, because resizeEvent is suppressed while hidden.
        self.setMinimumSize(DASHBOARD_MIN_W, DASHBOARD_MIN_H)

        self._build_ui()
        self._start_worker()
        self._push_all_credentials()
        self._apply_theme(self.theme)
        self._restore_geometry(DASHBOARD, QSize(1120, 820))

        self.refresh_timer = QTimer(self)
        self.refresh_timer.timeout.connect(self.refresh)
        self._apply_interval()

        self.tick_timer = QTimer(self)
        self.tick_timer.setInterval(1000)
        self.tick_timer.timeout.connect(self._tick)
        self.tick_timer.start()

        QShortcut(QKeySequence("F5"), self, activated=self.refresh)
        QShortcut(QKeySequence("Ctrl+W"), self, activated=self.toggle_mode)

        QTimer.singleShot(0, self.refresh)

    # -- construction -----------------------------------------------------

    def _build_ui(self) -> None:
        self.stack = QStackedWidget()
        self.setCentralWidget(self.stack)

        # Page 0: the full dashboard.
        self.dashboard_page = QWidget()
        outer = QVBoxLayout(self.dashboard_page)
        outer.setContentsMargins(20, 16, 20, 12)
        outer.setSpacing(12)
        outer.addWidget(self._build_header())

        self.tabs = QTabWidget()
        self.tabs.setDocumentMode(True)
        self.tabs.currentChanged.connect(self._on_tab_changed)
        for provider in self.providers:
            page = ProviderPage(provider, self.theme)
            page.configure_requested.connect(self.open_settings)
            page.throughput_interval_changed.connect(self._on_throughput_interval)
            page.set_throughput_interval(self.settings.throughput_interval)
            self.pages[provider.id] = page
            self.tabs.addTab(page, provider.display_name)
        outer.addWidget(self.tabs, 1)

        self.status = QLabel("Starting…")
        self.status.setContentsMargins(2, 0, 2, 0)
        outer.addWidget(self.status)
        self.stack.addWidget(self.dashboard_page)

        # Page 1: the compact widget.
        self.compact = CompactView(self.theme)
        self.compact.expand_requested.connect(self.enter_dashboard_mode)
        self.compact.menu_requested.connect(self._show_widget_menu)
        self.stack.addWidget(self.compact)

        active = self.settings.active_provider
        for index, provider in enumerate(self.providers):
            if provider.id == active:
                self.tabs.setCurrentIndex(index)
                break

    def _build_header(self) -> QWidget:
        header = QWidget()
        row = QHBoxLayout(header)
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(10)

        titles = QVBoxLayout()
        titles.setSpacing(1)
        self.title_label = QLabel("Claude Usage Monitor")
        self.account_label = QLabel("Loading…")
        titles.addWidget(self.title_label)
        titles.addWidget(self.account_label)
        row.addLayout(titles, 1)

        self.metric_combo = QComboBox()
        self.metric_combo.addItems(list(METRICS))
        self.metric_combo.setCurrentText(self.settings.metric)
        self.metric_combo.currentIndexChanged.connect(self._on_view_changed)
        row.addWidget(self.metric_combo)

        self.range_combo = QComboBox()
        for label, _ in RANGE_OPTIONS:
            self.range_combo.addItem(label)
        self.range_combo.setCurrentIndex(_index_of(RANGE_OPTIONS, self.settings.range_days, 1))
        self.range_combo.currentIndexChanged.connect(self._on_view_changed)
        row.addWidget(self.range_combo)

        self.interval_combo = QComboBox()
        for label, _ in INTERVAL_OPTIONS:
            self.interval_combo.addItem(label)
        self.interval_combo.setCurrentIndex(
            _index_of(INTERVAL_OPTIONS, self.settings.interval, 2)
        )
        self.interval_combo.currentIndexChanged.connect(self._apply_interval)
        row.addWidget(self.interval_combo)

        self.theme_button = QPushButton()
        self.theme_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.theme_button.clicked.connect(self._cycle_theme)
        row.addWidget(self.theme_button)

        self.settings_button = QPushButton("Settings…")
        self.settings_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.settings_button.clicked.connect(lambda: self.open_settings(""))
        row.addWidget(self.settings_button)

        self.widget_button = QPushButton("Widget")
        self.widget_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.widget_button.setToolTip("Collapse to the desk widget (Ctrl+W)")
        self.widget_button.clicked.connect(self.enter_widget_mode)
        row.addWidget(self.widget_button)

        self.refresh_button = QPushButton("Refresh")
        self.refresh_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.refresh_button.setProperty("accent", True)
        self.refresh_button.clicked.connect(self.refresh)
        row.addWidget(self.refresh_button)
        return header

    # -- worker -----------------------------------------------------------

    def _start_worker(self) -> None:
        self.thread = QThread(self)
        self.worker = RefreshWorker()
        self.worker.moveToThread(self.thread)
        self.request_refresh.connect(self.worker.refresh)
        self.push_credentials.connect(self.worker.apply_credentials)
        self.push_enabled.connect(self.worker.set_enabled)
        self.push_throughput_interval.connect(self.worker.set_throughput_interval)
        self.worker.finished.connect(self._on_result)
        self.thread.start()
        self.push_throughput_interval.emit(self.settings.throughput_interval)

    def _push_all_credentials(self) -> None:
        for provider in self.providers:
            self.push_credentials.emit(
                provider.id,
                self.settings.provider_key(provider.id),
                self.settings.provider_extra(provider.id),
            )
            self.push_enabled.emit(
                provider.id, self.settings.provider_enabled(provider.id)
            )

    def refresh(self) -> None:
        if self._refreshing:
            return
        self._refreshing = True
        self.refresh_button.setEnabled(False)
        self.refresh_button.setText("Refreshing…")
        want_history = self.mode == DASHBOARD
        # Widget mode only ever shows the active provider, so fetch just that one.
        only = self._active_provider_id() if self.mode == WIDGET else ""
        self.request_refresh.emit(
            self._days(), self._metric(), want_history, only
        )

    def _on_result(self, result: RefreshResult) -> None:
        self._refreshing = False
        self.refresh_button.setEnabled(True)
        self.refresh_button.setText("Refresh")
        self.last_result = result

        if any(snap.ok for snap in result.snapshots.values()):
            self.last_success = result.finished_at

        show_history = self.mode == DASHBOARD
        for provider_id, snapshot in result.snapshots.items():
            page = self.pages.get(provider_id)
            if page is not None:
                page.render(snapshot, show_history)

        active = self._active_provider_id()
        snapshot = result.snapshots.get(active)
        name = next(
            (p.display_name for p in self.providers if p.id == active), "Provider"
        )
        if snapshot is None:
            self.account_label.setText(f"{name} is not being monitored")
        elif snapshot.account:
            self.account_label.setText(snapshot.account)
        elif not snapshot.configured:
            self.account_label.setText(f"{name} is not configured yet")
        elif snapshot.error:
            self.account_label.setText(f"{name} — last refresh failed")
        else:
            self.account_label.setText(name)
        self._update_tab_labels(result)
        self._tick()

    def _update_tab_labels(self, result: RefreshResult) -> None:
        """Put the headline number on the tab, so it reads at a glance."""
        for index, provider in enumerate(self.providers):
            snapshot = result.snapshots.get(provider.id)
            label = provider.display_name
            if snapshot is not None and snapshot.configured and snapshot.meters:
                lead = snapshot.meters[0]
                if lead.percent is not None:
                    label = f"{provider.display_name}  {lead.percent:.0f}%"
                elif lead.detail:
                    label = f"{provider.display_name}  {lead.detail}"
            self.tabs.setTabText(index, label)

    # -- mode switching ---------------------------------------------------

    def _active_provider_id(self) -> str:
        index = self.tabs.currentIndex()
        if 0 <= index < len(self.providers):
            return self.providers[index].id
        return self.providers[0].id if self.providers else ""

    def toggle_mode(self) -> None:
        if self.mode == DASHBOARD:
            self.enter_widget_mode()
        else:
            self.enter_dashboard_mode()

    def enter_widget_mode(self) -> None:
        if self.mode == WIDGET or self._switching:
            return
        self._switching = True
        self._save_geometry(DASHBOARD)

        self.mode = WIDGET
        self.stack.setCurrentWidget(self.compact)
        # Charts hold the largest arrays on the page; let them go.
        for page in self.pages.values():
            page.release_charts()

        flags = self._widget_flags()
        if self.settings.always_on_top:
            flags |= Qt.WindowType.WindowStaysOnTopHint
        self.setWindowFlags(flags)
        self.setWindowOpacity(self.settings.opacity)
        self.setMinimumSize(180, 120)
        self.setMaximumSize(WIDGET_MAX_EDGE, WIDGET_MAX_EDGE)
        # Geometry is applied after show(): changing window flags re-creates the
        # native window, and a geometry set before that is discarded.
        self.show()
        self._restore_geometry(WIDGET, QSize(260, 230))
        memory.trim_working_set()
        self._switching = False
        self._render_compact()
        self.refresh()

    @staticmethod
    def _widget_flags() -> Qt.WindowType:
        """Frameless, but deliberately NOT Qt.Tool.

        A Tool window keeps no taskbar button, which strands the widget if the
        user turns off always-on-top, and Qt excludes Tool windows from
        `lastWindowClosed` - so closing the widget used to hide the window and
        leave the process running invisibly.
        """
        return Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint

    def enter_dashboard_mode(self) -> None:
        if self.mode == DASHBOARD or self._switching:
            return
        self._switching = True
        self._save_geometry(WIDGET)

        self.mode = DASHBOARD
        self.stack.setCurrentWidget(self.dashboard_page)
        self.setWindowFlags(Qt.WindowType.Window)
        self.setWindowOpacity(1.0)
        self.setMaximumSize(16777215, 16777215)
        self.setMinimumSize(DASHBOARD_MIN_W, DASHBOARD_MIN_H)
        self.show()
        self._restore_geometry(DASHBOARD, QSize(1120, 820))
        self._switching = False
        self.refresh()

    def resizeEvent(self, event) -> None:  # noqa: N802 - Qt naming
        super().resizeEvent(event)
        if self._switching or self.mode != DASHBOARD:
            return
        # Transient sizes reported while hidden or minimised are not the user
        # dragging the frame, and must not collapse the window.
        if not self.isVisible() or self.isMinimized():
            return
        size = event.size()
        if size.width() < WIDGET_TRIGGER_EDGE or size.height() < WIDGET_TRIGGER_EDGE:
            # Defer: we are inside Qt's resize handling right now.
            QTimer.singleShot(0, self.enter_widget_mode)

    def _show_widget_menu(self, global_pos) -> None:
        menu = QMenu(self)

        expand = QAction("Expand to dashboard", menu)
        expand.triggered.connect(self.enter_dashboard_mode)
        menu.addAction(expand)

        refresh = QAction("Refresh now", menu)
        refresh.triggered.connect(self.refresh)
        menu.addAction(refresh)
        menu.addSeparator()

        if len(self.providers) > 1:
            provider_menu = menu.addMenu("Show")
            group = QActionGroup(provider_menu)
            group.setExclusive(True)
            active = self._active_provider_id()
            for index, provider in enumerate(self.providers):
                action = QAction(provider.display_name, provider_menu)
                action.setCheckable(True)
                action.setChecked(provider.id == active)
                action.triggered.connect(
                    lambda _checked=False, i=index: self._select_provider(i)
                )
                group.addAction(action)
                provider_menu.addAction(action)

        on_top = QAction("Always on top", menu)
        on_top.setCheckable(True)
        on_top.setChecked(self.settings.always_on_top)
        on_top.toggled.connect(self._set_always_on_top)
        menu.addAction(on_top)

        opacity_menu = menu.addMenu("Opacity")
        for percent in (100, 90, 80, 70, 60, 45):
            action = QAction(f"{percent}%", opacity_menu)
            action.setCheckable(True)
            action.setChecked(abs(self.settings.opacity - percent / 100) < 0.01)
            action.triggered.connect(
                lambda _checked=False, p=percent: self._set_opacity(p / 100)
            )
            opacity_menu.addAction(action)

        menu.addSeparator()
        quit_action = QAction("Quit", menu)
        quit_action.triggered.connect(self.close)
        menu.addAction(quit_action)

        menu.exec(QPoint(int(global_pos.x()), int(global_pos.y())))

    def _select_provider(self, index: int) -> None:
        self.tabs.setCurrentIndex(index)
        self._render_compact()
        self.refresh()

    def _set_always_on_top(self, enabled: bool) -> None:
        self.settings.always_on_top = enabled
        if self.mode != WIDGET:
            return
        flags = self._widget_flags()
        if enabled:
            flags |= Qt.WindowType.WindowStaysOnTopHint
        geometry = self.geometry()
        self.setWindowFlags(flags)
        self.show()
        self.setGeometry(geometry)

    def _set_opacity(self, value: float) -> None:
        self.settings.opacity = value
        if self.mode == WIDGET:
            self.setWindowOpacity(value)

    def _render_compact(self) -> None:
        provider_id = self._active_provider_id()
        snapshot = (
            self.last_result.snapshots.get(provider_id) if self.last_result else None
        )
        name = next(
            (p.display_name for p in self.providers if p.id == provider_id), "Usage"
        )
        status = ""
        if self.last_success is not None:
            age = dt.datetime.now(dt.timezone.utc) - self.last_success
            seconds = int(age.total_seconds())
            status = "updated just now" if seconds < 10 else f"updated {formatting.duration(age)} ago"
        self.compact.set_snapshot(name, snapshot, status)

    # -- geometry ---------------------------------------------------------

    def _save_geometry(self, mode: str) -> None:
        # A minimised or maximised frame is not a size worth restoring to.
        if self.isMinimized() or self.isMaximized() or not self.isVisible():
            return
        rect = self.geometry()
        if mode == DASHBOARD and (
            rect.width() < WIDGET_TRIGGER_EDGE or rect.height() < WIDGET_TRIGGER_EDGE
        ):
            # This is the drag that collapsed the window, not a dashboard size
            # anyone wants back. Storing it would reopen the app as a squeezed
            # dashboard next launch; keep whatever was saved before.
            return
        self.settings.save_geometry(
            mode, [rect.x(), rect.y(), rect.width(), rect.height()]
        )

    def _restore_geometry(self, mode: str, fallback: QSize) -> None:
        """Apply the saved rect for `mode`, falling back to a default size.

        An explicit rect is stored rather than `saveGeometry()`'s opaque blob:
        the blob encodes window state that does not survive the flag change
        between dashboard and widget, and silently restored the wrong size.
        """
        saved = self.settings.load_geometry(mode)
        rect = None
        if isinstance(saved, (list, tuple)) and len(saved) == 4:
            try:
                rect = [int(value) for value in saved]
            except (TypeError, ValueError):
                rect = None

        # Reject a stored dashboard rect below the collapse trigger, however it
        # got there (an older build, a hand-edited setting): honouring it opens
        # the app squeezed.
        if (
            rect
            and mode == DASHBOARD
            and (rect[2] < WIDGET_TRIGGER_EDGE or rect[3] < WIDGET_TRIGGER_EDGE)
        ):
            rect = None

        if rect and rect[2] > 0 and rect[3] > 0 and self._on_a_screen(rect):
            self.setGeometry(*rect)
        else:
            self.resize(fallback)

    def _on_a_screen(self, rect: list[int]) -> bool:
        """Guard against restoring onto a monitor that is no longer attached."""
        centre = QPoint(rect[0] + rect[2] // 2, rect[1] + rect[3] // 2)
        return any(
            screen.availableGeometry().contains(centre)
            for screen in QApplication.screens()
        )

    # -- periodic ---------------------------------------------------------

    def _tick(self) -> None:
        if self.mode == WIDGET:
            self._render_compact()
        else:
            page = self.pages.get(self._active_provider_id())
            if page is not None:
                page.refresh_countdowns()
            self._update_status()

        # Sampled here rather than straight after the worker's trim, which would
        # report the momentary post-trim floor instead of what the app settles at.
        self._memory_check += 1
        if self._memory_check >= 5:
            self._memory_check = 0
            self._working_set = memory.working_set_mb()

        if self.theme_name == "system":
            self._theme_check += 1
            if self._theme_check >= 10:
                self._theme_check = 0
                resolved = theming.resolve("system")
                if resolved.dark != self.theme.dark:
                    self._apply_theme(resolved)

    def _update_status(self) -> None:
        parts = []
        if self.last_success is None:
            parts.append("No successful fetch yet")
        else:
            age = dt.datetime.now(dt.timezone.utc) - self.last_success
            seconds = int(age.total_seconds())
            when = "just now" if seconds < 5 else f"{formatting.duration(age)} ago"
            parts.append(f"Updated {when} ({formatting.clock(self.last_success)})")

        interval = INTERVAL_OPTIONS[self.interval_combo.currentIndex()][1]
        parts.append(
            f"auto-refresh {INTERVAL_OPTIONS[self.interval_combo.currentIndex()][0].lower()}"
            if interval
            else "manual refresh"
        )
        if self._working_set:
            parts.append(f"{self._working_set:.0f} MB resident")
        self.status.setText("  ·  ".join(parts))

    # -- settings ---------------------------------------------------------

    def _days(self) -> int:
        return RANGE_OPTIONS[self.range_combo.currentIndex()][1]

    def _metric(self) -> str:
        return self.metric_combo.currentText()

    def _on_tab_changed(self, index: int) -> None:
        if 0 <= index < len(self.providers):
            self.settings.active_provider = self.providers[index].id
        if self.last_result is not None:
            self._on_result(self.last_result)

    def _on_throughput_interval(self, seconds: int) -> None:
        """Changing the bucket size discards history at the old scale."""
        if seconds == self.settings.throughput_interval:
            return
        self.settings.throughput_interval = seconds
        for page in self.pages.values():
            page.set_throughput_interval(seconds)
        self.push_throughput_interval.emit(seconds)
        self.refresh()

    def _on_view_changed(self) -> None:
        self.settings.range_days = self._days()
        self.settings.metric = self._metric()
        self.refresh()

    def _apply_interval(self) -> None:
        seconds = INTERVAL_OPTIONS[self.interval_combo.currentIndex()][1]
        self.settings.interval = seconds
        self.refresh_timer.stop()
        if seconds > 0:
            self.refresh_timer.start(seconds * 1000)
        self._update_status()

    def open_settings(self, focus_provider: str = "") -> None:
        dialog = ProviderSettingsDialog(
            self.providers, self.settings, self.theme, focus_provider, self
        )
        dialog.changed.connect(self._on_settings_changed)
        dialog.exec()

    def _on_settings_changed(self) -> None:
        self._push_all_credentials()
        if self.mode == WIDGET:
            self.setWindowOpacity(self.settings.opacity)
            self._set_always_on_top(self.settings.always_on_top)
        self.refresh()

    def _cycle_theme(self) -> None:
        order = ["system", "light", "dark"]
        self.theme_name = order[(order.index(self.theme_name) + 1) % len(order)]
        self.settings.theme = self.theme_name
        self._apply_theme(theming.resolve(self.theme_name))

    def _apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        QApplication.instance().setPalette(theming.build_qpalette(theme))
        self.theme_button.setText(
            {"system": "Theme: System", "light": "Theme: Light", "dark": "Theme: Dark"}[
                self.theme_name
            ]
        )
        self.setStyleSheet(self._stylesheet(theme))
        self.title_label.setStyleSheet(
            f"color: {theme.ink}; font-size: 17px; font-weight: 600;"
        )
        self.account_label.setStyleSheet(
            f"color: {theme.ink_secondary}; font-size: 11px;"
        )
        self.status.setStyleSheet(f"color: {theme.ink_muted}; font-size: 11px;")
        for page in self.pages.values():
            page.apply_theme(theme)
        self.compact.apply_theme(theme)

    def _stylesheet(self, theme: Theme) -> str:
        border = qcolor(theme.border)
        ring = (
            f"rgba({border.red()}, {border.green()}, {border.blue()},"
            f" {border.alphaF():.2f})"
        )
        return f"""
        QMainWindow, QScrollArea, QScrollArea > QWidget > QWidget {{
            background-color: {theme.plane};
        }}
        QWidget {{ color: {theme.ink}; }}
        QTabWidget::pane {{ border: none; background: transparent; }}
        QTabBar::tab {{
            background: transparent;
            color: {theme.ink_secondary};
            padding: 7px 14px;
            margin-right: 2px;
            border: none;
            border-bottom: 2px solid transparent;
            font-size: 12px;
        }}
        QTabBar::tab:selected {{
            color: {theme.ink};
            font-weight: 600;
            border-bottom: 2px solid {theme.accent};
        }}
        QTabBar::tab:hover {{ color: {theme.ink}; }}
        QComboBox, QPushButton, QLineEdit, QDoubleSpinBox {{
            background-color: {theme.surface};
            border: 1px solid {ring};
            border-radius: 6px;
            padding: 5px 10px;
            color: {theme.ink};
            font-size: 12px;
            min-height: 18px;
        }}
        QComboBox:hover, QPushButton:hover {{ border-color: {theme.accent}; }}
        QPushButton[accent="true"] {{
            background-color: {theme.accent};
            border-color: {theme.accent};
            color: #ffffff;
            font-weight: 600;
        }}
        QPushButton[accent="true"]:disabled {{
            background-color: {theme.track};
            border-color: {theme.track};
            color: {theme.ink_muted};
        }}
        QComboBox::drop-down {{ border: none; width: 18px; }}
        QComboBox QAbstractItemView {{
            background-color: {theme.surface};
            border: 1px solid {ring};
            selection-background-color: {theme.accent};
            selection-color: #ffffff;
            outline: none;
        }}
        QGroupBox {{
            border: 1px solid {ring};
            border-radius: 8px;
            margin-top: 14px;
            padding-top: 10px;
            font-weight: 600;
        }}
        QGroupBox::title {{ left: 10px; padding: 0 4px; }}
        QMenu {{
            background-color: {theme.surface};
            border: 1px solid {ring};
            padding: 4px;
        }}
        QMenu::item {{ padding: 5px 22px 5px 12px; border-radius: 4px; }}
        QMenu::item:selected {{ background-color: {theme.accent}; color: #ffffff; }}
        QScrollBar:vertical {{ background: transparent; width: 10px; margin: 0; }}
        QScrollBar::handle:vertical {{
            background: {theme.baseline}; border-radius: 5px; min-height: 30px;
        }}
        QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; }}
        QScrollBar::add-page, QScrollBar::sub-page {{ background: transparent; }}
        QToolTip {{
            background-color: {theme.surface};
            color: {theme.ink};
            border: 1px solid {ring};
            padding: 6px 8px;
        }}
        """

    # -- lifecycle --------------------------------------------------------

    def closeEvent(self, event) -> None:  # noqa: N802 - Qt naming
        self._save_geometry(self.mode)
        self.refresh_timer.stop()
        self.tick_timer.stop()
        self.hide()
        self.thread.quit()
        if not self.thread.wait(2000):
            # A request is still unwinding. Destroying a running QThread aborts
            # the process, so wait out the remaining socket timeout instead.
            self.thread.wait(api.TIMEOUT_SECONDS * 1000 + 2000)
        super().closeEvent(event)
        # Quit explicitly rather than relying on quitOnLastWindowClosed: this
        # window changes flags at runtime, and a frameless/always-on-top window
        # is easy to get wrong in a way that leaves an invisible live process.
        QApplication.instance().quit()


def _index_of(options: list[tuple[str, int]], value: int, default: int) -> int:
    for index, (_, candidate) in enumerate(options):
        if candidate == value:
            return index
    return default
