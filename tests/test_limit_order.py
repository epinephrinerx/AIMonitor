"""The gauges stay where they are put.

Reported from use: Claude's meters were no longer in the order they had been -
5-hour, weekly, per-model. They were sorted by descending usage after the
session window, so "Weekly · All models" and a per-model window traded places
the moment one overtook the other. Two screenshots of the same three gauges,
hours apart:

    24% all models, 5% Fable   ->  Session, All models, Fable only
    7%  all models, 10% Fable  ->  Session, Fable only, All models

A dashboard is read by position. A gauge that moves when its number moves is
the one thing a gauge must not do.
"""

from __future__ import annotations

import unittest

from ai_usage_monitor.api import _limits_from_payload


def entry(kind: str, percent: float, **extra) -> dict:
    payload = {"kind": kind, "percent": percent, "resets_at": None}
    payload.update(extra)
    return payload


def scoped(name: str, percent: float) -> dict:
    return entry(
        "weekly_scoped",
        percent,
        scope={"model": {"display_name": name}},
    )


def order(*entries: dict) -> list[tuple[str, str]]:
    limits = _limits_from_payload({"limits": list(entries)})
    return [(limit.title, limit.subtitle) for limit in limits]


class ReadingOrderTests(unittest.TestCase):
    def test_the_reported_case(self):
        """Fable ahead of all-models on usage must not move it up the row."""
        self.assertEqual(
            order(scoped("Fable", 10.0), entry("weekly_all", 7.0), entry("session", 56.0)),
            [
                ("Session", "5-hour window"),
                ("Weekly", "All models"),
                ("Weekly", "Fable only"),
            ],
        )

    def test_the_order_does_not_depend_on_the_numbers(self):
        """The same three windows, every possible way round, read the same."""
        expected = [
            ("Session", "5-hour window"),
            ("Weekly", "All models"),
            ("Weekly", "Fable only"),
        ]
        for session, weekly, fable in (
            (56.0, 24.0, 5.0),
            (56.0, 7.0, 10.0),
            (0.0, 0.0, 0.0),
            (1.0, 99.0, 50.0),
            (99.0, 1.0, 100.0),
        ):
            with self.subTest(session=session, weekly=weekly, fable=fable):
                self.assertEqual(
                    order(
                        scoped("Fable", fable),
                        entry("weekly_all", weekly),
                        entry("session", session),
                    ),
                    expected,
                )

    def test_the_order_does_not_depend_on_what_the_server_sent_first(self):
        expected = [
            ("Session", "5-hour window"),
            ("Weekly", "All models"),
            ("Weekly", "Opus only"),
        ]
        first = order(entry("session", 5.0), entry("weekly_all", 5.0), entry("weekly_opus", 5.0))
        second = order(entry("weekly_opus", 5.0), entry("session", 5.0), entry("weekly_all", 5.0))
        self.assertEqual(first, expected)
        self.assertEqual(second, expected)

    def test_the_session_window_is_always_first(self):
        """The tab label and the widget's lead meter both read position zero."""
        limits = _limits_from_payload(
            {"limits": [entry("weekly_all", 90.0), entry("session", 1.0)]}
        )
        self.assertEqual(limits[0].kind, "session")

    def test_per_model_windows_keep_a_fixed_order_among_themselves(self):
        self.assertEqual(
            order(
                scoped("Sonnet", 30.0),
                scoped("Fable", 20.0),
                scoped("Opus", 10.0),
                entry("weekly_all", 40.0),
            ),
            [
                ("Weekly", "All models"),
                ("Weekly", "Fable only"),
                ("Weekly", "Opus only"),
                ("Weekly", "Sonnet only"),
            ],
        )

    def test_an_unknown_kind_sorts_last_rather_than_displacing_a_known_one(self):
        """A newer server must not push the five-hour window down the row."""
        result = order(
            entry("some_new_window", 99.0),
            entry("weekly_all", 2.0),
            entry("session", 1.0),
        )
        self.assertEqual(result[0], ("Session", "5-hour window"))
        self.assertEqual(result[1], ("Weekly", "All models"))
        self.assertEqual(result[2][1], "Some new window")

    def test_the_legacy_payload_shape_is_ordered_the_same_way(self):
        """Older accounts get the named blocks instead of a `limits` list."""
        limits = _limits_from_payload({
            "seven_day": {"utilization": 80.0},
            "five_hour": {"utilization": 3.0},
            "seven_day_opus": {"utilization": 90.0},
        })
        self.assertEqual(
            [(limit.title, limit.subtitle) for limit in limits],
            [
                ("Session", "5-hour window"),
                ("Weekly", "All models"),
                ("Weekly", "Opus only"),
            ],
        )

    def test_nothing_is_dropped(self):
        result = order(
            entry("session", 1.0),
            entry("weekly_all", 2.0),
            scoped("Fable", 3.0),
            entry("weekly_oauth_apps", 4.0),
        )
        self.assertEqual(len(result), 4)


if __name__ == "__main__":
    unittest.main()
