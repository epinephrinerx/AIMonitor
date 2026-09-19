"""The version is one number, and the update check never offers a downgrade."""

from __future__ import annotations

import io
import json
import re
import unittest
import urllib.error
from pathlib import Path
from unittest import mock

from ai_usage_monitor import version

ROOT = Path(__file__).resolve().parent.parent


class SingleSourceTests(unittest.TestCase):
    """The About box, the exe's file properties and the installer must agree.

    Three files carry the number and only one of them is importable, so this
    is the thing that notices when a release bumps two of the three.
    """

    def test_file_version_resource_matches(self):
        text = (ROOT / "version_info.txt").read_text(encoding="utf-8")
        expected = tuple(int(p) for p in version.VERSION.split(".")) + (0,)
        for field in ("filevers", "prodvers"):
            match = re.search(rf"{field}=\((\d+), (\d+), (\d+), (\d+)\)", text)
            self.assertIsNotNone(match, f"{field} missing from version_info.txt")
            self.assertEqual(tuple(int(g) for g in match.groups()), expected)
        self.assertIn(f"'{version.VERSION}.0'", text)

    def test_installer_version_matches(self):
        text = (ROOT / "installer" / "AIUsageMonitor.iss").read_text(encoding="utf-8")
        match = re.search(r'#define AppVersion\s+"([^"]+)"', text)
        self.assertIsNotNone(match)
        self.assertEqual(match.group(1), version.VERSION)

    def test_the_version_sent_to_codex_is_imported_not_typed(self):
        """The one version string that leaves the machine.

        It said 1.1.0 while the app shipped 1.3.0, so a Codex-side log could
        not identify the build a report came from. A literal here cannot be
        caught by comparing files, so the rule is that there is no literal.
        """
        import ast

        source = (ROOT / "ai_usage_monitor" / "codex_usage.py").read_text(
            encoding="utf-8"
        )
        for node in ast.walk(ast.parse(source)):
            if not isinstance(node, ast.Dict):
                continue
            for key, value in zip(node.keys, node.values):
                if isinstance(key, ast.Constant) and key.value == "version":
                    self.assertIsInstance(
                        value,
                        ast.Name,
                        f"line {node.lineno}: clientInfo carries a literal "
                        "version; import __version__ instead",
                    )

    def test_no_file_advertises_a_version_that_is_not_this_one(self):
        """Documentation drifts silently; this is what notices.

        The readme told people to run an installer two releases old, and the
        build script's own header named one older still. The macOS pair are in
        the list for the same reason: they name the .dmg they produce, and a
        stale number there is a download link for a file that is not built.
        """
        pattern = re.compile(r"AIUsageMonitor-(?:Setup-)?(\d+\.\d+\.\d+)")
        for name in (
            "README.md",
            "build_ai_installer.ps1",
            "build_mac.sh",
            "AIUsageMonitor-mac.spec",
        ):
            text = (ROOT / name).read_text(encoding="utf-8")
            for found in pattern.findall(text):
                self.assertEqual(
                    found,
                    version.VERSION,
                    f"{name} names version {found}, but this is "
                    f"{version.VERSION}",
                )


class CompareTests(unittest.TestCase):
    def test_parses_tags_with_and_without_the_v(self):
        self.assertEqual(version.parse("v1.2.7"), (1, 2, 7))
        self.assertEqual(version.parse("1.2.7"), (1, 2, 7))
        self.assertEqual(version.parse("  v2.0"), (2, 0))

    def test_unparseable_tag_is_never_newer(self):
        """A tag we cannot read must not be announced as an update."""
        self.assertEqual(version.parse("nightly"), ())
        self.assertFalse(version.is_newer("nightly", "1.2.7"))
        self.assertFalse(version.is_newer("", "1.2.7"))

    def test_shorter_and_longer_tags_compare_by_value(self):
        self.assertFalse(version.is_newer("v1.3", "1.3.0"))
        self.assertFalse(version.is_newer("v1.3.0", "1.3"))
        self.assertTrue(version.is_newer("v1.3.1", "1.3"))
        self.assertTrue(version.is_newer("v1.10.0", "1.9.9"))

    def test_older_release_is_not_an_update(self):
        self.assertFalse(version.is_newer("v1.2.6", "1.3.0"))
        self.assertTrue(version.is_newer("v1.4.0", "1.3.0"))


class _Response:
    def __init__(self, payload):
        self._body = json.dumps(payload).encode()

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _not_found() -> urllib.error.HTTPError:
    """A closed 404, so the test does not leak a file object into the warning log."""
    error = urllib.error.HTTPError(
        version.LATEST_API, 404, "Not Found", {}, io.BytesIO(b"")
    )
    error.close()
    return error


class CheckTests(unittest.TestCase):
    def test_reports_a_newer_release(self):
        payload = {
            "tag_name": "v9.9.9",
            "name": "Release 9.9.9",
            "html_url": "https://example.invalid/r/9.9.9",
            "published_at": "2026-09-14T10:00:00Z",
        }
        with mock.patch("urllib.request.urlopen", return_value=_Response(payload)):
            release = version.check_latest()
        self.assertEqual(release.tag, "v9.9.9")
        self.assertEqual(release.published, "2026-09-14")
        self.assertTrue(release.newer)

    def test_same_version_is_not_an_update(self):
        payload = {"tag_name": f"v{version.VERSION}"}
        with mock.patch("urllib.request.urlopen", return_value=_Response(payload)):
            release = version.check_latest()
        self.assertFalse(release.newer)
        # No html_url in the payload: fall back to the releases page rather
        # than handing the dialog an empty link.
        self.assertEqual(release.url, version.RELEASES_URL)

    def test_falls_back_to_the_highest_version_tag(self):
        """A repo that tags releases without publishing them still answers."""
        tags = [{"name": "v1.2.6"}, {"name": "v9.9.9"}, {"name": "nightly"}]

        def urlopen(request, **kwargs):
            if request.full_url == version.LATEST_API:
                raise _not_found()
            return _Response(tags)

        with mock.patch("urllib.request.urlopen", side_effect=urlopen):
            release = version.check_latest()
        self.assertEqual(release.tag, "v9.9.9")
        self.assertTrue(release.newer)
        self.assertIn("releases/tag/v9.9.9", release.url)

    def test_tagless_repository_reports_no_releases(self):
        def urlopen(request, **kwargs):
            if request.full_url == version.LATEST_API:
                raise _not_found()
            return _Response([])

        with mock.patch("urllib.request.urlopen", side_effect=urlopen):
            with self.assertRaises(version.UpdateCheckError) as caught:
                version.check_latest()
        self.assertIn("no published releases", str(caught.exception))

    def test_private_repository_says_so_instead_of_up_to_date(self):
        """Both endpoints 404: never report 'up to date' from a blind check."""
        error = urllib.error.HTTPError(
            version.LATEST_API, 404, "Not Found", {}, io.BytesIO(b"")
        )
        self.addCleanup(error.close)
        with mock.patch("urllib.request.urlopen", side_effect=error):
            with self.assertRaises(version.UpdateCheckError) as caught:
                version.check_latest()
        self.assertIn("anonymous request", str(caught.exception))

    def test_rate_limiting_is_reported_as_itself(self):
        error = urllib.error.HTTPError(
            version.LATEST_API, 403, "rate limited", {}, io.BytesIO(b"")
        )
        self.addCleanup(error.close)
        with mock.patch("urllib.request.urlopen", side_effect=error):
            with self.assertRaises(version.UpdateCheckError) as caught:
                version.check_latest()
        self.assertIn("rate-limiting", str(caught.exception))

    def test_offline_is_reported_as_a_check_failure(self):
        error = urllib.error.URLError("getaddrinfo failed")
        with mock.patch("urllib.request.urlopen", side_effect=error):
            with self.assertRaises(version.UpdateCheckError):
                version.check_latest()

    def test_untagged_release_is_rejected(self):
        with mock.patch(
            "urllib.request.urlopen", return_value=_Response({"name": "nightly"})
        ):
            with self.assertRaises(version.UpdateCheckError):
                version.check_latest()


if __name__ == "__main__":
    unittest.main()
