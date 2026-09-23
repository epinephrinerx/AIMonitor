"""Dashboard, widget and tray answer "which window matters most" the same way.

The widget's reset line used to belong to whichever window was fullest, so the
caption changed which window it described as the numbers moved - the same
complaint that had the gauges themselves reordering, moved one line down.

It could also disagree with the arc directly above it. At the smallest size
only the first arc survives, and after `api._reading_order` that is the
five-hour window, while the caption might still have been describing the
weekly one that was no longer being drawn. The tray never had this problem:
it asks `is_five_hour`, which reads what the service reported.
"""

from __future__ import annotations

import ast
import pathlib
import unittest

from ai_usage_monitor.providers.base import Meter
from ai_usage_monitor.tray import is_five_hour
from ai_usage_monitor.widgets.compact import lead_meter

ROOT = pathlib.Path(__file__).resolve().parent.parent


def meter(key: str, title: str, subtitle: str, percent: float | None) -> Meter:
    return Meter(key=key, title=title, subtitle=subtitle, percent=percent)


SESSION = meter("session", "Session", "5-hour window", 12.0)
WEEKLY = meter("weekly_all", "Weekly", "All models", 88.0)
SCOPED = meter("weekly_scoped", "Weekly", "Fable only", 95.0)


class LeadMeterTests(unittest.TestCase):
    def test_the_five_hour_window_leads_however_full_the_others_are(self):
        """The reported shape: a fuller weekly window used to take the line."""
        self.assertIs(lead_meter([SESSION, WEEKLY, SCOPED]), SESSION)

    def test_it_does_not_depend_on_the_numbers(self):
        for session, weekly, scoped in (
            (99.0, 1.0, 1.0),
            (1.0, 99.0, 50.0),
            (0.0, 0.0, 0.0),
            (50.0, 50.0, 50.0),
        ):
            with self.subTest(session=session):
                row = [
                    meter("session", "Session", "5-hour window", session),
                    meter("weekly_all", "Weekly", "All models", weekly),
                    meter("weekly_scoped", "Weekly", "Fable only", scoped),
                ]
                self.assertEqual(lead_meter(row).subtitle, "5-hour window")

    def test_it_does_not_depend_on_position(self):
        self.assertIs(lead_meter([WEEKLY, SCOPED, SESSION]), SESSION)

    def test_the_caption_matches_the_only_arc_left_at_the_smallest_size(self):
        """`shown = gauges[:count]` keeps the first; the caption must agree."""
        row = [SESSION, WEEKLY, SCOPED]
        shown = row[:1]
        self.assertIs(lead_meter(shown), shown[0])

    def test_a_service_with_no_five_hour_window_falls_back_to_the_first(self):
        """Gemini reports a daily count and nothing shorter."""
        today = meter("requests_today", "Today", "API requests", None)
        row = [today, WEEKLY]
        self.assertIs(lead_meter(row), today)

    def test_it_agrees_with_the_tray(self):
        row = [SESSION, WEEKLY, SCOPED]
        from_tray = next(m for m in row if is_five_hour(m))
        self.assertIs(lead_meter(row), from_tray)

    def test_a_codex_window_named_by_its_duration_also_leads(self):
        """Codex builds the subtitle from the window's own length in minutes."""
        codex_session = meter("primary", "Session", "5-hour window", 3.0)
        codex_weekly = meter("secondary", "Weekly", "7-day window", 80.0)
        self.assertIs(lead_meter([codex_weekly, codex_session]), codex_session)


class OneRuleEverywhereTests(unittest.TestCase):
    """Checked in the source, so a fourth surface cannot invent its own rule."""

    def test_the_widget_does_not_pick_its_lead_by_percentage(self):
        source = (
            ROOT / "ai_usage_monitor" / "widgets" / "compact.py"
        ).read_text(encoding="utf-8")
        for node in ast.walk(ast.parse(source)):
            if not isinstance(node, ast.Call):
                continue
            if getattr(node.func, "id", "") != "max":
                continue
            # `max(a, b)` clamps a number; `max(iterable, key=...)` picks one
            # thing out of many. Only the second can choose a lead meter, and
            # flagging the first made this test fail on the drawing code.
            if not any(kw.arg == "key" for kw in node.keywords):
                continue
            rendered = ast.unparse(node)
            self.assertNotIn(
                "percent",
                rendered,
                f"line {node.lineno}: {rendered} - the lead meter is chosen "
                "by what the window is, not by how full it is",
            )

    def test_the_widget_actually_calls_the_helper_when_it_paints(self):
        """A helper nothing calls is not a shared rule, it is dead code.

        The first version of this test looked for the words `is_five_hour` in
        the file, which a comment would have satisfied. This paints a real
        widget and watches whether the reset line goes through `lead_meter`.
        """
        import os
        from unittest.mock import patch

        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
        from PySide6.QtGui import QPixmap, QPainter  # noqa: E402
        from PySide6.QtWidgets import QApplication  # noqa: E402

        from ai_usage_monitor import theme as theming
        from ai_usage_monitor.providers.base import ProviderSnapshot
        from ai_usage_monitor.widgets import compact

        app = QApplication.instance() or QApplication([])
        view = compact.CompactView(theming.resolve("light"))
        view.resize(300, 300)
        view.set_snapshot(
            "Claude",
            ProviderSnapshot(
                provider_id="claude",
                configured=True,
                meters=[SESSION, WEEKLY, SCOPED],
            ),
            "updated just now",
        )

        with patch.object(
            compact, "lead_meter", wraps=compact.lead_meter
        ) as spy:
            pixmap = QPixmap(view.size())
            painter = QPainter(pixmap)
            painter.end()
            view.render(pixmap)

        view.deleteLater()
        app.processEvents()
        spy.assert_called()
        (painted_row,) = spy.call_args.args
        self.assertIs(
            compact.lead_meter(painted_row),
            SESSION,
            "the reset line was drawn for a window other than the five-hour one",
        )


if __name__ == "__main__":
    unittest.main()
