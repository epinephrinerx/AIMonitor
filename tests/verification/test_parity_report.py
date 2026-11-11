"""Regression: the parity report pairs agy/gemini counterparts, lists 2.0-only and
missing-in-2.0 screens, and preserves owner records (SPEC R-005 V, V3/V10)."""
from pathlib import Path
import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]

# Exact strings required by Docs/SPEC_R005_EVIDENCE.md (V3, V3.1, V3.2, V10).
CCUSAGE_REASON = "R-014\u2013R-017 ccusage tabs (owner accepted 2026-11-10)"
FALLBACK_REASON = "No 1.3.3 counterpart; owner decision pending"
SUPPLEMENTARY_REASON = "R-010/R-011 supplementary details"
ONLY_SECTION_TITLE = "2.0 only \u2014 not in 1.3.3, not scored for parity"

PNG_PLACEHOLDER = b"\x89PNG\r\n\x1a\n"


class ParityReportTests(unittest.TestCase):
    # --- fixture helpers (pattern of the owner-preservation test below) ---

    def setUp(self):
        (ROOT / "artifacts/verification/test-fixtures").mkdir(parents=True, exist_ok=True)

    def make_fixture(self, temporary, wpf_names, legacy_names, wpf_pngs=(), legacy_pngs=()):
        """Temporary repo holding a copy of the script and synthetic manifests.

        Every manifest screen also gets a placeholder PNG on both sides, plus any
        extra PNGs requested, so an implementation that checks file existence (the
        tray pairs and the -agy- counterpart rule) sees consistent evidence."""
        fixture = Path(temporary).resolve()
        self.assertTrue(fixture.is_relative_to(ROOT))
        (fixture / "scripts").mkdir()
        (fixture / "Docs").mkdir()
        names = {"wpf": sorted(set(wpf_names)), "legacy": sorted(set(legacy_names))}
        pngs = {"wpf": sorted(set(wpf_names) | set(wpf_pngs)), "legacy": sorted(set(legacy_names) | set(legacy_pngs))}
        for renderer in ("wpf", "legacy"):
            directory = fixture / "artifacts/verification" / renderer
            directory.mkdir(parents=True)
            (directory / "manifest.json").write_text(json.dumps(names[renderer]), encoding="utf-8")
            for name in pngs[renderer]:
                (directory / f"{name}.png").write_bytes(PNG_PLACEHOLDER)
        shutil.copyfile(ROOT / "scripts/build_parity_report.py", fixture / "scripts/build_parity_report.py")
        return fixture

    def run_generator(self, fixture):
        return subprocess.run(
            [sys.executable, str(fixture / "scripts/build_parity_report.py")],
            capture_output=True, text=True, cwd=fixture)

    def outputs(self, fixture):
        index = (fixture / "artifacts/verification/index.html").read_text(encoding="utf-8")
        acceptance = (fixture / "Docs/PARITY_ACCEPTANCE.md").read_text(encoding="utf-8")
        return index, acceptance

    def reported_pair_count(self, result, acceptance):
        """Exact pair count from stdout and from the acceptance evidence line."""
        generated = re.search(r"Generated (\d+) visual pair", result.stdout)
        self.assertIsNotNone(generated, f"no 'Generated N visual pairs' in stdout: {result.stdout!r}")
        evidence = re.search(r"Evidence: (\d+) matched", acceptance)
        self.assertIsNotNone(evidence, f"no 'Evidence: N matched' line in acceptance: {acceptance!r}")
        self.assertEqual(generated.group(1), evidence.group(1), "stdout and acceptance pair counts differ")
        return int(generated.group(1))

    @staticmethod
    def markdown_rows(section_text):
        rows = []
        for line in section_text.splitlines():
            if not line.startswith("|"):
                continue
            cells = tuple(cell.strip() for cell in line.strip("|").split("|"))
            if all(cell and set(cell) <= {"-", ":"} for cell in cells):
                continue  # header separator row
            rows.append(cells)
        return rows

    def section_rows(self, acceptance, heading, required=True):
        match = re.search(rf"^## .*{re.escape(heading)}.*$", acceptance, re.MULTILINE)
        if match is None:
            if not required:
                return []
            self.fail(f"missing section '## {heading}' in acceptance document")
        body = acceptance[match.end():]
        stop = re.search(r"^## ", body, re.MULTILINE)
        if stop:
            body = body[:stop.start()]
        return self.markdown_rows(body)

    def visual_rows(self, acceptance):
        return self.section_rows(acceptance, "Visual screens")

    def only_rows(self, acceptance):
        # The section may be absent when no screen is 2.0-only; absence must not be
        # reported as a listing violation, only as a missing entry by assert_only_row.
        rows = self.section_rows(acceptance, "2.0-only screens", required=False)
        return [row for row in rows if row[0] != "Screen"]

    def assert_pair_row(self, acceptance, name):
        for row in self.visual_rows(acceptance):
            if row[0] == name:
                self.assertEqual(row[1], "Pending", f"pair {name} must start unscored")
                self.assertIn("no owner score", row[2].lower(), f"pair {name} note changed")
                return
        self.fail(f"{name!r} is not a pair row of the Visual screens table")

    def assert_not_in_visual_table(self, acceptance, name):
        first_cells = [row[0] for row in self.visual_rows(acceptance)]
        self.assertNotIn(name, first_cells, f"{name!r} must not be listed as a pair")

    def assert_only_row(self, acceptance, name, reason):
        matches = [row for row in self.only_rows(acceptance) if row[0] == name]
        self.assertEqual(len(matches), 1, f"expected exactly one 2.0-only row for {name!r}, got {matches}")
        self.assertEqual(matches[0][1], reason, f"wrong reason for {name!r}")

    def assert_not_in_only_table(self, acceptance, name):
        first_cells = [row[0] for row in self.only_rows(acceptance)]
        self.assertNotIn(name, first_cells, f"{name!r} must not be listed as 2.0-only")

    # --- V10: -agy- WPF screens pair with -gemini- legacy screens, both themes ---

    def test_agy_counterparts_pair_with_gemini_screens_in_both_themes(self):
        wpf = ["dashboard-agy-light", "dashboard-agy-dark", "connect-agy-light", "connect-agy-dark"]
        legacy = ["dashboard-gemini-light", "dashboard-gemini-dark",
                  "connect-gemini-light", "connect-gemini-dark"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 4)
            for name in ("dashboard-agy-light ↔ dashboard-gemini-light",
                         "dashboard-agy-dark ↔ dashboard-gemini-dark",
                         "connect-agy-light ↔ connect-gemini-light",
                         "connect-agy-dark ↔ connect-gemini-dark"):
                self.assertIn(name, index, "counterpart label missing from index.html")
                self.assert_pair_row(acceptance, name)
            for wpf_name, legacy_name in zip(wpf, legacy):
                self.assertIn(f"wpf/{wpf_name}.png", index)
                self.assertIn(f"legacy/{legacy_name}.png", index)
                self.assertNotIn(wpf_name, [row[0] for row in self.only_rows(acceptance)],
                                 "a paired screen must not be listed as 2.0-only")

    # --- V10: identical-name pairing keeps working (regression of base behaviour) ---

    def test_identical_name_screens_still_pair(self):
        names = ["dashboard-claude-light", "dashboard-claude-dark"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, names, names)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 2)
            for name in names:
                self.assert_pair_row(acceptance, name)
                self.assertIn(f"legacy/{name}.png", index)
                self.assertIn(f"wpf/{name}.png", index)
                self.assertNotIn(f"{name} ↔", acceptance, "identical names must keep the plain label")
                self.assertNotIn(name, [row[0] for row in self.only_rows(acceptance)])

    # --- V10: a tray icon pairs only when the PNG exists on both sides ---

    def test_tray_icon_pairs_only_when_both_pngs_exist(self):
        names = ["dashboard-claude-light"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(
                temporary, names, names,
                wpf_pngs=["tray-light", "tray-dark"], legacy_pngs=["tray-light"])
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 2)
            self.assert_pair_row(acceptance, "tray-light")
            self.assertIn("legacy/tray-light.png", index)
            self.assertIn("wpf/tray-light.png", index)
            self.assert_not_in_visual_table(acceptance, "tray-dark")
            self.assert_not_in_only_table(acceptance, "tray-dark")

    # --- V3.1 / V10: every 2.0-only reason class is listed and not counted as a pair ---

    def test_two_only_screens_are_listed_with_reasons_and_never_paired(self):
        wpf = ["dashboard-claude-light",
               "supplementary-report-light",
               "dashboard-opencode-light", "dashboard-overview-dark", "dashboard-all-agents-light",
               "custom-panel-dark"]
        legacy = ["dashboard-claude-light"]
        expected = {
            "supplementary-report-light": SUPPLEMENTARY_REASON,
            "dashboard-opencode-light": CCUSAGE_REASON,
            "dashboard-overview-dark": CCUSAGE_REASON,
            "dashboard-all-agents-light": CCUSAGE_REASON,
            "custom-panel-dark": FALLBACK_REASON,
        }
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            self.assert_pair_row(acceptance, "dashboard-claude-light")
            self.assertIn(ONLY_SECTION_TITLE, index, "2.0-only section missing from index.html")
            for name, reason in expected.items():
                self.assert_only_row(acceptance, name, reason)
                self.assertIn(f"wpf/{name}.png", index, f"2.0-only image missing for {name}")
                self.assert_not_in_visual_table(acceptance, name)

    # --- V3.2 / V10: a legacy screen without a WPF counterpart is untested evidence ---

    def test_legacy_only_screen_is_listed_as_no_2_0_render(self):
        wpf = ["dashboard-claude-light"]
        legacy = ["dashboard-claude-light", "dashboard-gemini-dark"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            _, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            matches = [row for row in self.visual_rows(acceptance) if row[0] == "dashboard-gemini-dark"]
            self.assertEqual(len(matches), 1, "legacy-only screen must appear once in the visual table")
            self.assertEqual(matches[0][1], "Pending", "untested evidence must keep the score Pending")
            self.assertIn("No 2.0 render", matches[0][2])
            self.assert_not_in_only_table(acceptance, "dashboard-gemini-dark")

    # --- V10: identical matches win; a legacy screen is consumed by at most one pair ---

    def test_identical_match_is_taken_before_counterpart_and_legacy_screen_used_once(self):
        wpf = ["dashboard-gemini-light", "dashboard-agy-light"]
        legacy = ["dashboard-gemini-light"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            self.assert_pair_row(acceptance, "dashboard-gemini-light")
            label = "dashboard-agy-light ↔ dashboard-gemini-light"
            self.assertNotIn(label, index, "the consumed legacy screen paired twice in index.html")
            self.assertNotIn(label, acceptance, "the consumed legacy screen paired twice in acceptance")
            # The unmatched agy screen is 2.0-only with the fallback reason, not a pair.
            self.assert_only_row(acceptance, "dashboard-agy-light", FALLBACK_REASON)
            self.assert_not_in_visual_table(acceptance, "dashboard-agy-light")

    # --- V4/V9: the owner-score preservation guard keeps working unchanged ---

    def test_regeneration_preserves_owner_results_and_notes(self):
        scratch = ROOT / "artifacts/verification/test-fixtures"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            fixture = Path(temporary).resolve()
            self.assertTrue(fixture.is_relative_to(ROOT))
            (fixture / "scripts").mkdir()
            (fixture / "Docs").mkdir()
            for renderer in ("wpf", "legacy"):
                directory = fixture / "artifacts/verification" / renderer
                directory.mkdir(parents=True)
                (directory / "manifest.json").write_text('["fixture-light"]', encoding="utf-8")
            script = fixture / "scripts/build_parity_report.py"
            shutil.copyfile(ROOT / "scripts/build_parity_report.py", script)
            first = subprocess.run([sys.executable, str(script)], capture_output=True, text=True, cwd=fixture)
            self.assertEqual(0, first.returncode, first.stderr)
            acceptance = fixture / "Docs/PARITY_ACCEPTANCE.md"
            owner_text = acceptance.read_text(encoding="utf-8").replace("| Pending |", "| Pass |", 1)
            owner_text += "\nOwner exception: approved version label.\n"
            acceptance.write_text(owner_text, encoding="utf-8")
            second = subprocess.run([sys.executable, str(script)], capture_output=True, text=True, cwd=fixture)
            self.assertNotEqual(0, second.returncode)
            self.assertIn("preserve owner scores", second.stderr)
            self.assertEqual(owner_text, acceptance.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
