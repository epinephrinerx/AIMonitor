"""One bad transcript line must not cost a record, a refresh, or the gauges.

Found by review on 2026-09-18, and worse on inspection than reported. The
de-duplication id was recorded before the record was validated, so a record
with an unreadable token count did two kinds of damage:

* `int("not-a-number")` raised out of `_ingest`, out of `TranscriptStore`,
  out of `ClaudeProvider.fetch` and into the worker's blanket handler, which
  turned the whole snapshot into "Unexpected error". The quota gauges come
  from the server and have nothing to do with local transcripts, but they
  vanished too - which the project guidelines forbid outright.
* The id was already in the seen-set, so the same record could never be
  counted afterwards, even from a corrected copy.
"""

from __future__ import annotations

import datetime as dt
import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from ai_usage_monitor.usage_log import TranscriptStore, token_count

WHEN = "2026-09-14T10:00:00Z"


def record(message_id: str = "msg-1", **usage_overrides) -> dict:
    usage = {
        "input_tokens": 100,
        "output_tokens": 200,
        "cache_read_input_tokens": 300,
        "cache_creation_input_tokens": 400,
    }
    usage.update(usage_overrides)
    return {
        "type": "assistant",
        "timestamp": WHEN,
        "cwd": "C:\\work\\project",
        "message": {
            "id": message_id,
            "model": "claude-opus-4",
            "usage": usage,
        },
    }


class TokenCountTests(unittest.TestCase):
    def test_missing_and_empty_are_zero(self):
        """Transcripts omit fields that did not apply."""
        self.assertEqual(token_count(None), 0)
        self.assertEqual(token_count(""), 0)
        self.assertEqual(token_count("  "), 0)

    def test_plain_integers_pass_through(self):
        self.assertEqual(token_count(0), 0)
        self.assertEqual(token_count(12345), 12345)

    def test_numeric_strings_are_accepted(self):
        """The old code did `int(value or 0)`; keep that tolerance."""
        self.assertEqual(token_count("42"), 42)
        self.assertEqual(token_count(" 42 "), 42)

    def test_whole_floats_are_accepted_but_fractions_are_not(self):
        self.assertEqual(token_count(1.0), 1)
        self.assertIsNone(token_count(1.5))

    def test_a_bool_is_not_a_token_count(self):
        """Python says isinstance(True, int); a transcript saying so is wrong."""
        self.assertIsNone(token_count(True))
        self.assertIsNone(token_count(False))

    def test_negatives_and_nonsense_are_rejected(self):
        self.assertIsNone(token_count(-1))
        self.assertIsNone(token_count("-1"))
        self.assertIsNone(token_count("not-a-number"))
        self.assertIsNone(token_count([1]))
        self.assertIsNone(token_count({"n": 1}))


class IngestTests(unittest.TestCase):
    def setUp(self):
        self.store = TranscriptStore(Path("unused"))

    def _ingest(self, data: dict) -> bool:
        return self.store._ingest(data, "fallback")

    def test_a_good_record_counts(self):
        self.assertTrue(self._ingest(record()))
        self.assertEqual(self.store.all_totals.messages, 1)
        self.assertEqual(self.store.all_totals.input_tokens, 100)
        self.assertEqual(self.store.malformed, 0)

    def test_a_malformed_token_count_never_raises(self):
        """The reported crash. It must be a skipped record, not an exception."""
        self.assertFalse(self._ingest(record(input_tokens="not-a-number")))
        self.assertEqual(self.store.all_totals.messages, 0)
        self.assertEqual(self.store.malformed, 1)

    def test_a_malformed_record_does_not_claim_its_id(self):
        """The subtler half: a corrected copy must still be able to count."""
        self._ingest(record("msg-7", output_tokens="oops"))
        self.assertNotIn("msg-7", self.store._seen_ids)

        self.assertTrue(self._ingest(record("msg-7")))
        self.assertEqual(self.store.all_totals.messages, 1)
        self.assertEqual(self.store.all_totals.output_tokens, 200)

    def test_a_good_record_still_de_duplicates(self):
        self.assertTrue(self._ingest(record("msg-9")))
        self.assertFalse(self._ingest(record("msg-9")))
        self.assertEqual(self.store.all_totals.messages, 1)

    def test_a_bad_timestamp_is_skipped_without_claiming_the_id(self):
        self.assertFalse(self._ingest({**record("msg-3"), "timestamp": "yesterday"}))
        self.assertEqual(self.store.malformed, 1)
        self.assertNotIn("msg-3", self.store._seen_ids)

    def test_a_missing_timestamp_is_skipped(self):
        data = record("msg-4")
        del data["timestamp"]
        self.assertFalse(self._ingest(data))
        self.assertNotIn("msg-4", self.store._seen_ids)

    def test_a_negative_count_is_treated_as_malformed(self):
        self.assertFalse(self._ingest(record(cache_read_input_tokens=-5)))
        self.assertEqual(self.store.malformed, 1)

    def test_non_assistant_records_are_not_counted_as_malformed(self):
        """Skipping a user record is normal traffic, not a defect."""
        self.assertFalse(self._ingest({"type": "user", "timestamp": WHEN}))
        self.assertFalse(self._ingest({"type": "assistant", "message": "text"}))
        self.assertEqual(self.store.malformed, 0)

    def test_the_ephemeral_cache_split_is_validated_too(self):
        data = record()
        data["message"]["usage"]["cache_creation"] = {
            "ephemeral_5m_input_tokens": 10,
            "ephemeral_1h_input_tokens": "bad",
        }
        self.assertFalse(self._ingest(data))
        self.assertEqual(self.store.malformed, 1)


class FileReadTests(unittest.TestCase):
    """The same guarantee through the real file path, not just `_ingest`."""

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.project = self.root / "C--work-project"
        self.project.mkdir()
        self.store = TranscriptStore(self.root)

    def _write(self, *records: dict) -> Path:
        path = self.project / "session.jsonl"
        with path.open("w", encoding="utf-8") as handle:
            for item in records:
                handle.write(json.dumps(item) + "\n")
        return path

    def test_one_bad_line_does_not_stop_the_good_ones(self):
        self._write(
            record("good-1"),
            record("bad-1", input_tokens="not-a-number"),
            record("good-2"),
        )
        added = self.store.refresh()
        self.assertEqual(added, 2)
        self.assertEqual(self.store.all_totals.messages, 2)
        self.assertEqual(self.store.malformed, 1)
        self.assertIsNone(self.store.last_error)

    def test_refresh_never_raises_on_a_bad_line(self):
        """What the crash actually cost: the whole refresh, and the gauges."""
        self._write(record("bad-1", output_tokens=object.__name__ + "!"))
        try:
            self.store.refresh()
        except Exception as exc:  # noqa: BLE001 - that is the point of the test
            self.fail(f"refresh() raised {exc!r}; the Claude snapshot would blank")

    def test_the_offset_advances_past_a_bad_line(self):
        """A bad line used to leave the offset unmoved and be re-read forever."""
        path = self._write(record("bad-1", input_tokens="x"))
        self.store.refresh()
        self.assertEqual(self.store._offsets[path], path.stat().st_size)

    def test_a_corrected_record_counts_on_a_later_refresh(self):
        self._write(record("msg-1", input_tokens="x"))
        self.store.refresh()
        self.assertEqual(self.store.all_totals.messages, 0)

        # The writer fixes the line and appends the corrected record.
        with (self.project / "session.jsonl").open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record("msg-1")) + "\n")
        self.store.refresh()
        self.assertEqual(self.store.all_totals.messages, 1)
        self.assertEqual(self.store.all_totals.input_tokens, 100)

    def test_totals_land_on_the_local_day(self):
        self._write(record())
        self.store.refresh()
        expected = (
            dt.datetime.fromisoformat(WHEN.replace("Z", "+00:00"))
            .astimezone()
            .date()
        )
        self.assertIn(expected, self.store.by_day_model)


if __name__ == "__main__":
    unittest.main()
