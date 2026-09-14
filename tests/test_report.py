"""The usage log: what it says about each service, and what it never invents."""

from __future__ import annotations

import datetime as dt
import unittest

from ai_usage_monitor import report as reporting
from ai_usage_monitor.detection import CONNECTED, EXPIRED, Detection
from ai_usage_monitor.providers.base import HistoryView, Provider, ProviderSnapshot
from ai_usage_monitor.usage_log import DayBucket


class _Fake(Provider):
    def __init__(self, provider_id: str, name: str) -> None:
        self.id = provider_id
        self.display_name = name

    def configure(self, key: str, extra: str = "") -> None:
        return

    def is_configured(self) -> bool:
        return True


CLAUDE = _Fake("claude", "Claude")
OPENAI = _Fake("openai", "OpenAI")
GEMINI = _Fake("gemini", "Gemini")

WHEN = dt.datetime(2026, 9, 14, 12, 0, tzinfo=dt.timezone.utc)


def _history(pairs, metric="Total tokens"):
    return HistoryView(
        buckets=[
            DayBucket(day=day, per_model=per_model) for day, per_model in pairs
        ],
        series=["Total"],
        days=3,
        metric=metric,
    )


def _snapshot(provider_id, **kwargs):
    return ProviderSnapshot(provider_id=provider_id, **kwargs)


class BuildTests(unittest.TestCase):
    def test_reports_provider_plan_and_daily_tokens(self):
        """The three things the log was asked for: who, what plan, per day."""
        snapshots = {
            "claude": _snapshot(
                "claude",
                configured=True,
                account="Apichart Chantanis — Max plan · sonnet 4",
                detection=Detection(
                    provider_id="claude",
                    state=CONNECTED,
                    source_label="Claude Code login",
                    account="Max plan",
                ),
                history=_history([
                    (dt.date(2026, 9, 12), {"opus": 13_500_000.0}),
                    (dt.date(2026, 9, 13), {}),
                    (dt.date(2026, 9, 14), {"opus": 90_800_000.0}),
                ]),
            ),
        }
        report = reporting.build(
            [CLAUDE], snapshots, {}, 3, "Total tokens", generated_at=WHEN
        )
        section = report.sections[0]
        self.assertEqual(section.provider_name, "Claude")
        self.assertEqual(section.account, "Apichart Chantanis — Max plan · sonnet 4")
        self.assertEqual(section.source, "Claude Code login")
        self.assertEqual(section.status, "Connected")
        self.assertEqual([row.value for row in section.rows],
                         [13_500_000.0, 0.0, 90_800_000.0])
        self.assertEqual(section.total, 104_300_000.0)
        # A day with no usage is a real day that scored zero, not an active one.
        self.assertEqual(section.active_days, 2)

    def test_detection_supplies_the_account_when_the_snapshot_has_none(self):
        snapshots = {"openai": _snapshot("openai", configured=True)}
        detections = {
            "openai": Detection(
                provider_id="openai",
                state=CONNECTED,
                source_label="Codex CLI login (ChatGPT)",
                account="someone@example.invalid",
            )
        }
        report = reporting.build(
            [OPENAI], snapshots, detections, 3, "Total tokens", generated_at=WHEN
        )
        section = report.sections[0]
        self.assertEqual(section.account, "someone@example.invalid")
        self.assertEqual(section.status, "Connected")

    def test_rows_are_sorted_by_day(self):
        snapshots = {
            "claude": _snapshot(
                "claude",
                configured=True,
                history=_history([
                    (dt.date(2026, 9, 14), {"m": 3.0}),
                    (dt.date(2026, 9, 12), {"m": 1.0}),
                    (dt.date(2026, 9, 13), {"m": 2.0}),
                ]),
            )
        }
        report = reporting.build(
            [CLAUDE], snapshots, {}, 3, "Total tokens", generated_at=WHEN
        )
        self.assertEqual(
            [row.day.day for row in report.sections[0].rows], [12, 13, 14]
        )

    def test_a_failed_service_keeps_its_section_and_says_why(self):
        """One service failing must not drop it from the log silently."""
        snapshots = {
            "gemini": _snapshot(
                "gemini",
                configured=False,
                error="Run `gemini` and sign in again to refresh the token.",
                detection=Detection(
                    provider_id="gemini",
                    state=EXPIRED,
                    source_label="Gemini CLI login",
                    account="someone@example.invalid",
                ),
            )
        }
        report = reporting.build(
            [GEMINI], snapshots, {}, 3, "Total tokens", generated_at=WHEN
        )
        section = report.sections[0]
        self.assertEqual(section.status, "Expired")
        self.assertIn("sign in again", section.note)
        self.assertEqual(section.rows, [])
        self.assertEqual(section.total, 0)

    def test_a_service_with_no_snapshot_is_marked_not_monitored(self):
        report = reporting.build(
            [GEMINI], {}, {}, 3, "Total tokens", generated_at=WHEN
        )
        self.assertEqual(report.sections[0].note, "Not monitored.")

    def test_sections_follow_provider_order(self):
        report = reporting.build(
            [CLAUDE, OPENAI, GEMINI], {}, {}, 3, "Total tokens", generated_at=WHEN
        )
        self.assertEqual(
            [s.provider_id for s in report.sections], ["claude", "openai", "gemini"]
        )

    def test_no_days_are_invented_when_the_service_reports_none(self):
        """Empty history stays empty - never padded out to `days` zero rows."""
        snapshots = {"openai": _snapshot("openai", configured=True)}
        report = reporting.build(
            [OPENAI], snapshots, {}, 14, "Total tokens", generated_at=WHEN
        )
        self.assertEqual(report.sections[0].rows, [])
        self.assertIn("no daily history", report.sections[0].note)


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.snapshots = {
            "claude": _snapshot(
                "claude",
                configured=True,
                account="Apichart Chantanis — Max plan",
                history=_history([
                    (dt.date(2026, 9, 13), {"opus": 1_234_567.0}),
                    (dt.date(2026, 9, 14), {"opus": 2_000_000.0}),
                ]),
            )
        }
        self.report = reporting.build(
            [CLAUDE], self.snapshots, {}, 2, "Total tokens", generated_at=WHEN
        )

    def test_csv_is_one_flat_row_per_day(self):
        lines = reporting.to_csv(self.report).strip().splitlines()
        self.assertEqual(
            lines[0],
            "date,provider,account,source,status,metric,value",
        )
        self.assertEqual(len(lines), 3)
        self.assertTrue(lines[1].startswith("2026-09-13,Claude,"))
        self.assertTrue(lines[1].endswith(",1234567"))

    def test_markdown_carries_the_plan_and_a_total(self):
        text = reporting.to_markdown(self.report)
        self.assertIn("## Claude", text)
        self.assertIn("Max plan", text)
        self.assertIn("| 2026-09-14 | 2,000,000 |", text)
        self.assertIn("**3,234,567**", text)

    def test_html_escapes_account_text(self):
        self.snapshots["claude"].account = "<script>alert(1)</script>"
        report = reporting.build(
            [CLAUDE], self.snapshots, {}, 2, "Total tokens", generated_at=WHEN
        )
        html = reporting.to_html(report)
        self.assertNotIn("<script>", html)
        self.assertIn("&lt;script&gt;", html)

    def test_printing_never_uses_the_dark_palette(self):
        """A dark-themed window still prints dark ink on white paper."""
        light = reporting.to_html(self.report, dark=False)
        dark = reporting.to_html(self.report, dark=True)
        self.assertIn("#1b1b1f", light)
        self.assertIn("#e8e8ea", dark)
        self.assertNotIn("#e8e8ea", light)

    def test_a_money_metric_is_labelled_and_formatted_as_money(self):
        """Printing while the dashboard shows dollars must not say 'tokens'."""
        report = reporting.build(
            [CLAUDE], self.snapshots, {}, 2, "Equivalent value", generated_at=WHEN
        )
        text = reporting.to_markdown(report)
        self.assertIn("Equivalent value", text)
        self.assertIn("$2,000,000.00", text)
        csv_text = reporting.to_csv(report)
        self.assertIn(",Equivalent value,2000000.00", csv_text)


class SaveFormatTests(unittest.TestCase):
    """Which renderer Save as… picks, per extension."""

    def setUp(self):
        snapshots = {
            "claude": _snapshot(
                "claude",
                configured=True,
                account="Max plan",
                history=_history([(dt.date(2026, 9, 14), {"opus": 5.0})]),
            )
        }
        self.report = reporting.build(
            [CLAUDE], snapshots, {}, 1, "Total tokens", generated_at=WHEN
        )

    def test_extension_picks_the_renderer(self):
        from ai_usage_monitor.log_dialog import render

        self.assertTrue(render(self.report, ".csv").startswith("date,provider"))
        self.assertIn("<table", render(self.report, ".html"))
        self.assertIn("<table", render(self.report, ".htm"))
        self.assertTrue(render(self.report, ".md").startswith("# AI Usage Monitor"))
        # Anything else gets the readable one rather than nothing.
        self.assertTrue(render(self.report, ".txt").startswith("# AI Usage Monitor"))

    def test_printable_document_is_built_from_the_light_html(self):
        from ai_usage_monitor.log_dialog import printable
        # Importing Qt widgets is enough; building a QTextDocument needs no
        # QApplication, and this is the document Print actually puts on paper.
        self.assertIn("usage log", printable(self.report).toPlainText())

    def test_saved_html_is_never_the_dark_palette(self):
        """A file outlives the theme it was saved under."""
        from ai_usage_monitor.log_dialog import render

        self.assertNotIn("#e8e8ea", render(self.report, ".html"))

    def test_default_filename_is_dated(self):
        from ai_usage_monitor.log_dialog import default_filename

        self.assertTrue(default_filename(WHEN).endswith(".csv"))
        self.assertIn("2026-09-14", default_filename(WHEN))


if __name__ == "__main__":
    unittest.main()
