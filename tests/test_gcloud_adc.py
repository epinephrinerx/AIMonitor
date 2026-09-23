"""A gcloud user login is explained, not silently skipped.

README advertised "gcloud ADC" as a Gemini credential source, but
`_gcloud_adc()` only returned something when the file held a service account.
`gcloud auth application-default login` writes an `authorized_user`, so a user
who followed the documentation got "Not connected" and no reason why.

This app cannot use that credential: reading usage with it would mean
refreshing another tool's OAuth token, which the credential policy forbids.
The honest outcome is Limited with the reason attached - the state every other
present-but-unusable login already gets - and a README that says what is
actually supported.
"""

from __future__ import annotations

import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from ai_usage_monitor.detection import CONNECTED, MISSING, PARTIAL, resolve
from ai_usage_monitor.providers import sources

ROOT = Path(__file__).resolve().parent.parent

SERVICE_ACCOUNT = {
    "type": "service_account",
    "client_email": "robot@example.iam.gserviceaccount.com",
    "project_id": "my-project",
    # Not decoration: `GeminiProvider._load_key_file` refuses a key without
    # it, so a fixture that leaves it out is not a usable service account and
    # a test using one proves the detector agrees with nothing.
    "private_key": "-----BEGIN PRIVATE KEY----- not-a-real-key",
}
INCOMPLETE_SERVICE_ACCOUNT = {
    "type": "service_account",
    "client_email": "robot@example.iam.gserviceaccount.com",
    "project_id": "my-project",
}
USER_LOGIN = {
    "type": "authorized_user",
    "account": "someone@example.invalid",
    "client_id": "123.apps.googleusercontent.com",
    "refresh_token": "not-read",
}


class GcloudAdcTests(unittest.TestCase):
    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.home = Path(self.tmp.name)
        (self.home / "AppData" / "Roaming" / "gcloud").mkdir(parents=True)
        patcher = patch.object(sources, "home", lambda: self.home)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _write(self, payload: dict, posix: bool = False) -> None:
        """The Windows location by default; `posix` for the other one.

        Both are looked at, and one machine really can have both - native
        tooling writes the first, anything under WSL or a copied dotfile the
        second.
        """
        folder = (
            self.home / ".config" / "gcloud"
            if posix
            else self.home / "AppData" / "Roaming" / "gcloud"
        )
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "application_default_credentials.json").write_text(
            json.dumps(payload), encoding="utf-8"
        )

    def test_a_service_account_is_usable(self):
        self._write(SERVICE_ACCOUNT)
        credential = sources._gcloud_adc()
        self.assertIsNotNone(credential)
        self.assertTrue(credential.usage_capable)
        self.assertEqual(credential.account, SERVICE_ACCOUNT["client_email"])
        self.assertEqual(credential.project, "my-project")

    def test_a_user_login_is_reported_as_limited_not_dropped(self):
        """The reported gap: it used to return None and disappear."""
        self._write(USER_LOGIN)
        credential = sources._gcloud_adc()
        self.assertIsNotNone(credential, "a present credential vanished")
        self.assertFalse(credential.usage_capable)
        self.assertIn("user login", credential.limited_reason)

    def test_the_reason_tells_the_user_what_to_do_instead(self):
        self._write(USER_LOGIN)
        reason = sources._gcloud_adc().limited_reason
        self.assertIn("gemini", reason)
        self.assertIn("service-account", reason)

    def test_the_refresh_token_is_never_read(self):
        """The policy the Limited state exists to protect."""
        self._write(USER_LOGIN)
        credential = sources._gcloud_adc()
        self.assertEqual(credential.value, "")
        self.assertNotIn("not-read", credential.limited_reason)

    def test_the_posix_location_is_read_too(self):
        self._write(SERVICE_ACCOUNT, posix=True)
        credential = sources._gcloud_adc()
        self.assertIsNotNone(credential)
        self.assertTrue(credential.usage_capable)

    def test_a_usable_account_wins_over_a_user_login_found_first(self):
        """Review point: the first file read used to end the search.

        A user login in the Windows path returned Limited immediately, and a
        service account sitting in the POSIX path - one this app can actually
        read usage with - was never looked at. Limited is only the right
        answer when there is nothing better anywhere.
        """
        self._write(USER_LOGIN)
        self._write(SERVICE_ACCOUNT, posix=True)
        credential = sources._gcloud_adc()
        self.assertTrue(
            credential.usage_capable,
            "a usable service account was hidden by a user login",
        )
        self.assertEqual(credential.account, SERVICE_ACCOUNT["client_email"])

    def test_the_card_says_Connected_when_one_of_them_is_usable(self):
        self._write(USER_LOGIN)
        self._write(SERVICE_ACCOUNT, posix=True)
        detection = resolve(
            "gemini",
            [s for s in sources.GEMINI_SOURCES if s.id == "gcloud_adc"],
        )
        self.assertEqual(detection.state, CONNECTED)

    def test_two_user_logins_are_still_just_limited(self):
        """The fallback must survive being passed over once."""
        self._write(USER_LOGIN)
        self._write({**USER_LOGIN, "account": "other@example.invalid"}, posix=True)
        credential = sources._gcloud_adc()
        self.assertIsNotNone(credential)
        self.assertFalse(credential.usage_capable)
        self.assertEqual(credential.account, USER_LOGIN["account"])

    def test_an_incomplete_key_is_limited_not_connected(self):
        """Found by review: `type` alone was enough to claim Connected.

        A key with no `private_key` cannot sign anything, so the card said
        Connected and the refresh that followed failed with no visible cause.
        Limited with the reason attached is the state that tells the truth.
        """
        self._write(INCOMPLETE_SERVICE_ACCOUNT)
        credential = sources._gcloud_adc()
        self.assertIsNotNone(credential)
        self.assertFalse(credential.usage_capable)
        self.assertIn("missing the fields", credential.limited_reason)

    def test_an_incomplete_key_does_not_hide_a_complete_one(self):
        self._write(INCOMPLETE_SERVICE_ACCOUNT)
        self._write(SERVICE_ACCOUNT, posix=True)
        credential = sources._gcloud_adc()
        self.assertTrue(
            credential.usage_capable,
            "a usable key was hidden by an unusable one found first",
        )
        self.assertEqual(credential.value, str(
            self.home / ".config" / "gcloud" / "application_default_credentials.json"
        ))

    def test_no_file_is_still_nothing(self):
        self.assertIsNone(sources._gcloud_adc())

    def test_the_connections_card_shows_Limited(self):
        self._write(USER_LOGIN)
        detection = resolve(
            "gemini",
            [s for s in sources.GEMINI_SOURCES if s.id == "gcloud_adc"],
        )
        self.assertEqual(detection.state, PARTIAL)
        self.assertEqual(detection.word, "Limited")
        self.assertIn("user login", detection.hint)

    def test_a_service_account_card_shows_Connected(self):
        self._write(SERVICE_ACCOUNT)
        detection = resolve(
            "gemini",
            [s for s in sources.GEMINI_SOURCES if s.id == "gcloud_adc"],
        )
        self.assertEqual(detection.state, CONNECTED)

    def test_nothing_found_is_still_missing(self):
        detection = resolve(
            "gemini",
            [s for s in sources.GEMINI_SOURCES if s.id == "gcloud_adc"],
        )
        self.assertEqual(detection.state, MISSING)


class ReadmeMatchesTheCodeTests(unittest.TestCase):
    def test_the_readme_no_longer_claims_bare_gcloud_adc(self):
        """It advertised a flow the detector never supported."""
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        self.assertNotIn("gcloud ADC", readme)

    def test_the_readme_says_what_is_supported(self):
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        self.assertIn("application-default location", readme)


if __name__ == "__main__":
    unittest.main()


class DetectorAndProviderAgreeTests(unittest.TestCase):
    """What the card promises is what the refresh can actually do.

    These were two separate lists of required fields, in two files, and they
    disagreed: `_gcloud_adc` asked only for `type`, while `_load_key_file`
    also wanted `client_email` and `private_key`. The gap did not show up as
    a wrong card - it showed up later, as a refresh error with no cause the
    user could see. Neither check is the authority here; agreeing is.
    """

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "key.json"

    def _provider_accepts(self, payload: dict) -> bool:
        from ai_usage_monitor.providers import gemini_provider

        self.path.write_text(json.dumps(payload), encoding="utf-8")
        provider = gemini_provider.GeminiProvider()
        try:
            provider._load_key_file(str(self.path))
        except gemini_provider._GeminiError:
            return False
        return True

    def test_they_agree_about_a_complete_key(self):
        self.assertTrue(sources.usable_service_account(SERVICE_ACCOUNT))
        self.assertTrue(self._provider_accepts(SERVICE_ACCOUNT))

    def test_they_agree_about_every_field_that_can_be_missing(self):
        for field in sources.SERVICE_ACCOUNT_FIELDS:
            with self.subTest(missing=field):
                payload = {k: v for k, v in SERVICE_ACCOUNT.items() if k != field}
                self.assertFalse(
                    sources.usable_service_account(payload),
                    f"the detector accepted a key with no {field}",
                )
                self.assertFalse(
                    self._provider_accepts(payload),
                    f"the provider accepted a key with no {field}",
                )

    def test_they_agree_about_a_file_that_is_not_a_key_at_all(self):
        self.assertFalse(sources.usable_service_account(USER_LOGIN))
        self.assertFalse(self._provider_accepts(USER_LOGIN))

    def test_an_empty_field_counts_as_missing(self):
        """A blank string is present and useless; `data.get` alone says yes."""
        self.assertFalse(
            sources.usable_service_account({**SERVICE_ACCOUNT, "private_key": ""})
        )


class ServiceAccountEnvTests(unittest.TestCase):
    """GOOGLE_APPLICATION_CREDENTIALS gets the same treatment.

    It was looser still: it read the file and reported Connected without
    looking at `type`, so any JSON at all on that path claimed a working
    Gemini credential.
    """

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "key.json"

    def _probe(self, payload: dict):
        self.path.write_text(json.dumps(payload), encoding="utf-8")
        with patch.dict(
            "os.environ", {"GOOGLE_APPLICATION_CREDENTIALS": str(self.path)}
        ):
            return sources._gemini_service_account_env()

    def test_a_complete_key_is_usable(self):
        credential = self._probe(SERVICE_ACCOUNT)
        self.assertTrue(credential.usage_capable)
        self.assertEqual(credential.value, str(self.path))

    def test_an_incomplete_key_is_limited(self):
        credential = self._probe(INCOMPLETE_SERVICE_ACCOUNT)
        self.assertFalse(credential.usage_capable)
        self.assertIn("missing the fields", credential.limited_reason)

    def test_something_that_is_not_a_key_is_limited_too(self):
        credential = self._probe({"hello": "world"})
        self.assertFalse(credential.usage_capable)

    def test_the_path_of_an_unusable_key_is_not_handed_on(self):
        """Nothing downstream should try to sign with it."""
        self.assertEqual(self._probe(INCOMPLETE_SERVICE_ACCOUNT).value, "")

    def test_an_unset_variable_is_still_nothing(self):
        with patch.dict("os.environ", {}, clear=True):
            self.assertIsNone(sources._gemini_service_account_env())


class SavedKeyTests(unittest.TestCase):
    """The key the user chose in Settings gets the same scrutiny.

    Found by review: two of the three ways a service account reaches this app
    were checked against `usable_service_account`, and the third - the file
    the user picked themselves - accepted any JSON it could parse. That is
    the one where picking the wrong file is most likely, and it was the one
    that said Connected and then failed on refresh.
    """

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "saved.json"

    def _detect(self, payload=None):
        from ai_usage_monitor.providers import gemini_provider

        if payload is not None:
            self.path.write_text(json.dumps(payload), encoding="utf-8")
        provider = gemini_provider.GeminiProvider()
        provider.configure(str(self.path), "")
        return provider._service_account_credential(str(self.path))

    def test_a_complete_key_is_usable(self):
        credential = self._detect(SERVICE_ACCOUNT)
        self.assertTrue(credential.usage_capable)
        self.assertEqual(credential.value, str(self.path))

    def test_a_key_missing_its_private_half_is_limited(self):
        credential = self._detect(INCOMPLETE_SERVICE_ACCOUNT)
        self.assertIsNotNone(credential, "the file the user chose vanished")
        self.assertFalse(
            credential.usage_capable,
            "a key the provider will reject was reported as usable",
        )
        self.assertIn("missing the fields", credential.limited_reason)

    def test_the_wrong_kind_of_json_is_limited(self):
        credential = self._detect(USER_LOGIN)
        self.assertFalse(credential.usage_capable)

    def test_a_file_that_is_not_json_says_so(self):
        self.path.write_text("not json at all", encoding="utf-8")
        credential = self._detect()
        self.assertIsNotNone(credential, "an unreadable file vanished silently")
        self.assertFalse(credential.usage_capable)
        self.assertIn("JSON", credential.limited_reason)

    def test_an_unusable_key_never_reaches_the_signer(self):
        """The path is withheld, so nothing downstream can try to use it."""
        self.assertEqual(self._detect(INCOMPLETE_SERVICE_ACCOUNT).value, "")

    def test_a_missing_file_is_nothing_at_all(self):
        """Not Limited: there is no credential here to explain."""
        from ai_usage_monitor.providers import gemini_provider

        provider = gemini_provider.GeminiProvider()
        self.assertIsNone(
            provider._service_account_credential(str(self.path / "nope.json"))
        )

    def test_the_card_does_not_say_Connected(self):
        """End to end through the source the Settings dialog binds."""
        from ai_usage_monitor.providers import gemini_provider

        self.path.write_text(
            json.dumps(INCOMPLETE_SERVICE_ACCOUNT), encoding="utf-8"
        )
        provider = gemini_provider.GeminiProvider()
        provider.configure(str(self.path), "")
        manual = [s for s in provider.sources() if s.id == "manual"]
        detection = resolve("gemini", manual)
        self.assertNotEqual(detection.state, CONNECTED)
        self.assertEqual(detection.state, PARTIAL)

    def test_all_three_ways_in_agree(self):
        """Saved, environment and gcloud must not disagree about one file.

        The rule is one list of required fields, checked in one place. Three
        answers to the same file is how this drifted apart before.
        """
        from ai_usage_monitor.providers import gemini_provider

        for payload, usable in (
            (SERVICE_ACCOUNT, True),
            (INCOMPLETE_SERVICE_ACCOUNT, False),
            (USER_LOGIN, False),
        ):
            with self.subTest(usable=usable):
                self.path.write_text(json.dumps(payload), encoding="utf-8")
                saved = gemini_provider.GeminiProvider()._service_account_credential(
                    str(self.path)
                )
                with patch.dict(
                    "os.environ",
                    {"GOOGLE_APPLICATION_CREDENTIALS": str(self.path)},
                ):
                    from_env = sources._gemini_service_account_env()
                self.assertEqual(saved.usage_capable, usable)
                self.assertEqual(from_env.usage_capable, usable)
                self.assertEqual(
                    sources.usable_service_account(payload), usable
                )


class JsonThatIsNotAnObjectTests(unittest.TestCase):
    """Valid JSON is not the same thing as a JSON object.

    Review point: the predicate went straight to `.get()`, so a file holding
    `[]` or `"text"` raised AttributeError. `detection.resolve()` catches what
    a probe raises, so the card said *not connected* - telling a user nothing
    had been found when in fact their file had been read and rejected. Every
    other unusable file says Limited and why; these have to as well.
    """

    ROOTS = ([], "text", 1, None, True, [SERVICE_ACCOUNT])

    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "key.json"

    def test_the_predicate_answers_instead_of_raising(self):
        for root in self.ROOTS:
            with self.subTest(root=root):
                self.assertFalse(sources.usable_service_account(root))

    def test_the_saved_path_reports_limited(self):
        from ai_usage_monitor.providers import gemini_provider

        for root in self.ROOTS:
            with self.subTest(root=root):
                self.path.write_text(json.dumps(root), encoding="utf-8")
                credential = gemini_provider.GeminiProvider(
                )._service_account_credential(str(self.path))
                self.assertIsNotNone(credential, "the file vanished from the card")
                self.assertFalse(credential.usage_capable)
                self.assertEqual(credential.value, "")

    def test_the_card_says_Limited_not_missing(self):
        """Through `resolve`, which is what swallowed the exception."""
        from ai_usage_monitor.providers import gemini_provider

        self.path.write_text("[]", encoding="utf-8")
        provider = gemini_provider.GeminiProvider()
        provider.configure(str(self.path), "")
        detection = resolve(
            "gemini", [s for s in provider.sources() if s.id == "manual"]
        )
        self.assertEqual(detection.state, PARTIAL)
        self.assertNotEqual(detection.state, MISSING)

    def test_the_other_two_paths_agree(self):
        for root in self.ROOTS:
            with self.subTest(root=root):
                self.path.write_text(json.dumps(root), encoding="utf-8")
                with patch.dict(
                    "os.environ",
                    {"GOOGLE_APPLICATION_CREDENTIALS": str(self.path)},
                ):
                    from_env = sources._gemini_service_account_env()
                self.assertFalse(from_env.usage_capable)

    def test_the_provider_rejects_it_with_a_message_not_a_crash(self):
        from ai_usage_monitor.providers import gemini_provider

        self.path.write_text("[]", encoding="utf-8")
        provider = gemini_provider.GeminiProvider()
        with self.assertRaises(gemini_provider._GeminiError):
            provider._load_key_file(str(self.path))
