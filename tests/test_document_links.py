"""Links between bundled documents work inside the app, and go nowhere else.

The readme points at LICENSE and the third-party notices. `_on_anchor` treated
everything that was not an http URL as an in-page anchor, so those links were
handed to `scrollToAnchor("LICENSE")` - no such anchor exists, so clicking did
nothing at all. The documents were still reachable from the About menu, but
inside a window whose whole job is in-app documentation the links were dead.

Resolution is an allow-list, not "open whatever the document names". These
files are read from disk beside the executable, and a Markdown document should
not be able to point the app at an arbitrary path.
"""

from __future__ import annotations

import os
import unittest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QUrl  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor.readme_dialog import (  # noqa: E402
    SIBLING_DOCUMENTS,
    LicenceDialog,
    NoticesDialog,
    ReadmeDialog,
)
from ai_usage_monitor.theme import resolve  # noqa: E402


class AnchorRoutingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])
        cls.theme = resolve("light")

    def setUp(self):
        self.dialog = ReadmeDialog(self.theme)
        self.addCleanup(self.dialog.deleteLater)
        self.asked: list[str] = []
        self.dialog.sibling_requested.connect(self.asked.append)

    def test_a_link_to_the_licence_asks_for_the_licence(self):
        """The reported dead link."""
        self.dialog._on_anchor(QUrl("LICENSE"))
        self.assertEqual(self.asked, ["licence"])

    def test_the_installer_spelling_of_the_licence_works_too(self):
        """Inno renames it LICENSE.txt so Windows will open it."""
        self.dialog._on_anchor(QUrl("LICENSE.txt"))
        self.assertEqual(self.asked, ["licence"])

    def test_a_link_to_the_notices_asks_for_the_notices(self):
        self.dialog._on_anchor(QUrl("THIRD-PARTY-NOTICES.md"))
        self.assertEqual(self.asked, ["notices"])

    def test_a_leading_dot_slash_is_tolerated(self):
        self.dialog._on_anchor(QUrl("./LICENSE"))
        self.assertEqual(self.asked, ["licence"])

    def test_an_in_page_anchor_is_still_an_in_page_anchor(self):
        self.dialog._on_anchor(QUrl("#known-limits"))
        self.assertEqual(self.asked, [], "a section link opened a document")

    def test_an_http_link_still_goes_to_the_browser(self):
        from unittest.mock import patch

        with patch(
            "ai_usage_monitor.readme_dialog.QDesktopServices.openUrl"
        ) as opened:
            self.dialog._on_anchor(QUrl("https://example.invalid/x"))
        opened.assert_called_once()
        self.assertEqual(self.asked, [])

    def test_an_arbitrary_path_is_not_opened(self):
        """The allow-list is the point: a document cannot name any file."""
        for path in (
            "C:/Windows/System32/drivers/etc/hosts",
            "../../../secrets.txt",
            "settings.py",
            "ai_usage_monitor/secrets.py",
        ):
            with self.subTest(path=path):
                self.asked.clear()
                self.dialog._on_anchor(QUrl(path))
                self.assertEqual(self.asked, [], f"{path} was treated as a document")


class AllowListTests(unittest.TestCase):
    def test_every_target_has_a_dialog_that_can_show_it(self):
        self.assertEqual(
            set(SIBLING_DOCUMENTS.values()), {"readme", "licence", "notices"}
        )

    def test_the_window_opens_a_dialog_for_every_target(self):
        """Behaviour, not a dict that happens to have the right keys.

        The first version searched the AST for any dictionary containing
        those three strings, which a dead mapping would have satisfied. This
        calls `_open_document` for each target and checks which dialog class
        it actually built.
        """
        from unittest.mock import patch

        from ai_usage_monitor import main_window as module

        built: list[str] = []
        emit_on_exec: list[str] = []

        class _Signal:
            """A fake that actually keeps its slots, so emitting works.

            The first version discarded them, which meant the click-through
            path could never run and the test proved only that the first
            builder was chosen.
            """

            def __init__(self):
                self.slots = []

            def connect(self, slot):
                self.slots.append(slot)

            def emit(self, value):
                for slot in self.slots:
                    # Qt drops arguments a slot does not accept, which is how
                    # `sibling_requested.connect(dialog.accept)` works at all.
                    try:
                        slot(value)
                    except TypeError:
                        slot()

        class _Stub:
            def __init__(self):
                self.sibling_requested = _Signal()
                self.accepted = False

            def exec(self):
                # Standing in for the reader clicking a link in the document.
                if emit_on_exec:
                    self.sibling_requested.emit(emit_on_exec.pop(0))
                return 0

            def accept(self):
                self.accepted = True

        class _Fake:
            def __init__(self, name):
                self.name = name
                self.last = None

            def __call__(self, theme, parent):
                built.append(self.name)
                self.last = _Stub()
                return self.last

        class _Window:
            """Just enough of the window for `_open_document` to run.

            It carries the real method so the recursive hop to the sibling
            document runs the production code rather than a stand-in.
            """

            theme = resolve("light")
            _open_document = module.MainWindow._open_document

        window = _Window()

        readme, licence, notices = _Fake("readme"), _Fake("licence"), _Fake("notices")
        with (
            patch.object(module, "ReadmeDialog", readme),
            patch.object(module, "LicenceDialog", licence),
            patch.object(module, "NoticesDialog", notices),
        ):
            for target in sorted(set(SIBLING_DOCUMENTS.values())):
                module.MainWindow._open_document(window, target)

        self.assertEqual(built, sorted(set(SIBLING_DOCUMENTS.values())))

        # And the navigation the reader actually performs: open the readme,
        # click the licence link, and the readme should close as the licence
        # opens. Removing either `connect` or the recursive call fails here.
        built.clear()
        emit_on_exec.append("licence")
        with (
            patch.object(module, "ReadmeDialog", readme),
            patch.object(module, "LicenceDialog", licence),
            patch.object(module, "NoticesDialog", notices),
        ):
            module.MainWindow._open_document(window, "readme")

        self.assertEqual(built, ["readme", "licence"])
        self.assertTrue(
            readme.last.accepted, "the document being read was left open"
        )

    def test_closing_without_clicking_opens_nothing_else(self):
        from unittest.mock import patch

        from ai_usage_monitor import main_window as module

        built: list[str] = []

        class _Stub:
            def __init__(self):
                self.sibling_requested = type(
                    "S", (), {"connect": lambda self, slot: None}
                )()

            def exec(self):
                return 0

            def accept(self):
                return None

        def _build(theme, parent):
            built.append("opened")
            return _Stub()

        class _Window:
            theme = resolve("light")
            _open_document = module.MainWindow._open_document

        with patch.object(module, "ReadmeDialog", _build):
            module.MainWindow._open_document(_Window(), "readme")
        self.assertEqual(built, ["opened"])

    def test_an_unknown_target_opens_nothing(self):
        from unittest.mock import patch

        from ai_usage_monitor import main_window as module

        class _Window:
            theme = resolve("light")
            _open_document = module.MainWindow._open_document

        window = _Window()
        with patch.object(module, "ReadmeDialog") as readme:
            module.MainWindow._open_document(window, "not-a-document")
        readme.assert_not_called()

    def test_the_readme_really_does_link_to_them(self):
        """If these links go, the routing above is dead code."""
        import pathlib

        readme = (
            pathlib.Path(__file__).resolve().parent.parent / "README.md"
        ).read_text(encoding="utf-8")
        self.assertIn("](LICENSE)", readme)
        self.assertIn("](THIRD-PARTY-NOTICES.md)", readme)


class EveryDialogCarriesTheSignalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])
        cls.theme = resolve("light")

    def test_all_three_document_windows_can_request_a_sibling(self):
        for builder in (ReadmeDialog, LicenceDialog, NoticesDialog):
            with self.subTest(builder=builder.__name__):
                dialog = builder(self.theme)
                self.addCleanup(dialog.deleteLater)
                self.assertTrue(hasattr(dialog, "sibling_requested"))


if __name__ == "__main__":
    unittest.main()
