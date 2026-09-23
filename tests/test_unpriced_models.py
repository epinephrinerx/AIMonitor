"""The equivalent-value figure says what it left out.

`rate_for()` falls back to `Rate(0.0, 0.0)` for a model it does not know, so
`cost()` added nothing for it and the total came out quietly short - guaranteed
to happen every time a new model ships ahead of the price table. A dashboard
that prints a dollar figure it knows is incomplete, without saying so, is worse
than one that admits the gap: elsewhere this project refuses to invent missing
days or percentages for exactly the same reason.
"""

from __future__ import annotations

import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from ai_usage_monitor import pricing
from ai_usage_monitor.providers.claude_provider import ClaudeProvider
from ai_usage_monitor.usage_log import Counters, TranscriptStore


class IsPricedTests(unittest.TestCase):
    def test_a_known_model_is_priced(self):
        self.assertTrue(pricing.is_priced("claude-opus-5"))

    def test_a_dated_snapshot_still_resolves(self):
        """`-20251001` is trimmed before the table is consulted."""
        self.assertTrue(pricing.is_priced("claude-haiku-4-5-20251001"))

    def test_a_future_point_release_is_priced_but_only_as_an_estimate(self):
        """Priced, yes - but the caller has to be able to tell it apart.

        The first version of this test asserted only `is_priced`, which review
        rightly called passing for the opposite of the contract: the whole
        point is "no published price", and the prefix table hands one over for
        any id in a known family. It is still worth using - a figure that
        collapses the day a new model ships is no use either - but the caption
        has to say so, and that needs a third state, not a boolean.
        """
        self.assertTrue(pricing.is_priced("claude-opus-9-9"))
        self.assertEqual(pricing.price_kind("claude-opus-9-9"), pricing.ESTIMATED)

    def test_a_new_family_is_not_priced(self):
        self.assertFalse(pricing.is_priced("claude-newfamily-1"))

    def test_a_foreign_model_is_not_priced(self):
        self.assertFalse(pricing.is_priced("gpt-5"))

    def test_an_empty_model_is_not_priced(self):
        self.assertFalse(pricing.is_priced(""))


class PriceKindTests(unittest.TestCase):
    """Three states, because two of them are not the same kind of doubt."""

    def test_a_published_model_is_exact(self):
        self.assertEqual(pricing.price_kind("claude-opus-5"), pricing.EXACT)

    def test_a_dated_snapshot_is_still_exact(self):
        self.assertEqual(
            pricing.price_kind("claude-haiku-4-5-20251001"), pricing.EXACT
        )

    def test_an_unknown_family_is_unknown(self):
        self.assertEqual(pricing.price_kind("gpt-5"), pricing.UNKNOWN)
        self.assertEqual(pricing.price_kind(""), pricing.UNKNOWN)

    def test_the_family_rate_really_can_be_wrong(self):
        """Why the distinction earns its keep, in the published numbers.

        This is not hypothetical rounding. Anthropic dropped Sonnet's input
        price between 4.6 and 5, and the prefix entry still carries the older
        pair - so a future `claude-sonnet-7` would be billed by this app at
        $3/$15 while the family's most recent member lists at $2/$10. Any
        guess made this way can be out by half, in either direction.
        """
        older = pricing.rate_for("claude-sonnet-4-6")
        newer = pricing.rate_for("claude-sonnet-5")
        guessed = pricing.rate_for("claude-sonnet-7")
        self.assertNotEqual(
            (older.input, older.output),
            (newer.input, newer.output),
            "the premise is gone: the family now prices consistently",
        )
        self.assertEqual(pricing.price_kind("claude-sonnet-7"), pricing.ESTIMATED)
        self.assertNotEqual(
            (guessed.input, guessed.output),
            (newer.input, newer.output),
            "the guess happens to match the newest rate, so it reads as exact",
        )

    def test_the_estimate_is_the_family_rate_and_not_zero(self):
        """It must still contribute to the total, or the figure collapses."""
        rate = pricing.rate_for("claude-opus-9-9")
        self.assertGreater(rate.input, 0)
        self.assertGreater(rate.output, 0)

class CountersTests(unittest.TestCase):
    def test_unpriced_tokens_add_and_merge(self):
        left, right = Counters(), Counters()
        left.add(
            input_tokens=1, output_tokens=2, cache_write=3, cache_read=4,
            cost_usd=0.0, unpriced_tokens=10,
        )
        right.add(
            input_tokens=1, output_tokens=1, cache_write=0, cache_read=0,
            cost_usd=5.0,
        )
        left.merge(right)
        self.assertEqual(left.unpriced_tokens, 10)
        self.assertEqual(left.cost_usd, 5.0)

    def test_a_priced_record_adds_nothing_unpriced(self):
        counters = Counters()
        counters.add(
            input_tokens=1, output_tokens=1, cache_write=0, cache_read=0,
            cost_usd=1.0,
        )
        self.assertEqual(counters.unpriced_tokens, 0)


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.project = root / "C--work-project"
        self.project.mkdir()
        self.store = TranscriptStore(root)

    def _record(self, model: str, tokens: int) -> dict:
        return {
            "type": "assistant",
            "timestamp": "2026-09-18T10:00:00Z",
            "message": {
                "id": f"msg-{model}-{tokens}",
                "model": model,
                "usage": {"input_tokens": tokens, "output_tokens": 0},
            },
        }

    def _write(self, *records: dict) -> None:
        with (self.project / "session.jsonl").open("w", encoding="utf-8") as handle:
            for item in records:
                handle.write(json.dumps(item) + "\n")

    def test_a_family_estimate_is_counted_apart_from_both(self):
        """It pays into the total, and into the estimated column, not unpriced."""
        self._write(self._record("claude-opus-9-9", 1_000_000))
        self.store.refresh()
        totals = self.store.all_totals
        self.assertEqual(totals.unpriced_tokens, 0)
        self.assertEqual(totals.estimated_tokens, 1_000_000)
        self.assertGreater(totals.cost_usd, 0)
        self.assertEqual(self.store.unpriced_in_range(14), [])
        self.assertEqual(
            self.store.estimated_in_range(14), [pricing.display_name("claude-opus-9-9")]
        )

    def test_a_priced_model_counts_nothing_as_unpriced(self):
        self._write(self._record("claude-opus-5", 1_000))
        self.store.refresh()
        self.assertEqual(self.store.all_totals.unpriced_tokens, 0)
        self.assertGreater(self.store.all_totals.cost_usd, 0)
        self.assertEqual(self.store.unpriced_in_range(14), [])
        self.assertEqual(self.store.all_totals.estimated_tokens, 0)
        self.assertEqual(self.store.estimated_in_range(14), [])

    def test_an_unknown_model_is_counted_and_named(self):
        self._write(self._record("gpt-5", 2_000))
        self.store.refresh()
        self.assertEqual(self.store.all_totals.unpriced_tokens, 2_000)
        self.assertEqual(self.store.all_totals.cost_usd, 0.0)
        self.assertEqual(self.store.unpriced_in_range(14), ["gpt-5"])

    def test_the_priced_part_of_a_mixed_range_is_still_right(self):
        self._write(
            self._record("claude-opus-5", 1_000_000),
            self._record("gpt-5", 500),
        )
        self.store.refresh()
        totals = self.store.all_totals
        self.assertEqual(totals.unpriced_tokens, 500)
        self.assertAlmostEqual(totals.cost_usd, 5.0, places=6)


class CaveatTests(unittest.TestCase):
    def setUp(self):
        self.provider = ClaudeProvider()

    def _caveat(
        self,
        unpriced: int,
        models: list[str],
        estimated: int = 0,
        estimates: list[str] | None = None,
    ) -> str:
        totals = Counters()
        totals.unpriced_tokens = unpriced
        totals.estimated_tokens = estimated
        self.provider._store.unpriced_in_range = lambda days: sorted(models)
        self.provider._store.estimated_in_range = lambda days: sorted(estimates or [])
        return self.provider._value_caveat(totals, 14)

    def test_a_family_estimate_is_disclosed(self):
        caveat = self._caveat(0, [], estimated=3_000_000, estimates=["Opus"])
        self.assertIn("at API list price", caveat)
        self.assertIn("3M tokens", caveat)
        self.assertIn("Opus", caveat)
        self.assertIn("family", caveat)
        self.assertNotIn("no published price", caveat, "an estimate is not a gap")

    def test_both_kinds_of_doubt_are_reported_separately(self):
        caveat = self._caveat(
            500, ["gpt-5"], estimated=9_000, estimates=["Sonnet"]
        )
        self.assertIn("excludes", caveat)
        self.assertIn("gpt-5", caveat)
        self.assertIn("estimates", caveat)
        self.assertIn("Sonnet", caveat)

    def test_many_estimated_models_are_counted_too(self):
        caveat = self._caveat(
            0, [], estimated=1, estimates=[f"m{n}" for n in range(4)]
        )
        self.assertIn("4 models", caveat)

    def test_nothing_unpriced_reads_as_it_always_did(self):
        self.assertEqual(self._caveat(0, []), "at API list price")

    def test_one_unknown_model_is_named(self):
        caveat = self._caveat(12_400_000, ["gpt-5"])
        self.assertIn("at API list price", caveat)
        self.assertIn("12.4M tokens", caveat)
        self.assertIn("gpt-5", caveat)
        self.assertIn("no published price", caveat)

    def test_two_models_are_both_named(self):
        caveat = self._caveat(1_000, ["gpt-5", "claude-newfamily-1"])
        self.assertIn("gpt-5", caveat)
        self.assertIn("claude-newfamily-1", caveat)

    def test_many_models_are_counted_rather_than_listed(self):
        """A caption is one line; five model ids would not fit on it."""
        caveat = self._caveat(1_000, [f"model-{n}" for n in range(5)])
        self.assertIn("5 models", caveat)
        self.assertNotIn("model-0", caveat)


class StatTests(unittest.TestCase):
    """The caveat has to reach the tile, not just exist."""

    def test_the_stat_detail_carries_it(self):
        provider = ClaudeProvider()
        totals = Counters()
        totals.unpriced_tokens = 500
        provider._store.unpriced_in_range = lambda days: ["gpt-5"]

        # No login: `fetch` records that and carries on to build the history
        # stats, which is the part under test. Raising the provider's own
        # error type rather than a bare Exception keeps it on that path.
        from ai_usage_monitor import credentials

        with (
            patch.object(provider, "_history", return_value=None),
            patch.object(provider, "_history_note", return_value=None),
            patch.object(provider._store, "window_totals", return_value=totals),
            patch.object(
                credentials,
                "load",
                side_effect=credentials.CredentialsError("no login in a test"),
            ),
        ):
            snapshot = provider.fetch(14, "Total tokens", True)

        detail = next(
            stat.detail
            for stat in snapshot.stats
            if stat.label == "Equivalent API value"
        )
        self.assertIn("no published price", detail)


if __name__ == "__main__":
    unittest.main()


class RangeScopeTests(unittest.TestCase):
    """The names and the token figure have to describe the same range.

    Found by review: the names came from a set that lived as long as the
    store, while the token count came from `window_totals(days)`. A caption
    over the last 7 days could therefore name a model last seen a month ago,
    which is the same class of wrongness the whole change was made to fix.
    """

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.project = root / "C--work-project"
        self.project.mkdir()
        self.store = TranscriptStore(root)

    def _write(self, *records: dict) -> None:
        with (self.project / "session.jsonl").open("w", encoding="utf-8") as handle:
            for item in records:
                handle.write(json.dumps(item) + "\n")

    @staticmethod
    def _record(model: str, days_ago: int) -> dict:
        import datetime as dt

        when = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=days_ago)
        return {
            "type": "assistant",
            "timestamp": when.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "message": {
                "id": f"msg-{model}-{days_ago}",
                "model": model,
                "usage": {"input_tokens": 1_000, "output_tokens": 0},
            },
        }

    def test_a_model_outside_the_range_is_not_named(self):
        self._write(
            self._record("old-model-a", days_ago=40),
            self._record("new-model-b", days_ago=1),
        )
        self.store.refresh()

        recent = self.store.unpriced_in_range(7)
        self.assertEqual(recent, ["new-model-b"], "a model from last month was named")

        wider = self.store.unpriced_in_range(90)
        self.assertEqual(wider, ["new-model-b", "old-model-a"])

    def test_the_names_and_the_total_agree(self):
        self._write(
            self._record("old-model-a", days_ago=40),
            self._record("new-model-b", days_ago=1),
        )
        self.store.refresh()

        totals = self.store.window_totals(7)
        names = self.store.unpriced_in_range(7)
        self.assertEqual(totals.unpriced_tokens, 1_000)
        self.assertEqual(len(names), 1)

    def test_a_priced_model_is_never_named(self):
        self._write(self._record("claude-opus-5", days_ago=1))
        self.store.refresh()
        self.assertEqual(self.store.unpriced_in_range(7), [])

    def test_the_caption_only_names_what_it_excluded(self):
        from ai_usage_monitor.providers.claude_provider import ClaudeProvider

        provider = ClaudeProvider()
        provider._store = self.store
        self._write(
            self._record("old-model-a", days_ago=40),
            self._record("new-model-b", days_ago=1),
        )
        self.store.refresh()

        caveat = provider._value_caveat(self.store.window_totals(7), 7)
        self.assertIn("new-model-b", caveat)
        self.assertNotIn("old-model-a", caveat)
