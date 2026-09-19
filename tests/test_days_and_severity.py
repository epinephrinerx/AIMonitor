"""Days are the user's days, and colour agrees with the word beside it.

Two findings from the 2026-09-18 review.

* Gemini took its day boundary from UTC midnight while the chart labelled and
  bucketed in local time. In Bangkok that is 07:00, so "Today" silently
  dropped the first seven hours of every day and the daily bars carried most
  of one day's requests under the next day's label.
* `QuotaGauge` computed the effective severity in `set_meter` for the arc and
  the word, but `_restyle_severity` read `meter.severity` raw for the colour.
  A service reporting "normal" at 95% therefore drew a red arc labelled
  Critical in the ordinary text colour.
"""

from __future__ import annotations

import datetime as dt
import os
import unittest
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor.providers.base import Meter  # noqa: E402
from ai_usage_monitor.providers.gemini_provider import (  # noqa: E402
    GeminiProvider,
    bucket_day,
    local_day_start,
)
from ai_usage_monitor.theme import (  # noqa: E402
    CRITICAL_PERCENT,
    WARNING_PERCENT,
    resolve,
    severity_color,
    severity_for,
)
from ai_usage_monitor.widgets.gauge import QuotaGauge  # noqa: E402

BANGKOK = dt.timezone(dt.timedelta(hours=7), "ICT")
PACIFIC = dt.timezone(dt.timedelta(hours=-7), "PDT")
UTC = dt.timezone.utc


class _FixedLocal:
    """Pretend the machine is in one zone, for `astimezone()` with no argument.

    `datetime.astimezone()` with no argument reads the platform's zone, which
    a test cannot set portably; patching the class is the honest way to ask
    "what would this code do in Bangkok".
    """

    def __init__(self, zone: dt.tzinfo) -> None:
        self.zone = zone

    def __enter__(self):
        real = dt.datetime

        class _Local(real):
            @classmethod
            def now(cls, tz=None):
                # A fixed instant, so the tests do not drift with the clock.
                # Rebuilt as this class, not the real one: the caller usually
                # chains `.astimezone()` straight onto the result, and a plain
                # datetime there would fall back to the machine's own zone.
                moment = real(2026, 9, 18, 3, 30, tzinfo=UTC).astimezone(tz or zone)
                return cls(
                    moment.year, moment.month, moment.day,
                    moment.hour, moment.minute, moment.second,
                    moment.microsecond, tzinfo=moment.tzinfo,
                )

            def astimezone(self, tz=None):
                # A naive value means "local time", and local here is the
                # zone being pretended. Handing a naive value straight to
                # the real astimezone would have the platform read it as
                # the machine's own zone instead, which made every case
                # come out as whatever this machine happens to be.
                aware = self if self.tzinfo else self.replace(tzinfo=zone)
                return real.astimezone(aware, tz or zone)

        zone = self.zone
        self.patch = patch(
            "ai_usage_monitor.providers.gemini_provider.dt.datetime", _Local
        )
        self.patch.start()
        return self

    def __exit__(self, *exc):
        self.patch.stop()
        return False


class LocalDayStartTests(unittest.TestCase):
    def test_bangkok_midnight_is_the_previous_utc_evening(self):
        with _FixedLocal(BANGKOK):
            start = local_day_start(dt.date(2026, 9, 18))
        self.assertEqual(start, dt.datetime(2026, 9, 17, 17, 0, tzinfo=UTC))

    def test_pacific_midnight_is_the_same_utc_morning(self):
        with _FixedLocal(PACIFIC):
            start = local_day_start(dt.date(2026, 9, 18))
        self.assertEqual(start, dt.datetime(2026, 9, 18, 7, 0, tzinfo=UTC))

    def test_utc_midnight_is_itself(self):
        with _FixedLocal(UTC):
            start = local_day_start(dt.date(2026, 9, 18))
        self.assertEqual(start, dt.datetime(2026, 9, 18, 0, 0, tzinfo=UTC))

    def test_a_day_is_always_a_whole_day_later(self):
        with _FixedLocal(BANGKOK):
            first = local_day_start(dt.date(2026, 9, 18))
            second = local_day_start(dt.date(2026, 9, 19))
        self.assertEqual(second - first, dt.timedelta(days=1))


class BucketDayTests(unittest.TestCase):
    """A point is about the interval it covers, not the instant it ends."""

    def _point(self, start: str | None = None, end: str | None = None) -> dict:
        interval = {}
        if start:
            interval["startTime"] = start
        if end:
            interval["endTime"] = end
        return {"interval": interval}

    def test_the_start_of_the_interval_names_the_day(self):
        with _FixedLocal(BANGKOK):
            # Local 2026-09-18 00:00 in Bangkok is 2026-09-17T17:00Z.
            day = bucket_day(self._point(start="2026-09-17T17:00:00Z"), 86_400)
        self.assertEqual(day, dt.date(2026, 9, 18))

    def test_end_time_alone_is_walked_back_by_one_period(self):
        """Reading endTime directly put every bucket on the following day."""
        with _FixedLocal(BANGKOK):
            day = bucket_day(self._point(end="2026-09-18T17:00:00Z"), 86_400)
        self.assertEqual(day, dt.date(2026, 9, 18))

    def test_start_time_wins_when_both_are_present(self):
        with _FixedLocal(BANGKOK):
            day = bucket_day(
                self._point(
                    start="2026-09-17T17:00:00Z", end="2026-09-18T17:00:00Z"
                ),
                86_400,
            )
        self.assertEqual(day, dt.date(2026, 9, 18))

    def test_an_unreadable_stamp_is_skipped_rather_than_guessed(self):
        self.assertIsNone(bucket_day(self._point(start="soon"), 86_400))
        self.assertIsNone(bucket_day({"interval": {}}, 86_400))
        self.assertIsNone(bucket_day({}, 86_400))


class GeminiWindowTests(unittest.TestCase):
    """What the provider actually asks Monitoring for."""

    def _captured_query(self, zone):
        calls = []
        provider = GeminiProvider()

        def record(token, project, start, end, period):
            calls.append((start, end, period))
            return []

        with _FixedLocal(zone), patch.object(provider, "_query", record):
            provider._history("token", "project", 3)
        return calls

    def test_the_range_starts_at_local_midnight_in_bangkok(self):
        (start, _end, period), = self._captured_query(BANGKOK)
        self.assertEqual(period, 86_400)
        # Three days ending today: local 2026-09-16 00:00 = 2026-09-15T17:00Z.
        self.assertEqual(start, dt.datetime(2026, 9, 15, 17, 0, tzinfo=UTC))

    def test_the_range_starts_at_local_midnight_in_pacific(self):
        # 03:30Z is still the 17th in Pacific, so three days back is the 15th.
        (start, _end, _period), = self._captured_query(PACIFIC)
        self.assertEqual(start, dt.datetime(2026, 9, 15, 7, 0, tzinfo=UTC))

    def test_a_bangkok_morning_request_lands_on_today(self):
        """The seven hours that used to fall off the front of the day."""
        provider = GeminiProvider()
        # 2026-09-18 01:00 Bangkok = 2026-09-17T18:00Z, inside the local day.
        points = [{
            "interval": {"startTime": "2026-09-17T17:00:00Z"},
            "value": {"int64Value": "42"},
        }]
        with _FixedLocal(BANGKOK), patch.object(
            provider, "_query", lambda *a, **k: [{"points": points}]
        ):
            buckets, total = provider._history("token", "project", 3)
        self.assertEqual(total, 42)
        self.assertEqual(buckets[-1].day, dt.date(2026, 9, 18))
        self.assertEqual(buckets[-1].per_model.get("Requests"), 42.0)


class GaugeSeverityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])
        cls.theme = resolve("light")

    def _gauge(self, percent, reported="normal") -> QuotaGauge:
        gauge = QuotaGauge(self.theme)
        self.addCleanup(gauge.deleteLater)
        gauge.set_meter(
            Meter(
                key="session",
                title="Session",
                subtitle="5-hour window",
                percent=percent,
                severity=reported,
            )
        )
        return gauge

    def test_a_server_calling_95_percent_normal_is_still_critical(self):
        """The reported mismatch: red arc, Critical word, ordinary colour."""
        gauge = self._gauge(95.0, reported="normal")
        self.assertEqual(gauge.effective_severity(), "critical")
        self.assertEqual(gauge._arc.severity, "critical")
        self.assertIn("Critical", gauge._severity.text())
        self.assertIn(
            severity_color(self.theme, "critical").lstrip("#").lower(),
            gauge._severity.styleSheet().lower(),
        )

    def test_the_colour_is_the_ordinary_ink_while_normal(self):
        gauge = self._gauge(10.0)
        self.assertEqual(gauge.effective_severity(), "normal")
        self.assertIn(
            self.theme.ink_secondary.lstrip("#").lower(),
            gauge._severity.styleSheet().lower(),
        )

    def test_the_thresholds_are_the_shared_ones(self):
        self.assertEqual(self._gauge(WARNING_PERCENT).effective_severity(), "warning")
        self.assertEqual(
            self._gauge(CRITICAL_PERCENT).effective_severity(), "critical"
        )
        self.assertEqual(
            self._gauge(WARNING_PERCENT - 0.1).effective_severity(), "normal"
        )

    def test_a_worse_server_severity_is_never_masked(self):
        self.assertEqual(self._gauge(5.0, "critical").effective_severity(), "critical")

    def test_a_meter_with_no_percentage_keeps_what_the_server_said(self):
        """Gemini has no denominator; there is nothing local to apply."""
        gauge = self._gauge(None, reported="warning")
        self.assertEqual(gauge.effective_severity(), "warning")

    def test_the_dashboard_agrees_with_the_widget_and_the_tray(self):
        """All three read the same rule; only this page did not."""
        for percent, reported in ((95.0, "normal"), (80.0, "normal"), (5.0, "serious")):
            with self.subTest(percent=percent, reported=reported):
                self.assertEqual(
                    self._gauge(percent, reported).effective_severity(),
                    severity_for(percent, reported),
                )


if __name__ == "__main__":
    unittest.main()
