"""Regression: an owner-filled acceptance document must survive report regeneration."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ParityReportTests(unittest.TestCase):
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
