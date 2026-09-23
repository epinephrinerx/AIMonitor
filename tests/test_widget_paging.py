"""Stepping between services by hand, and the resize that no longer switches modes.

Two changes the user asked for after living with the widget:

* the chevrons - the rotation is a four-second wait and the context menu is
  three clicks deep, neither of which is what you want when the widget is
  showing the service you are not asking about;
* collapsing on resize is gone. It put a mode change on the same gesture as
  an ordinary resize, so a window dragged a bit smaller turned into something
  else entirely.
"""

import inspect
import os
import unittest
from types import SimpleNamespace
from unittest.mock import Mock

os.environ["QT_QPA_PLATFORM"] = "offscreen"

from PySide6.QtCore import QEvent, QPointF, QRect, QSize, Qt  # noqa: E402
from PySide6.QtGui import QImage, QMouseEvent  # noqa: E402
from PySide6.QtWidgets import QApplication, QMainWindow  # noqa: E402

from ai_usage_monitor import main_window as mw  # noqa: E402
from ai_usage_monitor import settings as settings_module  # noqa: E402
from ai_usage_monitor.providers import Meter, ProviderSnapshot  # noqa: E402
from ai_usage_monitor.theme import resolve as resolve_theme  # noqa: E402
from ai_usage_monitor.widgets import compact  # noqa: E402


def _click(view, point, kind=QEvent.Type.MouseButtonPress):
    event = QMouseEvent(
        kind,
        point,
        view.mapToGlobal(point),
        Qt.MouseButton.LeftButton,
        Qt.MouseButton.LeftButton,
        Qt.KeyboardModifier.NoModifier,
    )
    if kind == QEvent.Type.MouseButtonDblClick:
        view.mouseDoubleClickEvent(event)
    else:
        view.mousePressEvent(event)
    return event


class ChevronTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.view = compact.CompactView(resolve_theme("dark"))
        self.view.set_pageable(True)
        snapshot = ProviderSnapshot(provider_id="claude", configured=True)
        snapshot.meters = [
            Meter(key="a", title="5-Hour", subtitle="Session", percent=20.0),
            Meter(key="b", title="Weekly", subtitle="All models", percent=40.0),
        ]
        self.view.set_snapshot("Claude", snapshot, "updated just now")
        self.view.resize(230, 175)
        self.steps: list[int] = []
        self.view.page_requested.connect(self.steps.append)
        self.expanded: list[bool] = []
        self.view.expand_requested.connect(lambda: self.expanded.append(True))
        self.paint()

    def paint(self):
        image = QImage(
            self.view.width(), self.view.height(), QImage.Format.Format_ARGB32
        )
        self.view.render(image)

    def centre(self, which):
        rects = self.view._pager
        self.assertIsNotNone(rects, "the chevrons were never laid out")
        return rects[0 if which < 0 else 1].center()

    def test_each_chevron_steps_its_own_way(self):
        _click(self.view, self.centre(-1))
        _click(self.view, self.centre(1))
        self.assertEqual(self.steps, [-1, 1])

    def test_a_chevron_click_does_not_start_dragging_the_window(self):
        """The whole face of the widget moves the window, so a chevron has to
        claim the press outright or the click reads as a one-pixel drag."""
        _click(self.view, self.centre(1))
        self.assertIsNone(self.view._drag_origin)

    def test_pressing_anywhere_else_still_drags(self):
        _click(self.view, QPointF(self.view.width() / 2, self.view.height() / 2))
        self.assertIsNotNone(self.view._drag_origin)
        self.assertEqual(self.steps, [])

    def test_double_clicking_a_chevron_does_not_expand(self):
        point = self.centre(1)
        _click(self.view, point)
        _click(self.view, point, QEvent.Type.MouseButtonDblClick)
        self.assertEqual(self.expanded, [])

    def test_clicking_a_chevron_twice_quickly_steps_twice(self):
        """Qt sends `MouseButtonDblClick` *instead of* the second press, so
        the second click is only a step if this handler takes it. These are
        buttons: pressing one twice means doing it twice."""
        point = self.centre(1)
        _click(self.view, point)
        _click(self.view, point, QEvent.Type.MouseButtonDblClick)
        self.assertEqual(self.steps, [1, 1])

    def test_double_clicking_the_face_still_expands(self):
        _click(
            self.view,
            QPointF(self.view.width() / 2, self.view.height() / 2),
            QEvent.Type.MouseButtonDblClick,
        )
        self.assertEqual(self.expanded, [True])

    def test_one_service_means_no_chevrons(self):
        self.view.set_pageable(False)
        self.paint()
        self.assertIsNone(self.view._pager)
        _click(self.view, QPointF(self.view.width() - 20, 20))
        self.assertEqual(self.steps, [])

    def test_the_chevrons_keep_clear_of_the_resize_band(self):
        """Both live in the header, inset by the content margin. Overlapping
        the band would make one of the two gestures unreachable."""
        for width, height in ((150, 96), (230, 175), (300, 300)):
            with self.subTest(size=(width, height)):
                self.view.resize(width, height)
                self.paint()
                for rect in self.view._pager:
                    self.assertGreater(rect.top(), compact.RESIZE_MARGIN)
                    self.assertLess(rect.right(), width - compact.RESIZE_MARGIN)

    def test_hovering_a_chevron_says_it_is_clickable(self):
        point = self.centre(1)
        event = QMouseEvent(
            QEvent.Type.MouseMove,
            point,
            self.view.mapToGlobal(point),
            Qt.MouseButton.NoButton,
            Qt.MouseButton.NoButton,
            Qt.KeyboardModifier.NoModifier,
        )
        self.view.mouseMoveEvent(event)
        self.assertEqual(self.view.cursor().shape(), Qt.CursorShape.PointingHandCursor)
        self.assertEqual(self.view._hover_page, 1)


class _Window:
    """Enough of MainWindow to run the paging method against.

    It borrows the real `_widget_provider_id`, because *which service ends up
    on screen* is the whole question. An earlier version of this fake only
    counted renders and watched `_rotate_index`, and passed happily while the
    production path was showing the same service every time - the index it
    was checking was not the one the renderer read.
    """

    _page_widget = mw.MainWindow._page_widget
    _widget_provider_id = mw.MainWindow._widget_provider_id

    def __init__(self, ids, pinned=None, rotate=True, active=0, ticking=False):
        self._ids = ids
        self._widget_pinned = pinned
        self._rotate_index = 0
        self._active = active
        self.settings = SimpleNamespace(widget_rotate=rotate)
        self.shown: list[str] = []
        self._rotate_widget = Mock()
        self._rotate_widget.isActive.return_value = ticking

    def _rotatable_ids(self):
        return self._ids

    def _active_provider_id(self):
        return self._ids[self._active]

    def _render_compact(self):
        self.shown.append(self._widget_provider_id())


THREE = ["claude", "openai", "gemini"]


class PagingTests(unittest.TestCase):
    def test_it_steps_both_ways_and_wraps(self):
        window = _Window(THREE)
        window._page_widget(1)
        window._page_widget(1)
        window._page_widget(1)
        window._page_widget(-1)
        self.assertEqual(window.shown, ["openai", "gemini", "claude", "gemini"])

    def test_it_shows_the_new_service_at_once(self):
        """The whole point: no fetch, no waiting for the timer."""
        window = _Window(THREE)
        window._page_widget(1)
        self.assertEqual(window.shown, ["openai"])

    def test_it_works_with_the_rotation_switched_off(self):
        """The state a restart leaves behind.

        Picking a service from the menu saves `widget_rotate = False` but the
        pin itself lives only in memory, so the next launch has rotation off
        and nothing pinned. The widget then follows the dashboard tab, and
        stepping the rotation index moved a number nobody was reading: the
        chevrons were drawn, took the click, and changed nothing.
        """
        window = _Window(THREE, rotate=False)
        window._page_widget(1)
        self.assertEqual(window.shown, ["openai"])
        window._page_widget(1)
        self.assertEqual(window.shown[-1], "gemini")

    def test_paging_a_pinned_widget_moves_the_pin(self):
        window = _Window(THREE, pinned="openai", rotate=False)
        window._page_widget(1)
        self.assertEqual(window._widget_pinned, "gemini")
        self.assertEqual(window.shown, ["gemini"])

    def test_paging_while_rotating_keeps_rotating(self):
        """A step is not a request to stop - it is a request to get there now."""
        window = _Window(THREE)
        window._page_widget(1)
        self.assertIsNone(window._widget_pinned)

    def test_a_click_buys_a_whole_interval(self):
        window = _Window(THREE, ticking=True)
        window._page_widget(1)
        window._rotate_widget.start.assert_called_once()

    def test_nothing_happens_with_one_service(self):
        window = _Window(["claude"])
        window._page_widget(1)
        self.assertEqual(window.shown, [])


class PagerVisibilityTests(unittest.TestCase):
    """The chevrons must answer to the same list the handler checks."""

    def test_the_widget_asks_what_it_can_page_to_not_what_is_installed(self):
        source = inspect.getsource(mw.MainWindow._render_compact)
        self.assertIn("set_pageable", source)
        self.assertIn("_rotatable_ids", source)
        self.assertNotIn(
            "len(self.providers)",
            source,
            "installed providers is the wrong question - unconnected ones "
            "cannot be paged to",
        )

    def test_it_is_kept_up_to_date_rather_than_set_once(self):
        """Set at build time it would be stale: which services have data is
        not known until a refresh comes back."""
        build = inspect.getsource(mw.MainWindow._build_ui)
        self.assertNotIn("set_pageable", build)


class ResizeNoLongerSwitchesModesTests(unittest.TestCase):
    def test_the_window_does_not_watch_its_own_size(self):
        """Dragging the dashboard small is a resize and nothing more."""
        self.assertIs(mw.MainWindow.resizeEvent, QMainWindow.resizeEvent)
        self.assertFalse(hasattr(mw.MainWindow, "_collapse_when_drag_ends"))

    def test_the_threshold_is_gone_for_good(self):
        """Left behind, it would read as a rule the code still follows."""
        self.assertFalse(hasattr(settings_module, "WIDGET_TRIGGER_EDGE"))

    def test_a_small_dashboard_rect_is_still_remembered(self):
        """It used to be discarded as the collapse drag. Now it is a size
        someone chose, and only one below the window's own minimum is junk.

        Driven through `_save_geometry` and `_target_geometry` rather than by
        reading the source for a constant's name: the first version of this
        test searched for the string and would have passed with the method
        returning before it ever saved anything.
        """
        saved = {}

        class _Geometry:
            _save_geometry = mw.MainWindow._save_geometry
            _target_geometry = mw.MainWindow._target_geometry

            def __init__(self, rect):
                self._rect = rect
                self.settings = SimpleNamespace(
                    save_geometry=lambda mode, rect, display: saved.update(
                        {mode: rect}
                    ),
                    load_geometry=lambda mode, display: saved.get(mode),
                )

            def isMinimized(self):
                return False

            def isMaximized(self):
                return False

            def isVisible(self):
                return True

            def geometry(self):
                return self._rect

            def _display_signature(self):
                return "test"

            def _on_a_screen(self, rect):
                return True

        # Well under the old 380px threshold, at the window's own minimum.
        small = QRect(40, 40, mw.DASHBOARD_DRAG_MIN_W, mw.DASHBOARD_DRAG_MIN_H)
        window = _Geometry(small)
        window._save_geometry(mw.DASHBOARD)
        self.assertEqual(
            saved.get(mw.DASHBOARD),
            [small.x(), small.y(), small.width(), small.height()],
            "a dashboard the user dragged small was thrown away",
        )
        self.assertEqual(
            window._target_geometry(mw.DASHBOARD, QSize(1120, 820)),
            small,
            "it was saved and then refused on the way back",
        )

    def test_a_rect_below_the_minimum_is_still_refused(self):
        """Below what can be dragged to, it was never dragged there."""
        saved = {}

        class _Geometry:
            _save_geometry = mw.MainWindow._save_geometry

            def __init__(self, rect):
                self._rect = rect
                self.settings = SimpleNamespace(
                    save_geometry=lambda mode, rect, display: saved.update(
                        {mode: rect}
                    )
                )

            def isMinimized(self):
                return False

            def isMaximized(self):
                return False

            def isVisible(self):
                return True

            def geometry(self):
                return self._rect

            def _display_signature(self):
                return "test"

        window = _Geometry(
            QRect(0, 0, mw.DASHBOARD_DRAG_MIN_W - 1, mw.DASHBOARD_DRAG_MIN_H - 1)
        )
        window._save_geometry(mw.DASHBOARD)
        self.assertEqual(saved, {})

if __name__ == "__main__":
    unittest.main()
