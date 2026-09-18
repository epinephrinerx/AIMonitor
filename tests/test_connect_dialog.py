"""Cancel really cancels: no button in the connect dialog writes before Save.

Reported by review on 2026-09-18. `Clear` deleted the stored key the instant
it was pressed, while the dialog still showed Save and Cancel. Cancel took
back nothing, and because a blank field means "leave the stored key alone",
Save could not put it back either - so both buttons destroyed a DPAPI-sealed
key that cannot be recovered by retyping it from memory.
"""

from __future__ import annotations

import os
import unittest

# A scope of our own, so the tests never read or write the real user's keys.
SCOPE = "ConnectDialogTests"
os.environ["AI_USAGE_MONITOR_SETTINGS_SCOPE"] = SCOPE
# No display is needed to exercise dialog logic, and the CI/build machine may
# not have one.
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor import secrets  # noqa: E402
from ai_usage_monitor.connect_dialog import ConnectDialog  # noqa: E402
from ai_usage_monitor.detection import CONNECTED, Detection  # noqa: E402
from ai_usage_monitor.providers.base import Provider  # noqa: E402
from ai_usage_monitor.settings import ORG, Settings  # noqa: E402
from ai_usage_monitor.theme import resolve  # noqa: E402

KEY = "sk-admin-existing-key-value"
PROVIDER_ID = "testprovider"


class _Fake(Provider):
    id = PROVIDER_ID
    display_name = "Test Service"
    needs_key = True
    key_label = "API key"
    key_placeholder = "sk-…"
    extra_label = "Budget"
    extra_placeholder = "e.g. 50"
    setup_hint = "A test service."
    tagline = "testing"

    def configure(self, key: str, extra: str = "") -> None:
        return

    def is_configured(self) -> bool:
        return True


def _app() -> QApplication:
    return QApplication.instance() or QApplication([])


@unittest.skipUnless(secrets.available(), "sealed key storage needs Windows DPAPI")
class ClearAndCancelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = _app()
        cls.theme = resolve("light")

    def setUp(self):
        self.settings = Settings()
        self.settings.set_provider_key(PROVIDER_ID, KEY)
        self.assertEqual(self.settings.provider_key(PROVIDER_ID), KEY)
        self.addCleanup(self._wipe)

    def _wipe(self):
        QSettings(ORG, SCOPE).clear()

    def _dialog(self) -> ConnectDialog:
        detection = Detection(
            provider_id=PROVIDER_ID, state=CONNECTED, source_label="Saved here"
        )
        dialog = ConnectDialog(_Fake(), detection, self.settings, self.theme)
        self.addCleanup(dialog.deleteLater)
        return dialog

    def test_clear_then_cancel_keeps_the_key(self):
        """The reported bug. Cancel must put back what Clear only proposed."""
        dialog = self._dialog()
        dialog._clear_key()
        self.assertEqual(
            self.settings.provider_key(PROVIDER_ID),
            KEY,
            "Clear wrote to settings before Save",
        )
        dialog.reject()
        self.assertEqual(self.settings.provider_key(PROVIDER_ID), KEY)

    def test_clear_then_save_removes_the_key(self):
        dialog = self._dialog()
        dialog._clear_key()
        dialog._save()
        self.assertEqual(self.settings.provider_key(PROVIDER_ID), "")

    def test_clear_then_typing_a_new_key_replaces_rather_than_clears(self):
        """The last thing you did is what you meant."""
        dialog = self._dialog()
        dialog._clear_key()
        dialog._key_field.setText("sk-admin-replacement")
        dialog._save()
        self.assertEqual(
            self.settings.provider_key(PROVIDER_ID), "sk-admin-replacement"
        )

    def test_saving_an_untouched_blank_field_leaves_the_key_alone(self):
        """Blank means "keep what is stored" - the field never shows the key."""
        dialog = self._dialog()
        dialog._save()
        self.assertEqual(self.settings.provider_key(PROVIDER_ID), KEY)

    def test_cancel_does_not_write_the_companion_field_either(self):
        self.settings.set_provider_extra(PROVIDER_ID, "50")
        dialog = self._dialog()
        dialog._extra_field.setText("999")
        dialog.reject()
        self.assertEqual(self.settings.provider_extra(PROVIDER_ID), "50")

    def test_save_does_write_the_companion_field(self):
        self.settings.set_provider_extra(PROVIDER_ID, "50")
        dialog = self._dialog()
        dialog._extra_field.setText("999")
        dialog._save()
        self.assertEqual(self.settings.provider_extra(PROVIDER_ID), "999")


class NoEarlyWriteTests(unittest.TestCase):
    """The rule behind the fix, checked in the source itself.

    Only `_save` may write a credential. A future edit that reintroduces an
    immediate write from a button handler fails here even if it never trips
    the behavioural tests above.
    """

    def test_only_save_writes_a_provider_key(self):
        import ast
        import pathlib

        source = (
            pathlib.Path(__file__).resolve().parent.parent
            / "ai_usage_monitor"
            / "connect_dialog.py"
        ).read_text(encoding="utf-8")
        tree = ast.parse(source)

        writers = set()
        for node in ast.walk(tree):
            if not isinstance(node, ast.FunctionDef):
                continue
            for call in ast.walk(node):
                if (
                    isinstance(call, ast.Call)
                    and isinstance(call.func, ast.Attribute)
                    and call.func.attr
                    in ("set_provider_key", "set_provider_extra")
                ):
                    writers.add(node.name)
        self.assertEqual(
            writers,
            {"_save"},
            "a credential is written outside _save, so Cancel no longer cancels",
        )


if __name__ == "__main__":
    unittest.main()
