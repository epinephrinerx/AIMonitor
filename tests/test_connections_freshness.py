"""The connections page tracks the refresh instead of freezing at start-up.

Reported from a real install: the dashboard kept updating every three minutes
while the Connections page went on saying "Connected" for a login that had
expired, until Re-detect was pressed by hand. Every provider already re-detects
on its way to fetching and reports what it found on the snapshot; the window
was throwing that away.
"""

from __future__ import annotations

import unittest

from ai_usage_monitor.detection import CONNECTED, EXPIRED, MISSING, Detection, adopt
from ai_usage_monitor.providers.base import ProviderSnapshot


def _snapshot(provider_id: str, detection: Detection | None) -> ProviderSnapshot:
    return ProviderSnapshot(provider_id=provider_id, detection=detection)


def _detection(provider_id: str, state: str) -> Detection:
    return Detection(provider_id=provider_id, state=state, source_label="CLI login")


class AdoptTests(unittest.TestCase):
    def test_expiring_between_refreshes_is_picked_up(self):
        detections = {"openai": _detection("openai", CONNECTED)}
        adopt(detections, {"openai": _snapshot("openai", _detection("openai", EXPIRED))})
        self.assertEqual(detections["openai"].state, EXPIRED)
        self.assertEqual(detections["openai"].word, "Expired")

    def test_signing_back_in_is_picked_up_too(self):
        """The stale card cuts both ways: a renewed login must clear as well."""
        detections = {"gemini": _detection("gemini", EXPIRED)}
        adopt(detections, {"gemini": _snapshot("gemini", _detection("gemini", CONNECTED))})
        self.assertEqual(detections["gemini"].state, CONNECTED)

    def test_a_snapshot_without_a_detection_leaves_the_card_alone(self):
        """Better a card that is one cycle old than a card blanked by a hiccup."""
        known = _detection("claude", CONNECTED)
        detections = {"claude": known}
        adopt(detections, {"claude": _snapshot("claude", None)})
        self.assertIs(detections["claude"], known)

    def test_a_service_missing_from_the_result_is_left_alone(self):
        """A disabled service is not monitored; it is not 'not connected'."""
        known = _detection("gemini", CONNECTED)
        detections = {"gemini": known}
        adopt(detections, {"claude": _snapshot("claude", _detection("claude", MISSING))})
        self.assertIs(detections["gemini"], known)
        self.assertEqual(detections["claude"].state, MISSING)

    def test_one_provider_does_not_disturb_another(self):
        detections = {
            "claude": _detection("claude", CONNECTED),
            "openai": _detection("openai", CONNECTED),
        }
        adopt(
            detections,
            {"openai": _snapshot("openai", _detection("openai", EXPIRED))},
        )
        self.assertEqual(detections["claude"].state, CONNECTED)
        self.assertEqual(detections["openai"].state, EXPIRED)

    def test_returns_the_same_map_it_was_given(self):
        detections: dict = {}
        self.assertIs(adopt(detections, {}), detections)


class ProviderContractTests(unittest.TestCase):
    """`adopt` is only useful while every provider keeps filling this in."""

    def test_every_provider_reports_a_detection_on_every_fetch_path(self):
        import ast
        import pathlib

        root = pathlib.Path(__file__).resolve().parent.parent
        providers = root / "ai_usage_monitor" / "providers"
        for name in ("claude_provider.py", "openai_provider.py", "gemini_provider.py"):
            source = (providers / name).read_text(encoding="utf-8")
            tree = ast.parse(source)
            constructed = [
                node
                for node in ast.walk(tree)
                if isinstance(node, ast.Call)
                and isinstance(node.func, ast.Name)
                and node.func.id == "ProviderSnapshot"
            ]
            self.assertTrue(constructed, f"{name} builds no snapshot")
            for call in constructed:
                keywords = {kw.arg for kw in call.keywords}
                self.assertIn(
                    "detection",
                    keywords,
                    f"{name}:{call.lineno} builds a snapshot with no detection, "
                    "so the connections page would go stale on that path",
                )


if __name__ == "__main__":
    unittest.main()
