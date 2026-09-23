"""A history failure is a gap in the page, not a failed service.

Quota percentages come from the provider's server. Local transcripts and
daily-total endpoints have no bearing on them, so losing the history must
never take the gauges with it. Every provider used to report a history
problem through `snapshot.error`, which marked the whole service failed,
put an error banner over live gauges, and stopped the refresh counting as
a success.
"""

from __future__ import annotations

import ast
import datetime as dt
import pathlib
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
    """The rule, checked in the source: providers must not conflate the two.

    The first version of this test looked for one exact shape - assigning a
    variable literally named `history_error` to `snapshot.error` - and passed
    while the Admin API path did `snapshot.error = str(exc)` in the handler
    around its history call. A contract test that only recognises the mistake
    it was written from is not a contract test. This one asks the question
    that matters: does any handler wrapped around a history fetch report the
    whole service as failed?
    """

    PROVIDERS = ("claude_provider.py", "openai_provider.py", "gemini_provider.py")

    @staticmethod
    def _calls(node) -> list[str]:
        names = []
        for inner in ast.walk(node):
            if isinstance(inner, ast.Call):
                func = inner.func
                name = getattr(func, "attr", None) or getattr(func, "id", "")
                if name:
                    names.append(str(name))
        return names

    @classmethod
    def _history_only(cls, body: list) -> bool:
        """Is the history fetch the only thing in here that can fail?

        The distinction matters. Codex reads quota and history inside one
        call, so a failure there really is the whole service failing and its
        handler is right to set `error`. A block whose only risky operation is
        the history fetch has no such excuse.
        """
        names = [name for stmt in body for name in cls._calls(stmt)]
        if not names:
            return False
        return all("history" in name.lower() for name in names)

    @staticmethod
    def _assigns_error(handler) -> int | None:
        for inner in ast.walk(handler):
            if isinstance(inner, ast.Assign):
                for target in inner.targets:
                    if isinstance(target, ast.Attribute) and target.attr == "error":
                        return inner.lineno
        return None

    def test_no_handler_whose_only_risk_is_history_sets_error(self):
        root = pathlib.Path(__file__).resolve().parent.parent / "ai_usage_monitor"
        examined = 0
        for name in self.PROVIDERS:
            path = root / "providers" / name
            tree = ast.parse(path.read_text(encoding="utf-8"))
            for node in ast.walk(tree):
                if not isinstance(node, ast.Try):
                    continue
                if not self._history_only(node.body):
                    continue
                examined += 1
                for handler in node.handlers:
                    line = self._assigns_error(handler)
                    self.assertIsNone(
                        line,
                        f"{name}:{line}: a handler around a history fetch sets "
                        "snapshot.error, which marks the whole service failed "
                        "and puts a banner over live gauges. Use history_error.",
                    )
        # Without this the test passes on a codebase where the shape it looks
        # for no longer exists - a rename of `_history` would silently turn it
        # into an assertion about nothing.
        self.assertGreater(
            examined,
            0,
            "no history-only try block was found in any provider; this test "
            "matches on the call name, so a rename has made it vacuous",
        )

    def test_every_provider_assigns_history_error_somewhere(self):
        """Not a substring search: an actual assignment to the attribute.

        The first version looked for the word anywhere in the file, which a
        comment or a docstring would have satisfied.
        """
        root = pathlib.Path(__file__).resolve().parent.parent / "ai_usage_monitor"
        for name in self.PROVIDERS:
            tree = ast.parse((root / "providers" / name).read_text(encoding="utf-8"))
            assigned = any(
                isinstance(node, ast.Assign)
                and any(
                    isinstance(target, ast.Attribute)
                    and target.attr == "history_error"
                    for target in node.targets
                )
                for node in ast.walk(tree)
            )
            self.assertTrue(
                assigned,
                f"{name} never assigns snapshot.history_error, so a history "
                "problem there has nowhere to go but the error banner",
            )


if __name__ == "__main__":
    unittest.main()


class AdminApiHistoryTests(unittest.TestCase):
    """The OpenAI path that was missed when `history_error` was introduced.

    Month-to-date and today's spend are read from two requests that have
    already returned by the time the range is fetched. A failure there used to
    be written to `snapshot.error`, which put a banner over both of them and
    stopped the refresh counting as a success.
    """

    def _provider(self):
        from ai_usage_monitor.providers.openai_provider import OpenAIProvider

        provider = OpenAIProvider()
        provider.configure("sk-admin-test", "")
        return provider

    def _fetch_with_failing_history(self, provider):
        from ai_usage_monitor.providers import openai_provider as module
        from ai_usage_monitor.providers import sources

        with (
            patch.object(sources, "read_json", return_value=None),
            patch.object(provider, "_total_cost", side_effect=[20.0, 3.0]),
            patch.object(
                provider,
                "_history",
                side_effect=module._OpenAIError("Usage history unavailable"),
            ),
        ):
            return provider.fetch(14, "Total tokens", True)

    def test_live_spend_survives_a_failed_history(self):
        snapshot = self._fetch_with_failing_history(self._provider())
        self.assertEqual(snapshot.history_error, "Usage history unavailable")
        self.assertIsNone(snapshot.error)
        self.assertTrue(snapshot.ok, "a refresh with live spend is a success")

    def test_both_spend_meters_are_still_there(self):
        snapshot = self._fetch_with_failing_history(self._provider())
        titles = [meter.title for meter in snapshot.meters]
        self.assertIn("Month to date", titles)
        self.assertIn("Today", titles)

    def test_a_budget_meter_keeps_its_percentage(self):
        provider = self._provider()
        provider.configure("sk-admin-test", "100")
        snapshot = self._fetch_with_failing_history(provider)
        lead = snapshot.meters[0]
        self.assertEqual(lead.key, "month_budget")
        self.assertEqual(lead.percent, 20.0)
        self.assertIsNone(snapshot.error)
