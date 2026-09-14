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

Commands live in the menu bar (File / Settings / About); the header row keeps
the controls that change what the figures mean - metric, range, interval -
plus the two buttons pressed often enough that a menu would be in the way,
Widget and Refresh. Widget mode hides the bar, so every menu action is added
to the window as well and its shortcut keeps working while the bar is hidden.
"""

from __future__ import annotations

import datetime as dt
import hashlib

from PySide6.QtCore import QPoint, QSize, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QAction, QActionGroup, QKeySequence
from PySide6.QtWidgets import (
    QApplication,
    QComboBox,
    QHBoxLayout,
    QLabel,
    QMainWindow,
    QMenu,
    QPushButton,
    QSlider,
    QStackedWidget,
    QTabWidget,
    QVBoxLayout,
    QWidgetAction,
    QWidget,
)

from . import (
    api,
    detection,
    formatting,
    memory,
    report as reporting,
    startup,
    theme as theming,
)
from .about_dialog import DeveloperDialog, VersionDialog
from .connect_dialog import ConnectDialog
from .connections_page import ConnectionsPage
from .dashboard import ProviderPage
from .log_dialog import UsageLogDialog, print_preview
from .providers import build_all
from .readme_dialog import LicenceDialog, NoticesDialog, ReadmeDialog
from .settings import (
    DASHBOARD_DRAG_MIN_H,
    DASHBOARD_DRAG_MIN_W,
    DASHBOARD_MIN_H,
    DASHBOARD_MIN_W,
    INTERVAL_OPTIONS,
    OPACITY_MAX,
    OPACITY_MIN,
    OPACITY_STEP,
    RANGE_OPTIONS,
    Settings,
    THEME_OPTIONS,
    WIDGET_MAX_EDGE,
    WIDGET_MIN_H,
    WIDGET_MIN_W,
    WIDGET_TRIGGER_EDGE,
)
from .settings_dialog import ProviderSettingsDialog
from .theme import Theme, qcolor
from .tray import TrayController
from .usage_log import METRICS
from .widgets.compact import CompactView
from .worker import RefreshResult, RefreshWorker

DASHBOARD = "dashboard"
WIDGET = "widget"
CONNECTIONS = "connections"


# Slower than the tray's two seconds, deliberately. The tray shows one
# number in one glyph, so a quick cycle reads fine; the widget shows a row
# of meters with captions and a reset line, and swapping all of that every
# two seconds gave the eye no time to finish reading a service before it
# was replaced.
WIDGET_ROTATE_MS = 4000


class MainWindow(QMainWindow):
    request_refresh = Signal(int, str, bool, str)
    push_credentials = Signal(str, str, str)
    push_enabled = Signal(str, bool)

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
        self._display_check = 0
        self._pending_rescale = False
        self._last_available = None
        self._window_size_seen = settings.window_size
        self._detections: dict[str, object] = {}
        # The usage log is modeless so the dashboard can keep refreshing
        # behind it; one at a time, kept current rather than reopened.
        self._log_dialog: UsageLogDialog | None = None

        # Providers are mirrored here purely for display metadata; the worker
        # owns the instances that actually do the fetching.
        self.providers = build_all()
        self.pages: dict[str, ProviderPage] = {}

        self._quitting = False

        self.setWindowTitle("AI Usage Monitor")
        # Deliberately below WIDGET_TRIGGER_EDGE so the window can still be
        # dragged small enough to collapse into the widget. A squeezed
        # dashboard is prevented by validating stored geometry on save and on
        # restore, not by pinning the minimum above the collapse threshold -
        # doing that killed the drag gesture entirely.
        self.setMinimumSize(DASHBOARD_DRAG_MIN_W, DASHBOARD_DRAG_MIN_H)

        self._build_ui()
        self._start_worker()
        self._push_all_credentials()
        self._apply_theme(self.theme)
        self._restore_geometry(DASHBOARD, self._default_window_size())

        # Settle start-with-Windows against the registry. After the first
        # launch the registry wins, so turning the entry off in Windows'
        # Startup Apps page sticks instead of being re-added here.
        effective = startup.reconcile(
            self.settings.start_with_windows,
            first_run=self.settings.mark_once("startupApplied"),
        )
        if effective != self.settings.start_with_windows:
            self.settings.start_with_windows = effective

        self.tray: TrayController | None = None
        if TrayController.available():
            self.tray = TrayController(self.providers, self.theme, self)
            self.tray.resume_requested.connect(self.resume_from_tray)
            self.tray.widget_requested.connect(self.show_widget_from_tray)
            self.tray.settings_requested.connect(lambda: self.open_settings(""))
            self.tray.exit_requested.connect(self.quit_app)
            self.tray.refresh_requested.connect(self.refresh)
            self.tray.show()

        # The landing page is the connections screen unless it has been turned
        # off; either way detection has already run via _push_all_credentials.
        if self.settings.show_connections_at_startup:
            self.mode = CONNECTIONS
            self.stack.setCurrentWidget(self.connections)
        else:
            self.mode = DASHBOARD
            self.stack.setCurrentWidget(self.dashboard_page)

        self.refresh_timer = QTimer(self)
        self.refresh_timer.timeout.connect(self.refresh)
        self._apply_interval()

        self.tick_timer = QTimer(self)
        self.tick_timer.setInterval(1000)
        self.tick_timer.timeout.connect(self._tick)
        self.tick_timer.start()

        self._fit_timer = QTimer(self)
        self._fit_timer.setSingleShot(True)
        self._fit_timer.timeout.connect(self._fit_to_screen)
        self._watch_screens()

        self._sync_menus()
        QTimer.singleShot(0, self.refresh)

    # -- construction -----------------------------------------------------

    def _build_ui(self) -> None:
        self._build_menus()
        self.stack = QStackedWidget()
        self.setCentralWidget(self.stack)

        # Page 0: the landing page - every service detected the same way.
        self.connections = ConnectionsPage(self.providers, self.theme)
        self.connections.connect_requested.connect(self.open_connect_dialog)
        self.connections.redetect_requested.connect(self.redetect)
        self.connections.continue_requested.connect(self.enter_dashboard_mode)
        self.connections.startup_pref_changed.connect(
            self._set_startup_preference
        )
        self.connections.set_startup_preference(
            self.settings.show_connections_at_startup
        )
        self.stack.addWidget(self.connections)

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
        # Widget rotation: the tray has always cycled services every two
        # seconds, so a widget frozen on one service read as broken beside
        # it. Pinning a service from the Show menu stops the timer.
        self._widget_pinned: str | None = None
        self._rotate_index = 0
        self._rotate_widget = QTimer(self)
        self._rotate_widget.setInterval(WIDGET_ROTATE_MS)
        self._rotate_widget.timeout.connect(self._advance_widget)
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
        self.title_label = QLabel("AI Usage Monitor")
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

        # Connections, Settings, Readme and the theme cycle used to sit here as
        # well. They are commands you reach for occasionally, and they now live
        # in the menu bar. Widget and Refresh stayed: collapsing to the desk
        # widget is the gesture this app is used through all day, and burying a
        # daily action two clicks deep to tidy a toolbar is a bad trade. Both
        # are in the menus too, which is where their shortcuts are declared.
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

    # -- menu bar ---------------------------------------------------------

    def _build_menus(self) -> None:
        """File / Settings / About, per the Windows convention.

        Every entry here is a command or a preference. The header row keeps
        the three controls that change what the numbers below it mean - metric,
        range and interval - plus the two buttons used often enough that a menu
        would be in the way: Widget and Refresh.
        """
        bar = self.menuBar()
        bar.setNativeMenuBar(False)

        file_menu = bar.addMenu("&File")
        self._add_action(
            file_menu, "&Refresh", self.refresh, "F5",
            "Fetch every enabled service now",
        )
        self._add_action(
            file_menu, "&Sign-in…", self.enter_connections_mode, None,
            "Manage sign-ins for all AI services",
        )
        file_menu.addSeparator()
        self._add_action(
            file_menu, "Save to &Log…", self.open_usage_log, "Ctrl+L",
            "Read the per-day usage log, then save or print it",
        )
        self._add_action(
            file_menu, "&Print Report…", self.print_report, "Ctrl+P",
            "Print the usage log",
        )
        file_menu.addSeparator()
        self._add_action(file_menu, "E&xit", self.quit_app, "Ctrl+Q")

        settings_menu = bar.addMenu("&Settings")
        self.startup_action = QAction("Start on start up", self)
        self.startup_action.setCheckable(True)
        self.startup_action.setChecked(self.settings.start_with_windows)
        self.startup_action.toggled.connect(self._set_start_with_windows)
        settings_menu.addAction(self.startup_action)

        theme_menu = settings_menu.addMenu("Themes")
        self.theme_group = QActionGroup(self)
        self.theme_group.setExclusive(True)
        self.theme_actions: dict[str, QAction] = {}
        for label, value in THEME_OPTIONS:
            action = QAction(label, self)
            action.setCheckable(True)
            action.setChecked(value == self.theme_name)
            action.triggered.connect(lambda _=False, v=value: self._set_theme(v))
            self.theme_group.addAction(action)
            theme_menu.addAction(action)
            self.theme_actions[value] = action

        self.widget_action = QAction("Widget mode", self)
        self.widget_action.setCheckable(True)
        self.widget_action.setShortcut(QKeySequence("Ctrl+W"))
        self.widget_action.setToolTip("Collapse to the desk widget")
        self.widget_action.triggered.connect(self._toggle_widget_action)
        settings_menu.addAction(self.widget_action)
        # Ctrl+W is the way back out of the widget, where the menu is hidden.
        self.addAction(self.widget_action)

        settings_menu.addSeparator()
        self._add_action(
            settings_menu, "&Settings…", lambda: self.open_settings(""), None,
            "Services, appearance and startup",
        )

        about_menu = bar.addMenu("&About")
        self._add_action(
            about_menu, "&Version…", self.open_version, None,
            "Which build this is, and whether a newer one has been released",
        )
        self._add_action(
            about_menu, "&Readme", self.open_readme, "F1",
            "What every figure means, including how the quota counts tokens",
        )
        self._add_action(
            about_menu, "&License Agreement", self.open_licence, None,
            "GPL-3.0-or-later, the licence this program is given to you under",
        )
        self._add_action(about_menu, "Third-party &notices", self.open_notices)
        about_menu.addSeparator()
        self._add_action(about_menu, "About the &developer", self.open_developer)

    def _add_action(
        self,
        menu: QMenu,
        text: str,
        slot,
        shortcut: str | None = None,
        tip: str = "",
    ) -> QAction:
        action = QAction(text, self)
        if shortcut:
            action.setShortcut(QKeySequence(shortcut))
        if tip:
            action.setToolTip(tip)
            action.setStatusTip(tip)
        action.triggered.connect(lambda _=False: slot())
        menu.addAction(action)
        # Also a window action, so its shortcut still fires in widget mode,
        # where the menu bar is hidden and a menu-only shortcut goes dead.
        self.addAction(action)
        return action

    def _sync_menus(self) -> None:
        """Keep the menu's checkmarks honest about the live state.

        The same preferences are reachable from the Settings dialog, the tray
        and a drag on the window edge, so the menu has to be told rather than
        assumed to be the only writer.
        """
        if not hasattr(self, "startup_action"):
            return
        self.startup_action.blockSignals(True)
        self.startup_action.setChecked(self.settings.start_with_windows)
        self.startup_action.blockSignals(False)

        action = self.theme_actions.get(self.theme_name)
        if action is not None and not action.isChecked():
            action.setChecked(True)

        self.widget_action.blockSignals(True)
        self.widget_action.setChecked(self.mode == WIDGET)
        self.widget_action.blockSignals(False)

    def _toggle_widget_action(self, checked: bool) -> None:
        if checked:
            self.enter_widget_mode()
        else:
            self.enter_dashboard_mode()
        self._sync_menus()

    def _set_start_with_windows(self, enabled: bool) -> None:
        self.settings.start_with_windows = enabled
        startup.apply(enabled)

    def _set_theme(self, name: str) -> None:
        self.theme_name = name
        self.settings.theme = name
        self._apply_theme(theming.resolve(name))

    # -- worker -----------------------------------------------------------

    def _start_worker(self) -> None:
        self.thread = QThread(self)
        self.worker = RefreshWorker()
        self.worker.moveToThread(self.thread)
        self.request_refresh.connect(self.worker.refresh)
        self.push_credentials.connect(self.worker.apply_credentials)
        self.push_enabled.connect(self.worker.set_enabled)
        self.worker.finished.connect(self._on_result)
        self.thread.start()

    def _push_all_credentials(self) -> None:
        for provider in self.providers:
            key = self.settings.provider_key(provider.id)
            extra = self.settings.provider_extra(provider.id)
            self.push_credentials.emit(provider.id, key, extra)
            self.push_enabled.emit(
                provider.id, self.settings.provider_enabled(provider.id)
            )
            # The worker owns the instances that fetch; these local copies exist
            # so detection can run on the GUI thread (it is filesystem-only).
            provider.configure(key, extra)
        self.redetect()

    # -- connections ------------------------------------------------------

    def redetect(self, provider_id: str = "") -> None:
        """Re-run sign-in detection. Cheap enough for the GUI thread."""
        for provider in self.providers:
            if provider_id and provider.id != provider_id:
                continue
            try:
                self._detections[provider.id] = provider.detect()
            except Exception:  # noqa: BLE001 - never let one service break the page
                continue
        self.connections.set_detections(self._detections)

    def open_connect_dialog(self, provider_id: str) -> None:
        provider = next(
            (p for p in self.providers if p.id == provider_id), None
        )
        if provider is None:
            return
        detection = self._detections.get(provider_id) or provider.detect()
        dialog = ConnectDialog(provider, detection, self.settings, self.theme, self)
        dialog.setStyleSheet(self._stylesheet(self.theme))
        if dialog.exec():
            self._push_all_credentials()
            self.refresh()

    def _set_startup_preference(self, show: bool) -> None:
        self.settings.show_connections_at_startup = show

    def enter_connections_mode(self) -> None:
        if self.mode == WIDGET:
            self.enter_dashboard_mode()
        self.mode = CONNECTIONS
        self.redetect()
        self.stack.setCurrentWidget(self.connections)
        self._sync_menus()

    def refresh(self) -> None:
        if self._refreshing:
            return
        self._refreshing = True
        self.refresh_button.setEnabled(False)
        self.refresh_button.setText("Refreshing…")
        want_history = self.mode != WIDGET
        # Widget mode normally fetches only the service on screen. While the
        # widget is rotating it shows all of them, so all of them have to be
        # fetched - history stays off either way, which is the expensive part.
        only = ""
        if self.mode == WIDGET and not self._rotate_widget.isActive():
            only = self._widget_provider_id()
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

        self._sync_widget_rotation()
        show_history = self.mode != WIDGET
        for provider_id, snapshot in result.snapshots.items():
            page = self.pages.get(provider_id)
            if page is not None:
                page.render(snapshot, show_history)

        # Every provider re-detects its sign-in on the way to fetching, so the
        # connections page can be as fresh as the figures instead of frozen at
        # whatever was true when it was last opened by hand.
        detection.adopt(self._detections, result.snapshots)
        self.connections.set_detections(self._detections)
        if self._log_dialog is not None:
            self._log_dialog.set_report(self._build_report())

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
        if self.tray is not None:
            self.tray.set_snapshots(result.snapshots)
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

    def _widget_provider_id(self) -> str:
        """Which service the widget shows: the pinned one, or the rotation."""
        if self._widget_pinned:
            return self._widget_pinned
        if not self.settings.widget_rotate:
            return self._active_provider_id()
        showable = self._rotatable_ids()
        if not showable:
            return self._active_provider_id()
        self._rotate_index %= len(showable)
        return showable[self._rotate_index]

    def _rotatable_ids(self) -> list[str]:
        """Services worth cycling through: the ones that returned data."""
        if self.last_result is None:
            return [p.id for p in self.providers]
        ids = [
            p.id for p in self.providers
            if (snap := self.last_result.snapshots.get(p.id)) is not None
            and snap.configured
        ]
        return ids or [p.id for p in self.providers]

    def _advance_widget(self) -> None:
        showable = self._rotatable_ids()
        if len(showable) < 2:
            return
        self._rotate_index = (self._rotate_index + 1) % len(showable)
        self._render_compact()

    def _sync_widget_rotation(self) -> None:
        """Run the timer only while the widget is up and unpinned."""
        rotate = (
            self.mode == WIDGET
            and self.settings.widget_rotate
            and self._widget_pinned is None
            and len(self._rotatable_ids()) > 1
        )
        if rotate and not self._rotate_widget.isActive():
            self._rotate_widget.start()
        elif not rotate and self._rotate_widget.isActive():
            self._rotate_widget.stop()

    def _active_provider_id(self) -> str:
        index = self.tabs.currentIndex()
        if 0 <= index < len(self.providers):
            return self.providers[index].id
        return self.providers[0].id if self.providers else ""

    def toggle_mode(self) -> None:
        if self.mode == WIDGET:
            self.enter_dashboard_mode()
        else:
            self.enter_widget_mode()

    def enter_widget_mode(self) -> None:
        if self.mode == WIDGET or self._switching:
            return
        self._switching = True
        self._save_geometry(DASHBOARD)
        # Expanding always returns to the dashboard, so make sure it is the
        # page underneath before the window shrinks.
        self.stack.setCurrentWidget(self.dashboard_page)

        self.mode = WIDGET
        self.stack.setCurrentWidget(self.compact)
        # A frameless 230x175 widget has no room for a menu bar, and one drawn
        # across the top would eat a third of it.
        self.menuBar().setVisible(False)
        # Charts hold the largest arrays on the page; let them go.
        for page in self.pages.values():
            page.release_charts()

        flags = self._widget_flags()
        if self.settings.always_on_top:
            flags |= Qt.WindowType.WindowStaysOnTopHint
        self.setWindowFlags(flags)
        self.setWindowOpacity(self.settings.opacity)
        self.setMinimumSize(WIDGET_MIN_W, WIDGET_MIN_H)
        self.setMaximumSize(WIDGET_MAX_EDGE, WIDGET_MAX_EDGE)
        # Geometry is applied after show(): changing window flags re-creates the
        # native window, and a geometry set before that is discarded.
        self.show()
        self._sync_widget_rotation()
        # The smallest size that still fits all three meters with their reset
        # line - 260x230 left dead space under the content and read as
        # oversized for a desk widget. Only new installs see this; a saved
        # widget geometry still wins.
        self._restore_geometry(WIDGET, QSize(230, 175))
        memory.trim_working_set()
        self._switching = False
        self._fit_to_screen()
        self._render_compact()
        self._sync_menus()
        self.refresh()

    @staticmethod
    def _widget_flags() -> Qt.WindowType:
        """Frameless, but deliberately NOT Qt.Tool.

        A Tool window keeps no taskbar button, which strands the widget if
        always-on-top is off, and Qt excludes Tool windows from
        `lastWindowClosed` - closing the widget then hid the window and left the
        process running invisibly.
        """
        return Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint

    def enter_dashboard_mode(self) -> None:
        if self.mode == DASHBOARD or self._switching:
            return
        if self.mode == CONNECTIONS:
            # No window chrome change needed - just swap the page.
            self.mode = DASHBOARD
            self.stack.setCurrentWidget(self.dashboard_page)
            self._sync_menus()
            self.refresh()
            return
        self._switching = True
        self._save_geometry(WIDGET)

        self.mode = DASHBOARD
        self.stack.setCurrentWidget(self.dashboard_page)
        self.menuBar().setVisible(True)
        self.setWindowFlags(Qt.WindowType.Window)
        self.setWindowOpacity(1.0)
        self._rotate_widget.stop()
        self.setMaximumSize(16777215, 16777215)
        self.setMinimumSize(DASHBOARD_DRAG_MIN_W, DASHBOARD_DRAG_MIN_H)
        self.show()
        self._restore_geometry(DASHBOARD, self._default_window_size())
        self._switching = False
        # Settle it against the screen now rather than waiting for the display
        # poll: a remembered rect can outlive the usable area that produced it
        # (a taskbar that changed size, a scale-factor change), and nothing
        # would otherwise notice until the geometry happened to change again.
        self._fit_to_screen()
        self._sync_menus()
        self.refresh()

    def resizeEvent(self, event) -> None:  # noqa: N802 - Qt naming
        super().resizeEvent(event)
        if self._switching or self.mode == WIDGET:
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

            rotating = self.settings.widget_rotate and self._widget_pinned is None
            every = QAction("All services (rotate)", provider_menu)
            every.setCheckable(True)
            every.setChecked(rotating)
            every.triggered.connect(self._rotate_all_providers)
            group.addAction(every)
            provider_menu.addAction(every)
            provider_menu.addSeparator()

            showing = self._widget_provider_id()
            for index, provider in enumerate(self.providers):
                action = QAction(provider.display_name, provider_menu)
                action.setCheckable(True)
                action.setChecked(not rotating and provider.id == showing)
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

        menu.addAction(self._build_opacity_slider(menu))
        menu.addSeparator()
        quit_action = QAction("Quit", menu)
        quit_action.triggered.connect(self.close)
        menu.addAction(quit_action)

        menu.exec(QPoint(int(global_pos.x()), int(global_pos.y())))

    def _build_opacity_slider(self, menu: QMenu) -> QWidgetAction:
        """A live slider rather than a submenu of fixed steps.

        Opacity is the one setting you judge by eye, so the value applies while
        dragging - picking 70% from a list meant closing the menu to see the
        result, then reopening it to try again.
        """
        action = QWidgetAction(menu)
        holder = QWidget()
        row = QHBoxLayout(holder)
        row.setContentsMargins(12, 4, 12, 6)
        row.setSpacing(8)

        caption = QLabel("Opacity")
        caption.setStyleSheet(f"color: {self.theme.ink_secondary}; font-size: 12px;")
        row.addWidget(caption)

        slider = QSlider(Qt.Orientation.Horizontal)
        slider.setRange(OPACITY_MIN, OPACITY_MAX)
        slider.setSingleStep(OPACITY_STEP)
        slider.setPageStep(OPACITY_STEP * 2)
        slider.setValue(int(round(self.settings.opacity * 100)))
        slider.setMinimumWidth(140)
        row.addWidget(slider, 1)

        readout = QLabel(f"{slider.value()}%")
        readout.setMinimumWidth(34)
        readout.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        readout.setStyleSheet(
            f"color: {self.theme.ink}; font-size: 12px; font-weight: 600;"
        )
        row.addWidget(readout)

        def changed(value: int) -> None:
            readout.setText(f"{value}%")
            self._set_opacity(value / 100)

        slider.valueChanged.connect(changed)
        action.setDefaultWidget(holder)
        return action

    def _select_provider(self, index: int) -> None:
        self.tabs.setCurrentIndex(index)
        if self.mode == WIDGET:
            # An explicit choice pins the widget: rotating away from what the
            # user just asked for would undo the click a second later.
            self._widget_pinned = self.providers[index].id
            self.settings.widget_rotate = False
            self._sync_widget_rotation()
        self._render_compact()
        self.refresh()

    def _rotate_all_providers(self) -> None:
        self._widget_pinned = None
        self.settings.widget_rotate = True
        self._sync_widget_rotation()
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
        provider_id = self._widget_provider_id()
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

    # -- tray entry points -------------------------------------------------

    def resume_from_tray(self) -> None:
        """Bring the full window back, whatever state it was parked in."""
        if self.mode == WIDGET:
            self.enter_dashboard_mode()
        elif self.mode == CONNECTIONS:
            self.stack.setCurrentWidget(self.connections)
        self._unhide()

    def show_widget_from_tray(self) -> None:
        if self.mode != WIDGET:
            self.enter_widget_mode()
        self._unhide()

    def _unhide(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()
        # The desktop may have been reconfigured while the window was parked,
        # and the tick that would notice was idle. Check on the way back.
        self._schedule_rescale()
        if self.last_result is not None:
            self._on_result(self.last_result)

    def quit_app(self) -> None:
        """The only path that really exits while minimize-to-tray is on."""
        self._quitting = True
        self.close()

    # -- geometry ---------------------------------------------------------

    def _default_window_size(self) -> QSize:
        width, height = self.settings.window_size
        if width and height:
            return QSize(width, height)
        return QSize(1120, 820)

    def _display_signature(self) -> str:
        """A short, stable id for the current monitor layout.

        Covers each screen's position, size and scale factor, so a laptop panel
        and the same laptop docked to a 4K monitor are different layouts and
        keep their own window positions.
        """
        parts = []
        for screen in sorted(
            QApplication.screens(),
            key=lambda s: (s.geometry().x(), s.geometry().y()),
        ):
            geo = screen.geometry()
            parts.append(
                f"{geo.x()},{geo.y()},{geo.width()}x{geo.height()}"
                f"@{screen.devicePixelRatio():.2f}"
            )
        raw = "|".join(parts) or "none"
        return "d" + hashlib.sha1(raw.encode("utf-8")).hexdigest()[:10]

    def _save_geometry(self, mode: str) -> None:
        if self.isMinimized() or self.isMaximized() or not self.isVisible():
            return
        rect = self.geometry()
        if mode == DASHBOARD and (
            rect.width() < WIDGET_TRIGGER_EDGE or rect.height() < WIDGET_TRIGGER_EDGE
        ):
            # This is the drag that collapsed the window, not a dashboard size
            # anyone wants back. Storing it would reopen the app squeezed.
            return
        if mode == WIDGET and (
            rect.width() > WIDGET_MAX_EDGE or rect.height() > WIDGET_MAX_EDGE
        ):
            # The mirror of the guard above. Saving here during a mode change,
            # before the window has been resized into widget bounds, stored a
            # full dashboard rect under the widget key - one machine had
            # "0 0 1920 1032" - which then reopened the widget clamped to its
            # 300x300 maximum instead of the intended default.
            return
        self.settings.save_geometry(
            mode,
            [rect.x(), rect.y(), rect.width(), rect.height()],
            display=self._display_signature(),
        )

    def _restore_geometry(self, mode: str, fallback: QSize) -> None:
        """Apply the saved rect for `mode`, falling back to a default size.

        An explicit rect is stored rather than `saveGeometry()`'s opaque blob:
        the blob encodes window state that does not survive the flag change
        between dashboard and widget, and restored the wrong size.
        """
        saved = self.settings.load_geometry(mode, display=self._display_signature())
        rect = None
        if isinstance(saved, (list, tuple)) and len(saved) == 4:
            try:
                rect = [int(value) for value in saved]
            except (TypeError, ValueError):
                rect = None

        # The preset is a *default*, not a cage: it seeds the size when there is
        # nothing remembered and is the target after a display change, but it
        # must not overwrite a size the user dragged to every time the window
        # comes back from widget mode.
        if (
            rect
            and mode == DASHBOARD
            and (rect[2] < WIDGET_TRIGGER_EDGE or rect[3] < WIDGET_TRIGGER_EDGE)
        ):
            rect = None

        if (
            rect
            and mode == WIDGET
            and (rect[2] > WIDGET_MAX_EDGE or rect[3] > WIDGET_MAX_EDGE)
        ):
            # Written by an older build before the save guard existed. Falling
            # back repairs it: the next save stores a sane rect.
            rect = None

        if rect and rect[2] > 0 and rect[3] > 0 and self._on_a_screen(rect):
            self.setGeometry(*rect)
            return

        # No usable saved position. Resizing alone would leave the window
        # wherever it happened to be - which, coming back from a widget the
        # user had dragged into a corner, is nowhere sensible. Centre it.
        screen = self.screen() or QApplication.primaryScreen()
        if screen is None:
            self.resize(fallback)
            return
        available = screen.availableGeometry()
        width = min(fallback.width(), available.width())
        height = min(fallback.height(), available.height())
        self.setGeometry(
            available.left() + (available.width() - width) // 2,
            available.top() + (available.height() - height) // 2,
            width,
            height,
        )

    # -- display changes ---------------------------------------------------

    def _watch_screens(self) -> None:
        """Follow resolution changes, monitors coming and going, and DPI moves.

        A window sized for a 2560-wide display is simply too big after a switch
        to 1280, and Qt will not resize it for anyone - it just leaves it
        hanging off the edge. These signals are the only notice we get.
        """
        app = QApplication.instance()
        app.primaryScreenChanged.connect(self._schedule_fit)
        app.screenAdded.connect(self._attach_screen)
        app.screenRemoved.connect(lambda _screen: self._schedule_fit())
        for screen in app.screens():
            self._attach_screen(screen)

    def _attach_screen(self, screen) -> None:
        screen.geometryChanged.connect(self._schedule_rescale)
        screen.availableGeometryChanged.connect(self._schedule_rescale)
        screen.logicalDotsPerInchChanged.connect(self._schedule_rescale)
        self._schedule_fit()

    def _schedule_fit(self, *_args) -> None:
        # A resolution change fires several of these in a burst; settle first,
        # then fit once against the final geometry.
        self._fit_timer.start(250)

    def _schedule_rescale(self, *_args) -> None:
        """A display change: re-apply the preferred size, not just clamp."""
        self._pending_rescale = True
        self._fit_timer.start(250)

    def _check_display(self) -> None:
        """Poll the usable area as a backstop for the screen signals.

        Windows does not reliably raise `availableGeometryChanged` when the
        resolution of an existing monitor changes - only when monitors come and
        go - so signals alone left the window stranded at the old size. One
        cached geometry read is cheap enough to just check.
        """
        screen = self.screen() or QApplication.primaryScreen()
        if screen is None:
            return
        available = screen.availableGeometry()
        if available == self._last_available:
            return
        self._last_available = available
        self._schedule_rescale()

    def _fit_to_screen(self) -> None:
        """Keep the window inside the screen, and sized for it.

        Clamping alone was not enough: shrinking the desktop moved the window
        in, but enlarging it left the window stuck at the smaller size. On a
        display change the preferred size is re-applied (bounded by the screen),
        so the window grows back as well as shrinks.
        """
        rescale = self._pending_rescale
        self._pending_rescale = False

        if self._switching or not self.isVisible() or self.isMinimized():
            return
        screen = self.screen() or QApplication.primaryScreen()
        if screen is None:
            return
        available = screen.availableGeometry()
        self._last_available = available

        if self.mode == WIDGET:
            floor_w, floor_h = 180, 120
        else:
            # Fit to a sane dashboard size where the screen allows it, but the
            # draggable minimum stays low so collapsing still works.
            floor_w = min(DASHBOARD_MIN_W, available.width())
            floor_h = min(DASHBOARD_MIN_H, available.height())
            drag_w = min(DASHBOARD_DRAG_MIN_W, available.width())
            drag_h = min(DASHBOARD_DRAG_MIN_H, available.height())
            if self.minimumWidth() != drag_w or self.minimumHeight() != drag_h:
                self.setMinimumSize(drag_w, drag_h)

        rect = self.geometry()
        if rescale and self.mode == DASHBOARD:
            # The display changed: aim for the preferred size again rather than
            # keeping whatever the old screen forced the window down to.
            preferred = self._default_window_size()
            target_w, target_h = preferred.width(), preferred.height()
        else:
            target_w, target_h = rect.width(), rect.height()

        width = max(floor_w, min(target_w, available.width()))
        height = max(floor_h, min(target_h, available.height()))
        x = min(max(rect.x(), available.left()), available.right() - width + 1)
        y = min(max(rect.y(), available.top()), available.bottom() - height + 1)

        if (x, y, width, height) == (rect.x(), rect.y(), rect.width(), rect.height()):
            return
        self._switching = True          # our own resize must not trigger collapse
        self.setGeometry(x, y, width, height)
        self._switching = False
        # Re-lay the page against the new size and repaint with current data,
        # so the view updates without waiting for the next network refresh.
        if self.last_result is not None:
            self._on_result(self.last_result)
        else:
            self.update()

    def _on_a_screen(self, rect: list[int]) -> bool:
        """Guard against restoring onto a monitor that is no longer attached."""
        centre = QPoint(rect[0] + rect[2] // 2, rect[1] + rect[3] // 2)
        return any(
            screen.availableGeometry().contains(centre)
            for screen in QApplication.screens()
        )

    # -- periodic ---------------------------------------------------------

    def _tick(self) -> None:
        # Parked in the tray there is nothing on screen to keep current: the
        # tray icon and its tooltip are redrawn by the refresh and rotation
        # timers, not by this one. Idling here is the difference between waking
        # the CPU every second all day and not.
        if not self.isVisible():
            return

        self._display_check += 1
        if self._display_check >= 2:
            self._display_check = 0
            self._check_display()

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

    def open_readme(self) -> None:
        """Show the shipped README. One copy, read from disk, never duplicated
        into the source as a second version that can drift."""
        ReadmeDialog(self.theme, self).exec()

    def open_licence(self) -> None:
        LicenceDialog(self.theme, self).exec()

    def open_notices(self) -> None:
        NoticesDialog(self.theme, self).exec()

    def open_version(self) -> None:
        VersionDialog(self.theme, self).exec()

    def open_developer(self) -> None:
        DeveloperDialog(self.theme, self).exec()

    # -- usage log --------------------------------------------------------

    def _build_report(self) -> reporting.Report:
        """The log of what is on screen right now, per service and per day."""
        snapshots = self.last_result.snapshots if self.last_result else {}
        return reporting.build(
            self.providers,
            snapshots,
            self._detections,
            self._days(),
            self._metric(),
        )

    def open_usage_log(self) -> None:
        """Read the log first; saving and printing are buttons inside it."""
        if self._log_dialog is not None:
            self._log_dialog.raise_()
            self._log_dialog.activateWindow()
            return
        dialog = UsageLogDialog(self._build_report(), self.theme, self)
        self._log_dialog = dialog
        dialog.finished.connect(self._on_log_closed)
        dialog.show()

    def _on_log_closed(self) -> None:
        self._log_dialog = None

    def print_report(self) -> None:
        """File > Print Report: the same document, straight to the preview."""
        print_preview(self._build_report(), self)

    # -- settings ---------------------------------------------------------

    def open_settings(self, focus_provider: str = "") -> None:
        dialog = ProviderSettingsDialog(
            self.providers, self.settings, self.theme, focus_provider, self
        )
        dialog.changed.connect(self._on_settings_changed)
        dialog.preview.connect(self._apply_appearance)
        dialog.exec()

    def _apply_appearance(self) -> None:
        """Re-read the appearance settings and show them, without refetching.

        The preview signal fires on every drag of the opacity slider, so this
        path must not touch the network, the providers or the worker - it is
        only what the window looks like.
        """
        if self.theme_name != self.settings.theme:
            self.theme_name = self.settings.theme
            self._apply_theme(theming.resolve(self.theme_name))

        size = self.settings.window_size
        if size != self._window_size_seen:
            self._window_size_seen = size
            if self.mode == DASHBOARD and all(size):
                self._pending_rescale = True
                self._fit_to_screen()
        if self.mode == WIDGET:
            self.setWindowOpacity(self.settings.opacity)
            self._set_always_on_top(self.settings.always_on_top)

    def _on_settings_changed(self) -> None:
        self._push_all_credentials()
        self._apply_appearance()
        self._sync_menus()
        self.refresh()

    def _apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        QApplication.instance().setPalette(theming.build_qpalette(theme))
        self._sync_menus()
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
        self.connections.apply_theme(theme)
        self.compact.apply_theme(theme)
        if self._log_dialog is not None:
            self._log_dialog.apply_theme(theme)
        if getattr(self, "tray", None) is not None:
            self.tray.apply_theme(theme)

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
        QMenuBar {{
            background-color: {theme.surface};
            color: {theme.ink};
            border-bottom: 1px solid {ring};
            padding: 2px 6px;
            font-size: 12px;
        }}
        QMenuBar::item {{ padding: 5px 10px; border-radius: 4px; background: transparent; }}
        QMenuBar::item:selected {{ background-color: {theme.plane}; }}
        QMenuBar::item:pressed {{ background-color: {theme.accent}; color: #ffffff; }}
        QMenu {{
            background-color: {theme.surface};
            border: 1px solid {ring};
            padding: 4px;
        }}
        QMenu::item {{ padding: 5px 22px 5px 12px; border-radius: 4px; }}
        QMenu::item:selected {{ background-color: {theme.accent}; color: #ffffff; }}
        QMenu::separator {{ height: 1px; background: {ring}; margin: 4px 8px; }}
        QSlider::groove:horizontal {{
            height: 4px; background: {theme.track}; border-radius: 2px;
        }}
        QSlider::sub-page:horizontal {{
            height: 4px; background: {theme.accent}; border-radius: 2px;
        }}
        QSlider::handle:horizontal {{
            width: 12px; height: 12px; margin: -5px 0;
            background: {theme.accent}; border: 2px solid {theme.surface};
            border-radius: 6px;
        }}
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
        # Closing the window parks the app in the tray rather than quitting,
        # unless Exit was chosen or there is no tray to park in.
        if (
            not self._quitting
            and self.settings.minimize_to_tray
            and self.tray is not None
            and self.tray.icon.isVisible()
        ):
            self._save_geometry(self.mode)
            self.hide()
            # Nothing is on screen now, so stop holding chart arrays and hand
            # the pages back to Windows. This is where the app spends most of
            # its life, and it should be its cheapest state.
            for page in self.pages.values():
                page.release_charts()
            memory.trim_working_set()
            event.ignore()
            if self.settings.mark_once("trayHint"):
                self.tray.notify(
                    "Still watching",
                    "AI Usage Monitor is in the notification area. "
                    "Double-click its icon for the menu, or use Exit to quit.",
                )
            return

        self._save_geometry(self.mode)
        self.refresh_timer.stop()
        self.tick_timer.stop()
        if self.tray is not None:
            self.tray.hide()
        self.hide()
        self.thread.quit()
        if not self.thread.wait(2000):
            # A request is still unwinding. Destroying a running QThread aborts
            # the process, so wait out the remaining socket timeout instead.
            self.thread.wait(api.TIMEOUT_SECONDS * 1000 + 2000)
        super().closeEvent(event)
        # Quit explicitly rather than relying on quitOnLastWindowClosed: this
        # window changes flags at runtime and the app runs with that behaviour
        # disabled so tray-only operation works.
        QApplication.instance().quit()


def _index_of(options: list[tuple[str, int]], value: int, default: int) -> int:
    for index, (_, candidate) in enumerate(options):
        if candidate == value:
            return index
    return default
