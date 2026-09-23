"""Remembered window positions do not pile up, and nothing else is touched.

Each monitor arrangement gets its own saved rect, keyed by a signature of the
screens - that is what stops a docked layout and a laptop panel overwriting
each other. Nothing ever removed one, so every arrangement the machine had
ever been in kept a subkey.

The tidying is the dangerous half. It deletes from the user's settings, so
these tests are mostly about what it must refuse to delete: a review found
that `geometry/usedAt` sliced down to an empty id and became
`remove("geometry/")`, which QSettings reads as the whole group - every
remembered rect at once.
"""

from __future__ import annotations

import hashlib
import os
import unittest
from unittest.mock import patch

SCOPE = "GeometryRetentionTests"

from PySide6.QtCore import QSettings  # noqa: E402

from ai_usage_monitor.settings import (  # noqa: E402
    LAYOUT_ID,
    ORG,
    RETAINED_LAYOUTS,
    SCOPE_ENV_VAR,
    Settings,
)


def signature(seed: str) -> str:
    """An id shaped exactly like `MainWindow._display_signature` makes."""
    return "d" + hashlib.sha1(seed.encode()).hexdigest()[:10]


class RetentionTests(unittest.TestCase):
    def setUp(self):
        scope = patch.dict(os.environ, {SCOPE_ENV_VAR: SCOPE})
        scope.start()
        self.addCleanup(scope.stop)
        QSettings(ORG, SCOPE).clear()
        self.addCleanup(lambda: QSettings(ORG, SCOPE).clear())
        self.settings = Settings()

    def _layouts(self) -> set[str]:
        found = set()
        for key in QSettings(ORG, SCOPE).allKeys():
            if not key.startswith("geometry/"):
                continue
            rest = key[len("geometry/"):]
            if "/" in rest:
                found.add(rest.split("/", 1)[0])
        return found

    def _save(self, seed: str, name: str = "dashboard") -> None:
        self.settings.save_geometry(
            name, [0, 0, 800, 600], display=signature(seed)
        )

    def test_a_few_layouts_are_all_kept(self):
        for index in range(3):
            self._save(f"desk{index}")
        self.assertEqual(len(self._layouts()), 3)

    def test_exactly_the_limit_is_kept(self):
        for index in range(RETAINED_LAYOUTS):
            self._save(f"desk{index}")
        self.assertEqual(len(self._layouts()), RETAINED_LAYOUTS)

    def test_past_the_limit_the_oldest_are_forgotten(self):
        for index in range(RETAINED_LAYOUTS + 5):
            self._save(f"desk{index}")
        self.assertEqual(len(self._layouts()), RETAINED_LAYOUTS)

    def test_the_layouts_kept_are_the_ones_used_most_recently(self):
        for index in range(RETAINED_LAYOUTS + 2):
            self._save(f"desk{index}")
        kept = self._layouts()
        self.assertNotIn(signature("desk0"), kept, "the oldest layout survived")
        self.assertIn(
            signature(f"desk{RETAINED_LAYOUTS + 1}"), kept, "the newest was dropped"
        )

    def test_using_an_old_layout_again_renews_it(self):
        """Coming back to a desk you have not used in a while keeps it."""
        for index in range(RETAINED_LAYOUTS):
            self._save(f"desk{index}")
        self._save("desk0")               # back at the first desk
        for index in range(RETAINED_LAYOUTS, RETAINED_LAYOUTS + 2):
            self._save(f"desk{index}")
        self.assertIn(signature("desk0"), self._layouts())

    def test_the_unscoped_rect_is_never_removed(self):
        """It is the fallback for an arrangement never seen before."""
        for index in range(RETAINED_LAYOUTS + 4):
            self._save(f"desk{index}")
        self.assertIsNotNone(self.settings.load_geometry("dashboard"))

    def test_a_kept_layout_still_loads_its_own_rect(self):
        desk = signature("desk")
        self.settings.save_geometry("widget", [10, 20, 230, 175], display=desk)
        self.assertEqual(
            [int(v) for v in self.settings.load_geometry("widget", desk)],
            [10, 20, 230, 175],
        )

    def test_both_modes_under_one_layout_count_as_one_layout(self):
        for name in ("dashboard", "widget", "connections"):
            self._save("desk", name)
        self.assertEqual(self._layouts(), {signature("desk")})

    def test_saving_without_a_display_touches_nothing(self):
        self.settings.save_geometry("dashboard", [0, 0, 800, 600])
        self.assertEqual(self._layouts(), set())


class OwnershipTests(RetentionTests):
    """What the tidying must refuse to delete.

    It only owns keys shaped like a display signature. Anything else under
    `geometry/` belongs to somebody else - an older build, a hand edit, a
    feature added later - and is not its to remove.
    """

    def _raw(self) -> QSettings:
        return QSettings(ORG, SCOPE)

    def test_an_unscoped_usedAt_does_not_wipe_every_layout(self):
        """The reported hazard: it sliced to "" and removed the whole group."""
        raw = self._raw()
        raw.setValue("geometry/usedAt", "2020-01-01T00:00:00.000000")
        raw.sync()
        for index in range(RETAINED_LAYOUTS + 3):
            self._save(f"desk{index}")

        surviving = self._layouts()
        self.assertEqual(len(surviving), RETAINED_LAYOUTS)
        self.assertIsNotNone(
            self.settings.load_geometry("dashboard"),
            "the unscoped fallback rect was deleted",
        )
        self.assertIn(
            "geometry/usedAt",
            self._raw().allKeys(),
            "a key this function does not own was deleted",
        )

    def test_a_foreign_group_under_geometry_is_left_alone(self):
        raw = self._raw()
        raw.setValue("geometry/somethingElse/usedAt", "1999-01-01T00:00:00.000000")
        raw.setValue("geometry/somethingElse/payload", "keep me")
        raw.sync()
        for index in range(RETAINED_LAYOUTS + 3):
            self._save(f"desk{index}")
        self.assertEqual(
            self._raw().value("geometry/somethingElse/payload"), "keep me"
        )

    def test_a_foreign_group_does_not_consume_the_budget(self):
        """It is not a layout, so it must not push a real one out either."""
        raw = self._raw()
        raw.setValue("geometry/notALayout/usedAt", "1999-01-01T00:00:00.000000")
        raw.sync()
        for index in range(RETAINED_LAYOUTS):
            self._save(f"desk{index}")
        self.assertEqual(len(self._layouts() - {"notALayout"}), RETAINED_LAYOUTS)


class LayoutIdTests(unittest.TestCase):
    def test_it_matches_what_the_window_actually_generates(self):
        """The guard and the generator have to agree or nothing is tidied."""
        import ai_usage_monitor.main_window as main_window
        import inspect

        source = inspect.getsource(main_window.MainWindow._display_signature)
        self.assertIn('"d" + hashlib.sha1', source)
        self.assertIn("[:10]", source)
        self.assertTrue(LAYOUT_ID.fullmatch(signature("anything")))

    def test_it_rejects_everything_that_is_not_one(self):
        # "d" * 11 is NOT in this list: that is a "d" followed by ten more
        # "d"s, and "d" is a hex digit, so it is a perfectly valid id.
        for bad in (
            "", "usedAt", "layout00", "d123", "dZZZZZZZZZZ", "..",
            "d" + "0" * 11,        # one digit too many
            "d" + "0" * 9,         # one too few
            "e" + "0" * 10,        # wrong prefix
            "d0123456789/x",       # a path, not an id
        ):
            with self.subTest(bad=bad):
                self.assertFalse(LAYOUT_ID.fullmatch(bad))


class StampTests(unittest.TestCase):
    def test_the_stamp_is_utc_so_it_only_moves_forwards(self):
        """Local time steps back an hour at a DST fall-back; UTC does not."""
        import datetime as dt

        from ai_usage_monitor.settings import _now_stamp

        before = dt.datetime.now(dt.timezone.utc)
        stamp = dt.datetime.strptime(_now_stamp(), "%Y-%m-%dT%H:%M:%S.%fZ")
        after = dt.datetime.now(dt.timezone.utc)
        self.assertLessEqual(before.replace(tzinfo=None), stamp)
        self.assertLessEqual(stamp, after.replace(tzinfo=None))

    def test_two_stamps_in_quick_succession_still_order(self):
        from ai_usage_monitor.settings import _now_stamp

        self.assertLess(_now_stamp(), _now_stamp())

    def test_the_stamp_says_which_zone_it_is_in(self):
        """A bare timestamp cannot be told apart from a local-time one.

        Review point: switching to UTC fixed the values written from here on,
        but the stored text was byte-for-byte the shape the local-time version
        wrote. Anything already in the registry - or written by an older build
        someone rolled back to - sorts against the new values as though the
        offset were age.
        """
        from ai_usage_monitor.settings import _now_stamp, _stamp_is_utc

        self.assertTrue(_now_stamp().endswith("Z"))
        self.assertTrue(_stamp_is_utc(_now_stamp()))
        self.assertFalse(_stamp_is_utc("2026-09-18T10:00:00.000000"))


if __name__ == "__main__":
    unittest.main()


class ConcurrentUseTests(RetentionTests):
    """A layout someone is using right now must not be deleted underneath them.

    The scan reads every timestamp once, sorts, and deletes from that list.
    Between the scan and the delete another copy of the app can touch one of
    the candidates - a second signed-in user, or one that started while the
    single-instance guard was briefly down. Deleting a layout that has just
    been used is the one thing least-recently-used must not do.
    """

    def test_a_layout_touched_between_the_scan_and_the_delete_survives(self):
        """The window really is between the two reads, not before both.

        A first attempt at this wrote the new timestamp before calling the
        function, so the scan itself saw the fresh value and the layout was
        never a candidate - it passed with the re-check removed, proving
        nothing. Here the stored value changes *after* the scan has read it:
        the first read of the victim's stamp returns the old one, any later
        read returns a new one, which is exactly the interleaving a second
        process produces.
        """
        # Exactly at the limit, so nothing has been trimmed yet. Saving one
        # more is what triggers the deletion, and it happens under the patch.
        # Filling past the limit first would have removed the victim before
        # the patch was even installed.
        for index in range(RETAINED_LAYOUTS):
            self._save(f"desk{index}")

        victim = signature("desk0")       # the oldest, next to be dropped
        victim_key = f"geometry/{victim}/usedAt"
        reads = {"count": 0}
        real_value = self.settings._q.value

        def value(key, *args, **kwargs):
            if key == victim_key:
                reads["count"] += 1
                if reads["count"] > 1:
                    # Somebody else has just sat down at this desk.
                    return "2999-01-01T00:00:00.000000"
            return real_value(key, *args, **kwargs)

        with patch.object(self.settings._q, "value", side_effect=value):
            self._save("desk99")

        self.assertGreater(
            reads["count"], 1, "the stamp was never re-read before deleting"
        )
        self.assertIn(
            victim,
            self._layouts(),
            "a layout used between the scan and the delete was removed anyway",
        )

    def test_an_untouched_layout_is_still_removed(self):
        """The guard must not turn the tidying off altogether."""
        for index in range(RETAINED_LAYOUTS + 2):
            self._save(f"desk{index}")
        self.assertEqual(len(self._layouts()), RETAINED_LAYOUTS)

    def _raw_settings(self):
        return QSettings(ORG, SCOPE)


class MixedStampFormatTests(RetentionTests):
    """A registry holding both shapes must not lose the newer layouts.

    On UTC+7 a layout used at 10:00 local, written before the change, is
    stored as "10:00"; one used half an hour later and written after is
    stored as "03:30" UTC. Compared as text the newer one looks seven hours
    older, so it is the one thrown away - the exact inversion the UTC change
    was made to prevent, arriving by a different door.
    """

    def _legacy(self, seed: str, stamp: str) -> None:
        """A layout as the pre-UTC code left it: rect present, stamp bare."""
        raw = QSettings(ORG, SCOPE)
        layout = signature(seed)
        raw.setValue(f"geometry/{layout}/dashboard", [0, 0, 800, 600])
        raw.setValue(f"geometry/{layout}/usedAt", stamp)
        raw.sync()

    def test_a_local_time_stamp_never_outranks_a_utc_one(self):
        # Ahead of any UTC stamp this test could produce, which is what a
        # positive local offset does.
        for index in range(RETAINED_LAYOUTS):
            self._legacy(f"old{index}", "2099-01-01T10:00:00.000000")
        self._save("fresh")

        kept = self._layouts()
        self.assertIn(
            signature("fresh"),
            kept,
            "the layout saved just now was dropped for an unmarked stamp",
        )
        self.assertEqual(len(kept), RETAINED_LAYOUTS)

    def test_unmarked_layouts_are_the_ones_given_up(self):
        for index in range(RETAINED_LAYOUTS):
            self._legacy(f"old{index}", "2099-01-01T10:00:00.000000")
        for index in range(3):
            self._save(f"new{index}")

        kept = self._layouts()
        self.assertEqual(len(kept), RETAINED_LAYOUTS)
        for index in range(3):
            self.assertIn(signature(f"new{index}"), kept)

    def test_they_are_not_deleted_while_there_is_room(self):
        """Only the cap removes anything. Being old-format is not a reason."""
        for index in range(3):
            self._legacy(f"old{index}", "2020-01-01T10:00:00.000000")
        self._save("fresh")
        self.assertEqual(len(self._layouts()), 4)

    def test_the_registry_heals_itself_as_layouts_are_used_again(self):
        """Coming back to an old desk re-stamps it, so it stops being at risk."""
        for index in range(RETAINED_LAYOUTS):
            self._legacy(f"old{index}", "2020-01-01T10:00:00.000000")
        self._save("old0")                       # back at that desk
        for index in range(4):
            self._save(f"new{index}")
        self.assertIn(signature("old0"), self._layouts())
