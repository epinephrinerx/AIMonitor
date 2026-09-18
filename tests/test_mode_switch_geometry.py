"""Switching modes takes one step, not two.

Reported from use: dragging the dashboard narrow made the window grow for a
beat before it collapsed into the widget.

It was the size limits doing it. `enter_widget_mode` set the widget maximum
of 300x300 and then showed the window, so Qt clamped the dashboard's rect to
exactly 300x300 and drew that; only afterwards did the saved widget geometry
land. Coming from a window already dragged narrow - or from a maximised one,
where `show()` put it briefly back at full screen - that intermediate frame
reads as the app expanding on its way to collapsing.

The same fault ran the other way: raising the minimum back to the draggable
300x220 drew the dashboard as a stub where the widget had been before it
jumped to full size.

Both now work out the destination first and apply it before the window is
shown, so exactly one size is ever painted.

A second, larger cause of the same complaint lived in `_fit_to_screen`. It
applied a comfortable dashboard floor of 760x560 on *every* call, not only
after a display change, so a routine fit landing mid-drag pulled a window
being dragged narrow back out to 760 wide. That is the app visibly resisting
the gesture, and expanding on its way to collapsing. Only a display change may
grow the window now.
"""

from __future__ import annotations

import os
import unittest
from unittest.mock import Mock, patch

SCOPE = "ModeSwitchGeometryTests"
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QEvent, QObject, QSettings, Qt  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor import startup  # noqa: E402
from ai_usage_monitor.main_window import DASHBOARD, WIDGET, MainWindow  # noqa: E402
from ai_usage_monitor.settings import (  # noqa: E402
    DASHBOARD_MIN_W,
    ORG,
    SCOPE_ENV_VAR,
    Settings,
    WIDGET_MAX_EDGE,
    WIDGET_MIN_H,
    WIDGET_MIN_W,
)
from ai_usage_monitor.tray import TrayController  # noqa: E402


class _SizeLog(QObject):
    """Every size the window is actually painted at, in order."""

    def __init__(self) -> None:
        super().__init__()
        self.sizes: list[tuple[str, int, int]] = []

    def eventFilter(self, obj, event):
        if event.type() == QEvent.Type.Resize:
            self.sizes.append((obj.mode, event.size().width(), event.size().height()))
        return False


class ModeSwitchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        patches = [
            patch.dict(os.environ, {SCOPE_ENV_VAR: SCOPE}),
            patch.object(startup, "set_enabled", lambda enabled: None),
            patch.object(startup, "reconcile", lambda default_on, first_run: default_on),
            patch.object(TrayController, "available", staticmethod(lambda: False)),
            patch.object(MainWindow, "_start_worker", self._fake_worker),
            patch.object(MainWindow, "_push_all_credentials", lambda self: None),
        ]
        for item in patches:
            item.start()
            self.addCleanup(item.stop)

        self.window = MainWindow(Settings())
        self.window.mode = DASHBOARD
        self.window.stack.setCurrentWidget(self.window.dashboard_page)
        self.window.show()
        self.app.processEvents()
        self.log = _SizeLog()
        self.window.installEventFilter(self.log)
        self.addCleanup(self.window.deleteLater)
        self.addCleanup(lambda: QSettings(ORG, SCOPE).clear())

    @staticmethod
    def _fake_worker(window) -> None:
        window.thread = Mock()
        window.worker = Mock()

    def _settle(self) -> None:
        for _ in range(6):
            self.app.processEvents()

    def _widget_frames(self) -> list[tuple[int, int]]:
        return [(w, h) for mode, w, h in self.log.sizes if mode == WIDGET]

    def _dashboard_frames(self) -> list[tuple[int, int]]:
        return [(w, h) for mode, w, h in self.log.sizes if mode == DASHBOARD]

    def test_collapsing_paints_one_size(self):
        """The reported bug: 300x300 was drawn before the widget's real size."""
        self.window.resize(1120, 820)
        self._settle()
        self.log.sizes.clear()

        self.window.enter_widget_mode()
        self._settle()

        frames = self._widget_frames()
        self.assertEqual(
            len(frames),
            1,
            f"the widget was painted at more than one size: {frames}",
        )
        self.assertNotIn(
            (WIDGET_MAX_EDGE, WIDGET_MAX_EDGE),
            frames,
            "the window was drawn at the widget maximum before its real size",
        )
        self.assertEqual((self.window.width(), self.window.height()), frames[0])

    def test_collapsing_from_a_maximised_window_paints_one_size(self):
        """`show()` used to put a maximised window back at full screen first."""
        self.window.showMaximized()
        self._settle()
        self.assertTrue(self.window.isMaximized())
        self.log.sizes.clear()

        self.window.enter_widget_mode()
        self._settle()

        self.assertEqual(len(self._widget_frames()), 1)
        self.assertFalse(
            self.window.isMaximized(),
            "a widget still flagged maximised never saves its geometry, "
            "because _save_geometry declines to record a maximised window",
        )

    def test_expanding_paints_one_size(self):
        """The mirror: a 300x220 stub was drawn where the widget had been."""
        self.window.enter_widget_mode()
        self._settle()
        self.log.sizes.clear()

        self.window.enter_dashboard_mode()
        self._settle()

        frames = self._dashboard_frames()
        self.assertEqual(
            len(frames),
            1,
            f"the dashboard was painted at more than one size: {frames}",
        )
        self.assertGreaterEqual(frames[0][0], WIDGET_MAX_EDGE)

    def test_the_widget_lands_where_it_is_remembered(self):
        """Single-painting must not cost the saved position."""
        self.window.enter_widget_mode()
        self._settle()
        self.window.setGeometry(410, 320, 260, 200)
        self._settle()

        self.window.enter_dashboard_mode()
        self._settle()
        self.window.enter_widget_mode()
        self._settle()

        rect = self.window.geometry()
        self.assertEqual((rect.x(), rect.y()), (410, 320))
        self.assertEqual((rect.width(), rect.height()), (260, 200))

    def test_the_dashboard_size_survives_a_round_trip(self):
        # Sized against the screen the test is actually running on: the
        # offscreen platform's virtual display is only 800x800, and
        # `_fit_to_screen` would clamp anything larger.
        available = self.app.primaryScreen().availableGeometry()
        self.window.resize(
            min(1000, available.width() - 40), min(700, available.height() - 40)
        )
        self._settle()
        before = (self.window.width(), self.window.height())

        self.window.enter_widget_mode()
        self._settle()
        self.window.enter_dashboard_mode()
        self._settle()

        self.assertEqual((self.window.width(), self.window.height()), before)

    def test_a_widget_is_never_left_flagged_maximised(self):
        self.window.showMaximized()
        self._settle()
        self.window.enter_widget_mode()
        self._settle()
        self.assertFalse(self.window.isMaximized())
        self.assertFalse(self.window.isFullScreen())
        self.assertTrue(
            self.window.windowFlags() & Qt.WindowType.FramelessWindowHint
        )


class FitToScreenTests(ModeSwitchTests):
    """Routine fitting keeps the window on screen. It does not resize it."""

    def test_fitting_does_not_undo_a_drag_in_progress(self):
        """The reported bug: the window grew back while being dragged narrow."""
        self.window.resize(760, 560)
        self._settle()
        self.window.resize(420, 560)   # the user is still dragging inward
        self._settle()

        self.window._fit_to_screen()
        self._settle()

        self.assertEqual(
            self.window.width(),
            420,
            "a routine fit grew the window the user was shrinking",
        )

    def test_fitting_still_pulls_a_window_back_onto_the_screen(self):
        available = self.app.primaryScreen().availableGeometry()
        self.window.setGeometry(
            available.right() - 100, available.top() + 10, 600, 400
        )
        self._settle()
        self.window._fit_to_screen()
        self._settle()
        self.assertLessEqual(
            self.window.geometry().right(),
            available.right() + 1,
            "the window was left hanging off the screen",
        )

    def test_a_display_change_may_still_grow_the_window(self):
        """That is what the comfortable floor is for; it keeps that job."""
        self.window.resize(420, 560)
        self._settle()
        self.window._schedule_rescale()
        self.window._fit_to_screen()
        self._settle()

        available = self.app.primaryScreen().availableGeometry()
        self.assertGreaterEqual(
            self.window.width(), min(DASHBOARD_MIN_W, available.width())
        )

    def test_a_widget_dragged_to_its_minimum_is_left_there(self):
        """The floor here was 180x120, above the 150x96 the user can drag to."""
        self.window.enter_widget_mode()
        self._settle()
        self.window.resize(WIDGET_MIN_W, WIDGET_MIN_H)
        self._settle()

        self.window._fit_to_screen()
        self._settle()

        self.assertEqual(
            (self.window.width(), self.window.height()),
            (WIDGET_MIN_W, WIDGET_MIN_H),
        )


if __name__ == "__main__":
    unittest.main()
