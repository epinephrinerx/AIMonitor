"""How the widget decides what fits.

The reported symptom was a widget that "after a long time" showed one gauge
and nothing else, and would not come back however it was resized. Three
things combined to produce it, and each has a test here:

* how many arcs fit was decided by width *and* height together, though
  dropping a meter only ever buys width - so a short widget walked its way
  down to a single gauge, and dragging it wider changed nothing;
* the budget reserved 30px for two reset lines that already decline to draw
  when they do not fit, so a 17px status line appearing - which is what a
  long run eventually produces - was enough to tip a widget from three
  gauges to one while the user was not touching it;
* a floor of `max(28.0, arc)` was then applied to a number that could be
  negative, so the one surviving arc was drawn at 28px whatever the height
  said - overriding the layout's own rule that a meter too small to read is
  dropped rather than smeared. Measured afterwards, the captions did still
  land inside the frame; the visible damage was the missing meters, not
  text hanging off the edge.
"""

import os
import unittest

os.environ["QT_QPA_PLATFORM"] = "offscreen"

from PySide6.QtCore import QRectF  # noqa: E402
from PySide6.QtGui import QFontMetrics, QImage  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor.providers import Meter, ProviderSnapshot  # noqa: E402
from ai_usage_monitor.theme import resolve as resolve_theme  # noqa: E402
from ai_usage_monitor.widgets import compact  # noqa: E402

STALE = "Updated 3 hours ago"


def _meters(count: int = 3) -> list[Meter]:
    names = [
        ("5-Hour", "Session"),
        ("Weekly", "All models"),
        ("Weekly", "Fable only"),
        ("Weekly", "Opus only"),
    ]
    return [
        Meter(key=f"k{i}", title=t, subtitle=s, percent=10.0 * (i + 1))
        for i, (t, s) in enumerate(names[:count])
    ]


class FitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.view = compact.CompactView(resolve_theme("dark"))
        self.drawn: list[tuple[str, float]] = []
        self.texts: list[str] = []
        self.written: list[str] = []
        self.rects: list[QRectF] = []

        # Every caption and title in `compact` is elided before it is drawn,
        # and every text is drawn into a QRectF built there. Recording
        # subclasses of both, swapped into that module's namespace, are what
        # let these tests see the words and the geometry. Watching only
        # `_draw_message` - which is what the first version of this did -
        # could not tell a labelled ring from a bare one, so the test named
        # "never unlabelled" was checking nothing of the sort.
        written, rects = self.written, self.rects

        class RecordingMetrics(QFontMetrics):
            def elidedText(self, text, mode, width, flags=0):
                written.append(text)
                return super().elidedText(text, mode, width, flags)

        class RecordingRect(QRectF):
            def __init__(self, *args):
                super().__init__(*args)
                rects.append(QRectF(self))

        self.addCleanup(setattr, compact, "QFontMetrics", compact.QFontMetrics)
        self.addCleanup(setattr, compact, "QRectF", compact.QRectF)
        compact.QFontMetrics = RecordingMetrics
        compact.QRectF = RecordingRect

        real_arc = compact.CompactView._draw_arc
        real_view = self.view

        def spy_arc(view, painter, cx, y, arc, meter, font, inline):
            self.drawn.append((meter.subtitle, arc))
            return real_arc(view, painter, cx, y, arc, meter, font, inline)

        self.addCleanup(setattr, compact.CompactView, "_draw_arc", real_arc)
        compact.CompactView._draw_arc = spy_arc

        real_message = compact.CompactView._draw_message

        def spy_message(view, painter, x, y, width, text):
            self.texts.append(text)
            return real_message(view, painter, x, y, width, text)

        self.addCleanup(setattr, compact.CompactView, "_draw_message", real_message)
        compact.CompactView._draw_message = spy_message
        self.real_view = real_view

    def paint(self, width, height, status="", meters=None):
        snapshot = ProviderSnapshot(provider_id="claude", configured=True)
        snapshot.meters = meters if meters is not None else _meters()
        self.drawn.clear()
        self.texts.clear()
        self.written.clear()
        self.rects.clear()
        self.view.set_snapshot("Claude", snapshot, status)
        self.view.resize(width, height)
        image = QImage(width, height, QImage.Format.Format_ARGB32)
        self.view.render(image)
        self.image = image
        return list(self.drawn)

    # -- the reported bug -------------------------------------------------

    def test_a_short_widget_still_shows_every_gauge(self):
        self.assertEqual(len(self.paint(230, 128)), 3)

    def test_dragging_it_wider_brings_gauges_back(self):
        """The complaint was that resizing did nothing at all.

        It could not: the meter count was being decided by a height the loop
        never changed, so every iteration dropped a meter and measured the
        same failure again.
        """
        narrow = len(self.paint(150, 128))
        wide = len(self.paint(300, 128))
        self.assertLess(narrow, wide)
        self.assertEqual(wide, 3)

    def test_a_status_line_appearing_does_not_cost_a_gauge(self):
        """What "after a long time" actually does to the widget.

        Nothing about the window changes; the data goes stale and gains a
        footer line. That must not decide how many meters are shown.
        """
        for height in range(96, 190, 2):
            with self.subTest(height=height):
                self.assertEqual(
                    len(self.paint(230, height)),
                    len(self.paint(230, height, STALE)),
                )

    def test_no_height_makes_gauges_vanish(self):
        """From the enforced floor upwards, all three survive."""
        for height in range(96, 220, 2):
            with self.subTest(height=height):
                self.assertEqual(len(self.paint(230, height, STALE)), 3)

    def test_growing_taller_never_loses_a_gauge(self):
        counts = [len(self.paint(230, h, STALE)) for h in range(96, 220, 2)]
        for shorter, taller in zip(counts, counts[1:]):
            self.assertGreaterEqual(taller, shorter)

    # -- what may be given up, and in what order --------------------------

    def test_width_is_what_drops_a_meter(self):
        """The count is the most that still leaves each arc a legible width.

        The minimum is lifted because `resize` is clamped by it: asking for
        120 and measuring as though it were 120, while Qt quietly drew 150,
        made this subtest a fiction that happened to agree.
        """
        self.view.setMinimumSize(0, 0)
        for width in (120, 160, 230, 300, 400):
            with self.subTest(width=width):
                shown = len(self.paint(width, 175, meters=_meters(4)))
                usable = width - 22
                per_arc = (usable - compact.GAP * (shown - 1)) / shown
                self.assertTrue(
                    shown == 1 or per_arc >= compact.ARC_MIN,
                    f"{shown} arcs at {per_arc:.0f}px is below the minimum",
                )
                if shown < 4:
                    one_more = shown + 1
                    would_be = (usable - compact.GAP * (one_more - 1)) / one_more
                    self.assertLess(
                        would_be,
                        compact.ARC_MIN,
                        f"{one_more} would have fitted at {would_be:.0f}px",
                    )

    def test_the_arc_never_goes_below_the_floor(self):
        for height in range(96, 220, 2):
            with self.subTest(height=height):
                for _, arc in self.paint(230, height, STALE):
                    self.assertGreaterEqual(arc, compact.ARC_FLOOR)

    def test_a_ring_is_never_left_unlabelled(self):
        """Colour must not carry the meaning alone - the rule this project
        holds everywhere else. The subtitle may go; the title may not, and
        below `INLINE_VALUE_MIN` the caption is where the percentage lives."""
        for height in (96, 110, 128, 175):
            with self.subTest(height=height):
                drawn = self.paint(230, height, STALE)
                self.assertEqual(self.texts, [], "it gave up and showed a message")
                for subtitle, arc in drawn:
                    meter = next(m for m in _meters() if m.subtitle == subtitle)
                    captions = [w for w in self.written if w.startswith(meter.title)]
                    self.assertTrue(
                        captions,
                        f"the {subtitle} ring was drawn with no caption: "
                        f"{self.written}",
                    )
                    if arc < compact.INLINE_VALUE_MIN:
                        self.assertTrue(
                            any("%" in c for c in captions),
                            "below the inline size the percentage has to be "
                            f"in the caption: {captions}",
                        )

    def test_too_small_says_so_instead_of_drawing_a_smear(self):
        """Unreachable by dragging, reachable by a larger system font.

        The enforced 96px floor is lifted here because that floor is exactly
        what stops a user from getting here; a 150% text scale does not have
        to ask permission.
        """
        self.view.setMinimumSize(0, 0)
        self.assertEqual(self.paint(230, 40), [])
        self.assertEqual(self.texts, ["Too small to show the meters."])

    # -- the floor the user can drag to -----------------------------------

    def test_the_floor_is_always_a_height_that_draws(self):
        """The enforced minimum must never sit inside the "too small" branch.

        A fixed 96 was right for the test platform's font and low for a real
        desktop one, so on a normal machine the widget could be dragged to a
        size at which it would only say it was too small. The floor is worked
        out from the fonts now, and this holds it there as they grow.

        The scale is applied by patching `_fonts`, because that is the single
        place both the floor and the painting get their metrics from. Setting
        a larger font on the widget does nothing: `_fonts` pins the point
        sizes, so a test that scaled the widget font would be measuring
        nothing and would pass with the floor nailed shut.
        """
        real_fonts = compact.CompactView._fonts
        self.addCleanup(setattr, compact.CompactView, "_fonts", real_fonts)

        for factor in (1.0, 1.25, 1.5, 2.0, 2.5):
            with self.subTest(text_scale=factor):

                def scaled(view, _f=factor, _real=real_fonts):
                    header, small = _real(view)
                    header.setPointSizeF(header.pointSizeF() * _f)
                    small.setPointSizeF(small.pointSizeF() * _f)
                    return header, small

                compact.CompactView._fonts = scaled
                floor = self.view.minimum_useful_height()
                self.view.setMinimumSize(0, 0)
                drawn = self.paint(230, floor, STALE)
                self.assertEqual(
                    self.texts, [], f"{floor}px is the floor and it will not draw"
                )
                self.assertTrue(drawn)
                for _, arc in drawn:
                    self.assertGreaterEqual(arc, compact.ARC_FLOOR)

    # -- nothing outside the window ---------------------------------------

    def test_nothing_is_drawn_outside_the_widget(self):
        """Checked against the rectangles asked for, not the pixels kept.

        A QImage cannot answer this: Qt clips to the widget, so anything
        overflowing is simply gone by the time the pixels exist, and the
        bottom row is painted by the widget's own background either way. The
        first version of this test compared that row against a fill colour
        and would have passed with every caption hanging off the bottom.

        This is a guard, not a reproduction: the old layout kept its captions
        inside the frame after all. It is here because the arithmetic that
        places them is the arithmetic this change rewrote.
        """
        for width, height in ((150, 96), (230, 110), (230, 128), (230, 175)):
            with self.subTest(size=(width, height)):
                self.paint(width, height, STALE)
                bounds = QRectF(0.0, 0.0, float(width), float(height))
                for rect in self.rects:
                    if rect.isEmpty():
                        continue
                    self.assertLessEqual(
                        rect.bottom(),
                        bounds.bottom() + 0.5,
                        f"{rect} runs past the bottom of a {width}x{height} widget",
                    )
                    self.assertLessEqual(rect.right(), bounds.right() + 0.5, str(rect))
                    self.assertGreaterEqual(rect.top(), -0.5, str(rect))

if __name__ == "__main__":
    unittest.main()
