"""Tests for standalone offline Codex handshake verification probe."""
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
PROBE_SCRIPT = ROOT / "scripts/verify_codex_handshake.py"


class VerifyCodexHandshakeTests(unittest.TestCase):
    def test_missing_argument_fails(self):
        proc = subprocess.run(
            [sys.executable, str(PROBE_SCRIPT)],
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("usage:", proc.stderr.lower())

    def test_nonexistent_executable_fails(self):
        proc = subprocess.run(
            [sys.executable, str(PROBE_SCRIPT), "--codex-executable", "nonexistent_executable_binary.exe"],
            capture_output=True,
            text=True,
        )
        self.assertEqual(proc.returncode, 1)
        self.assertIn("Codex executable not found", proc.stderr)


if __name__ == "__main__":
    unittest.main()
