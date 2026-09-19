import base64
import datetime as dt
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from ai_usage_monitor import codex_usage
from ai_usage_monitor.detection import Credential, OAUTH, resolve
from ai_usage_monitor.providers.openai_provider import OpenAIProvider
from ai_usage_monitor.providers import sources


def jwt(payload):
    return "header." + base64.urlsafe_b64encode(json.dumps(payload).encode()).decode().rstrip("=") + ".signature"


def quota():
    return {"rateLimits": {"limitId": "codex", "primary": {
        "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 2000000000,
    }, "secondary": {"usedPercent": 80, "windowDurationMins": 10080, "resetsAt": 2000500000}}}


class DetectionTests(unittest.TestCase):
    def test_codex_home_and_expiration_read_only(self):
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ, {"CODEX_HOME": root}):
            path = Path(root) / "auth.json"
            original = json.dumps({"tokens": {"access_token": jwt({"exp": 1}),
                                              "account_id": "account", "id_token": jwt({"email": "test@example.com"})}})
            path.write_text(original, encoding="utf-8")
            detected = resolve("openai", [sources.OPENAI_SOURCES[-1]])
            self.assertEqual(detected.state, "expired")
            self.assertEqual(detected.credential.account_id, "account")
            self.assertEqual(path.read_text(encoding="utf-8"), original)
            self.assertNotIn(detected.credential.value, repr(detected))

    def test_saved_and_environment_keys_do_not_mask_codex(self):
        credential = Credential(OAUTH, "token", account_id="account")
        with patch.object(sources, "read_json", return_value={"tokens": {"access_token": "token", "account_id": "account"}}), patch.dict(os.environ, {"OPENAI_ADMIN_KEY": "", "OPENAI_API_KEY": "sk-project"}):
            provider = OpenAIProvider()
            self.assertEqual(provider.detect().source_id, "codex_cli")
            self.assertTrue(provider.is_configured())
            self.assertEqual(provider._resolved_key(), ("", ""))
            provider.configure("sk-admin-test")
            self.assertEqual(provider.detect().source_id, "codex_cli")
            self.assertEqual(provider._resolved_key(), ("", ""))
            self.assertEqual(provider._key, "sk-admin-test")
            with patch.dict(os.environ, {"OPENAI_ADMIN_KEY": "sk-admin-env"}):
                self.assertEqual(provider.detect().source_id, "codex_cli")

    def test_expired_codex_is_not_replaced_by_admin_spend(self):
        provider = OpenAIProvider()
        provider.configure("sk-admin-test")
        with patch.object(sources, "read_json", return_value={"tokens": {
                "access_token": jwt({"exp": 1}), "account_id": "account"}}):
            snapshot = provider.fetch(14, "Total tokens", False)
        self.assertEqual(snapshot.detection.source_id, "codex_cli")
        self.assertTrue(snapshot.unauthorized)
        self.assertIn("expired", snapshot.error)

    def test_admin_fallback_when_codex_login_is_missing(self):
        provider = OpenAIProvider()
        provider.configure("sk-admin-test")
        with patch.object(sources, "read_json", return_value=None):
            self.assertEqual(provider.detect().source_id, "manual")
            self.assertEqual(provider._resolved_key()[0], "sk-admin-test")


class MappingTests(unittest.TestCase):
    def test_windows_deduplicate_and_use_server_times(self):
        payload = quota()
        payload["rateLimitsByLimitId"] = {"codex": payload["rateLimits"], "model": {
            "limitName": "Model", "primary": {"usedPercent": 95, "windowDurationMins": 60}}}
        meters = codex_usage.meters(payload)
        self.assertEqual(len(meters), 3)
        self.assertEqual([m.percent for m in meters], [25, 80, 95])
        self.assertEqual(meters[1].title, "Weekly")
        self.assertEqual(meters[0].resets_at.timestamp(), 2000000000)
        self.assertEqual(meters[2].severity, "critical")

    def test_missing_invalid_or_zero_percent(self):
        for value in [None, "50", True, float("nan"), float("inf"), -1]:
            payload = {"rateLimits": {"primary": {"usedPercent": value}}}
            self.assertEqual(codex_usage.meters(payload), [])
        self.assertEqual(codex_usage.meters({"rateLimits": {"primary": {"usedPercent": 0}}})[0].percent, 0)

    def test_history_does_not_invent_models_spend_or_missing_days(self):
        today = dt.date.today()
        payload = {"summary": {"lifetimeTokens": 1000, "peakDailyTokens": None},
                   "dailyUsageBuckets": [{"startDate": today.isoformat(), "tokens": 100},
                                         {"startDate": "bad", "tokens": 123}]}
        history, stats = codex_usage.history(payload, 14, "Total tokens")
        self.assertEqual(len(history.buckets), 1)
        self.assertEqual(history.buckets[0].total, 100)
        self.assertEqual(history.by_model, [])
        self.assertEqual(len(stats), 2)
        for metric in ["Equivalent value", "Output tokens"]:
            self.assertIsNone(codex_usage.history(payload, 14, metric)[0])

    def test_history_failure_preserves_quota(self):
        """A missing daily history is a gap, not a failed service.

        The quota windows here came from the server on the same call. This
        used to travel as `snapshot.error`, which put an error banner over
        live gauges and made the refresh count as failed.
        """
        provider = OpenAIProvider()
        detected = Mock(credential=Credential(OAUTH, "token", account_id="account"),
                        usable=True, account="test", hint="")
        with patch.object(provider, "detect", return_value=detected), patch.object(codex_usage, "fetch", return_value=(quota(), None, "History unavailable")):
            snapshot = provider.fetch(14, "Total tokens", True)
        self.assertEqual(len(snapshot.meters), 2)
        self.assertEqual(snapshot.history_error, "History unavailable")
        self.assertIsNone(snapshot.error)
        self.assertTrue(snapshot.ok, "a refresh with live quota is a success")

    def test_no_quota_windows_is_still_a_real_failure(self):
        """The separation must not swallow an actually failed refresh."""
        provider = OpenAIProvider()
        detected = Mock(credential=Credential(OAUTH, "token", account_id="account"),
                        usable=True, account="test", hint="")
        with patch.object(provider, "detect", return_value=detected), patch.object(codex_usage, "fetch", return_value=({}, None, None)):
            snapshot = provider.fetch(14, "Total tokens", True)
        self.assertEqual(snapshot.meters, [])
        self.assertIsNotNone(snapshot.error)
        self.assertFalse(snapshot.ok)

    def test_admin_path_still_reports_spend(self):
        provider = OpenAIProvider()
        provider.configure("sk-admin-test", "100")
        with patch.object(sources, "read_json", return_value=None), patch.object(provider, "_total_cost", side_effect=[20, 5]), patch.object(codex_usage, "fetch") as fetch:
            snapshot = provider.fetch(14, "Total tokens", False)
        self.assertEqual(snapshot.meters[0].percent, 20)
        self.assertEqual(snapshot.meters[1].detail, "$5.00")
        fetch.assert_not_called()


class ProtocolTests(unittest.TestCase):
    def client(self, messages):
        process = Mock(stdin=io.StringIO(), stdout=io.StringIO("".join(json.dumps(m) + "\n" for m in messages)))
        return codex_usage._Client(process), process

    def test_ignores_notifications_and_matches_id(self):
        client, process = self.client([{"method": "account/updated"}, {"id": 90, "result": {}}, {"id": 1, "result": quota()}])
        self.assertEqual(client.call("account/rateLimits/read"), quota())

    def test_refresh_refused_without_exposing_token(self):
        client, process = self.client([{"id": "server-1", "method": "account/chatgptAuthTokens/refresh"}])
        with self.assertRaises(codex_usage.CodexError) as error:
            client.call("account/rateLimits/read")
        self.assertTrue(error.exception.unauthorized)
        self.assertIn("Read-only monitor", process.stdin.getvalue())

    def test_server_error_redacted(self):
        client, _ = self.client([{"id": 1, "error": {"message": "secret-bearer-token"}}])
        with self.assertRaises(codex_usage.CodexError) as error:
            client.call("account/rateLimits/read")
        self.assertNotIn("secret-bearer-token", str(error.exception))

    def test_timeout(self):
        client, _ = self.client([])
        client.reader.join()
        client.messages.get()
        with patch.object(codex_usage, "TIMEOUT_SECONDS", 0.01), self.assertRaisesRegex(codex_usage.CodexError, "timed out"):
            client.call("account/rateLimits/read")

    def test_isolated_child_cleaned_up_after_failure(self):
        credential = Credential(OAUTH, "test-secret", account_id="account")
        process = Mock()
        process.poll.return_value = None
        with patch.object(codex_usage, "executable", return_value="codex.exe"), patch.object(codex_usage.subprocess, "Popen", return_value=process) as spawn, patch.object(codex_usage, "_Client") as cls:
            cls.return_value.call.side_effect = codex_usage.CodexError("failed")
            with self.assertRaises(codex_usage.CodexError):
                codex_usage.fetch(credential, False)
        args, kwargs = spawn.call_args
        self.assertNotIn("test-secret", str(args))
        self.assertNotIn("test-secret", str(kwargs))
        self.assertFalse(Path(kwargs["env"]["CODEX_HOME"]).exists())
        process.terminate.assert_called_once()
        process.stdin.close.assert_called_once()
        process.stdout.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
