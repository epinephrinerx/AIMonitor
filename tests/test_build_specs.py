"""The two PyInstaller specs must not drift apart.

`AIUsageMonitor.spec` builds the portable onefile and `AIUsageMonitor-dir.spec`
the tree the installer wraps. They ship the same application, so they have to
leave out the same things - and they stopped doing that: the stray-OpenSSL
filter went into one of them, the portable dropped to 46.7 MB, and the
installer went on carrying 8.2 MB of a stranger's OpenSSL while the dir spec's
docstring said "same excludes".

Same failure as `CLAUDE.md` and `AGENTS.md`, same fix: one source, two thin
readers, and a test that says so.
"""

import ast
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPECS = ("AIUsageMonitor.spec", "AIUsageMonitor-dir.spec")
SHARED = ("EXCLUDED_QT", "EXCLUDED_STDLIB", "without_stray_openssl")


def _tree(name):
    return ast.parse((ROOT / name).read_text(encoding="utf-8"))


class SpecsShareOneSourceTests(unittest.TestCase):
    def test_both_specs_exist(self):
        for name in SPECS:
            with self.subTest(spec=name):
                self.assertTrue((ROOT / name).exists())

    def test_neither_spec_keeps_its_own_lists(self):
        """A second copy is how they came apart the first time."""
        for name in SPECS:
            with self.subTest(spec=name):
                assigned = {
                    target.id
                    for node in ast.walk(_tree(name))
                    if isinstance(node, ast.Assign)
                    for target in node.targets
                    if isinstance(target, ast.Name)
                }
                for shared in SHARED:
                    self.assertNotIn(
                        shared,
                        assigned,
                        f"{name} defines its own {shared}; it must import it",
                    )

    def test_both_specs_import_the_shared_module(self):
        for name in SPECS:
            with self.subTest(spec=name):
                imported = {
                    alias.name
                    for node in ast.walk(_tree(name))
                    if isinstance(node, ast.ImportFrom)
                    and node.module == "build_excludes"
                    for alias in node.names
                }
                self.assertTrue(
                    imported, f"{name} does not read build_excludes at all"
                )
                for shared in SHARED:
                    self.assertIn(shared, imported, f"{name} is missing {shared}")

    def test_both_specs_filter_the_stray_openssl(self):
        """Calling the helper is the point; importing it is not enough.

        The dir spec imported nothing and filtered nothing, and the only
        visible symptom was an installer 8 MB heavier than it should be -
        which nobody is going to notice by reading a build log.
        """
        for name in SPECS:
            with self.subTest(spec=name):
                called = [
                    node.func.id
                    for node in ast.walk(_tree(name))
                    if isinstance(node, ast.Call)
                    and isinstance(node.func, ast.Name)
                ]
                self.assertIn(
                    "without_stray_openssl",
                    called,
                    f"{name} imports the filter but never applies it",
                )


class SharedModuleTests(unittest.TestCase):
    def test_the_filter_drops_the_pair_and_keeps_python_own(self):
        import build_excludes

        binaries = [
            ("libcrypto-3-x64.dll", r"C:\somewhere\php\libcrypto-3-x64.dll", "BINARY"),
            ("libssl-3-x64.dll", r"C:\somewhere\php\libssl-3-x64.dll", "BINARY"),
            ("libcrypto-3.dll", r"C:\python\DLLs\libcrypto-3.dll", "BINARY"),
            ("libssl-3.dll", r"C:\python\DLLs\libssl-3.dll", "BINARY"),
            ("python314.dll", r"C:\python\python314.dll", "BINARY"),
        ]
        kept = [entry[0] for entry in build_excludes.without_stray_openssl(binaries)]
        self.assertEqual(kept, ["libcrypto-3.dll", "libssl-3.dll", "python314.dll"])

    def test_it_matches_whatever_case_the_path_arrives_in(self):
        import build_excludes

        binaries = [("LIBCRYPTO-3-X64.DLL", r"C:\x\LIBCRYPTO-3-X64.DLL", "BINARY")]
        self.assertEqual(build_excludes.without_stray_openssl(binaries), [])

    def test_python_own_openssl_is_never_in_the_drop_list(self):
        """Dropping that pair would take HTTPS out of the whole app."""
        import build_excludes

        self.assertNotIn("libcrypto-3.dll", build_excludes.STRAY_OPENSSL)
        self.assertNotIn("libssl-3.dll", build_excludes.STRAY_OPENSSL)


if __name__ == "__main__":
    unittest.main()
