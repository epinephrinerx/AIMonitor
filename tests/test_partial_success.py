"""A history failure is a gap in the page, not a failed service.

Quota percentages come from the provider's server. Local transcripts and
daily-total endpoints have no bearing on them, so losing the history must
never take the gauges with it. Every provider used to report a history
problem through `snapshot.error`, which marked the whole service failed,
put an error banner over live gauges, and stopped the refresh counting as
a success.
"""

from __future__ import annotations

import datetime as dt
import unittest
from unittest.mock import Mock, patch

from ai_usage_monitor import report as reporting
from ai_usage_monitor.providers.base import HistoryView, Meter, ProviderSnapshot
from ai_usage_monitor.providers.claude_provider import ClaudeProvider
from ai_usage_monitor.providers.gemini_provider import GeminiProvider, _GeminiError
from ai_usage_monitor.usage_log import DayBucket


class SnapshotContractTests(unittest.TestCase):
    def test_history_error_alone_leaves_the_snapshot_successful(self):
        snapshot = ProviderSnapshot(
            provider_id="x",
            configured=True,
            meters=[Meter(key="s", title="Session", subtitle="", percent=12.0)],
            history_error="Daily totals unavailable",
        )
        self.assertTrue(snapshot.ok)
        self.assertIsNone(snapshot.error)

    def test_a_real_error_still_fails(self):
        snapshot = ProviderSnapshot(
            provider_id="x", configured=True, error="Token expired"
        )
        self.assertFalse(snapshot.ok)

    def test_the_two_channels_are_independent(self):
        snapshot = ProviderSnapshot(
            provider_id="x",
            configured=True,
            error="Token expired",
            history_error="Daily totals unavailable",
        )
        self.assertFalse(snapshot.ok)
        self.assertEqual(snapshot.history_error, "Daily totals unavailable")


class ClaudeHistoryNoteTests(unittest.TestCase):
    """Claude's history is local files; the gauges are an OAuth endpoint."""

    def setUp(self):
        self.provider = ClaudeProvider()

    def test_clean_store_reports_nothing(self):
        self.provider._store.last_error = None
        self.provider._store.malformed = 0
        self.assertIsNone(self.provider._history_note())

    def test_a_missing_transcript_directory_is_reported(self):
        self.provider._store.last_error = "No transcripts directory at C:\\x"
        self.assertIn("No transcripts directory", self.provider._history_note())

    def test_skipped_records_are_counted_and_the_gauges_excused(self):
        self.provider._store.last_error = None
        self.provider._store.malformed = 3
        note = self.provider._history_note()
        self.assertIn("3 transcript records", note)
        self.assertIn("quota gauges are unaffected", note)

    def test_one_skipped_record_reads_as_singular(self):
        self.provider._store.last_error = None
        self.provider._store.malformed = 1
        self.assertIn("1 transcript record could not", self.provider._history_note())


class GeminiHistoryTests(unittest.TestCase):
    def test_a_failed_range_keeps_the_today_meter(self):
        """The Today meter was already read and good when the range failed."""
        provider = GeminiProvider()
        detected = Mock(
            usable=True,
            state="connected",
            account="someone@example.invalid",
            credential=Mock(project="proj"),
            hint="",
        )
        with (
            patch.object(provider, "detect", return_value=detected),
            patch.object(provider, "_bearer", return_value="token"),
            patch.object(provider, "_series_total", return_value=7),
            patch.object(
                provider, "_history", side_effect=_GeminiError("Monitoring is down")
            ),
        ):
            snapshot = provider.fetch(14, "Total tokens", True)

        self.assertIsNone(snapshot.error)
        self.assertEqual(snapshot.history_error, "Monitoring is down")
        self.assertTrue(snapshot.meters, "the Today meter must survive")
        self.assertTrue(snapshot.ok)


class ReportTests(unittest.TestCase):
    """The log is a document about history, so it has to say when it is short."""

    class _Fake:
        id = "claude"
        display_name = "Claude"

    def _report(self, snapshot):
        return reporting.build(
            [self._Fake()],
            {"claude": snapshot},
            {},
            14,
            "Total tokens",
            generated_at=dt.datetime(2026, 9, 18, tzinfo=dt.timezone.utc),
        )

    def test_a_partial_history_is_flagged_even_though_rows_arrived(self):
        snapshot = ProviderSnapshot(
            provider_id="claude",
            configured=True,
            history_error="2 transcript records could not be read.",
            history=HistoryView(
                buckets=[DayBucket(day=dt.date(2026, 9, 18), per_model={"m": 5.0})],
                series=["Total"],
                days=14,
                metric="Total tokens",
            ),
        )
        section = self._report(snapshot).sections[0]
        self.assertEqual(len(section.rows), 1)
        self.assertIn("could not be read", section.note)

    def test_a_real_error_outranks_the_history_note(self):
        snapshot = ProviderSnapshot(
            provider_id="claude",
            configured=True,
            error="Token expired",
            history_error="Daily totals unavailable",
        )
        self.assertEqual(self._report(snapshot).sections[0].note, "Token expired")

    def test_no_history_and_no_problem_still_says_so(self):
        snapshot = ProviderSnapshot(provider_id="claude", configured=True)
        self.assertIn(
            "no daily history", self._report(snapshot).sections[0].note
        )


class NoHistoryErrorInTheBannerTests(unittest.TestCase):
    """The rule, checked in the source: providers must not conflate the two."""

    def test_codex_history_error_is_not_assigned_to_error(self):
        import ast
        import pathlib

        source = (
            pathlib.Path(__file__).resolve().parent.parent
            / "ai_usage_monitor"
            / "providers"
            / "openai_provider.py"
        ).read_text(encoding="utf-8")
        tree = ast.parse(source)
        for node in ast.walk(tree):
            if not isinstance(node, ast.Assign):
                continue
            targets = [
                t.attr for t in node.targets if isinstance(t, ast.Attribute)
            ]
            if "error" not in targets:
                continue
            if isinstance(node.value, ast.Name):
                self.assertNotEqual(
                    node.value.id,
                    "history_error",
                    f"line {node.lineno}: a history failure assigned to "
                    "snapshot.error marks the whole service failed",
                )


if __name__ == "__main__":
    unittest.main()
