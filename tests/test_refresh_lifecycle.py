"""A refresh asked for is a refresh that happens, and shutdown does not hang.

Two findings from the 2026-09-18 review, in one place because they share the
machinery.

* `refresh()` returned early while one was running, so changing the range or
  the metric mid-fetch lost the new choice entirely. The window kept the old
  figures until the interval timer came round - up to half an hour.
* Shutdown waited two seconds, then waited out Claude's socket timeout: about
  nineteen seconds. Providers are fetched one after another, so the real worst
  case was their sum, roughly a minute. Past the budget the window was
  destroyed anyway, taking its child QThread with it, and deleting a running
  QThread aborts the process.
"""

from __future__ import annotations

import json
import threading
import unittest
import urllib.request
from unittest.mock import Mock, patch

from ai_usage_monitor import codex_usage
from ai_usage_monitor.providers.base import Provider, ProviderSnapshot
from ai_usage_monitor.worker import RefreshResult, RefreshWorker


class _Recorder(Provider):
    """A provider that records how it was called, without touching anything."""

    def __init__(self, provider_id: str, on_fetch=None) -> None:
        self.id = provider_id
        self.display_name = provider_id.title()
        self.calls: list[tuple] = []
        self._on_fetch = on_fetch

    def configure(self, key: str, extra: str = "") -> None:
        return

    def is_configured(self) -> bool:
        return True

    def fetch(self, days, metric, want_history):
        self.calls.append((days, metric, want_history))
        if self._on_fetch is not None:
            self._on_fetch(self)
        return ProviderSnapshot(provider_id=self.id, configured=True)


def _worker(*providers: Provider) -> RefreshWorker:
    with patch("ai_usage_monitor.worker.build_all", return_value=list(providers)):
        return RefreshWorker()


class WorkerCancelTests(unittest.TestCase):
    def test_cancelling_stops_before_the_next_provider(self):
        """The wait on shutdown was the sum of everything still to come."""
        first = None

        def stop_after_me(provider):
            provider_worker.cancel()

        first = _Recorder("claude", on_fetch=stop_after_me)
        second = _Recorder("openai")
        third = _Recorder("gemini")
        provider_worker = _worker(first, second, third)

        results = []
        provider_worker.finished.connect(results.append)
        provider_worker.refresh(14, "Total tokens", True, "", 1)

        self.assertEqual(len(first.calls), 1)
        self.assertEqual(second.calls, [], "kept going after being cancelled")
        self.assertEqual(third.calls, [])

    def test_a_cancelled_refresh_emits_nothing(self):
        """Nobody is listening; the window is already coming down."""
        worker = _worker(_Recorder("claude"))
        worker.cancel()
        results = []
        worker.finished.connect(results.append)
        worker.refresh(14, "Total tokens", True, "", 1)
        self.assertEqual(results, [])

    def test_the_cancel_event_reaches_the_provider(self):
        seen = []
        provider = _Recorder("claude", on_fetch=lambda p: seen.append(p.cancel))
        worker = _worker(provider)
        worker.refresh(14, "Total tokens", True, "", 1)
        self.assertIsInstance(seen[0], threading.Event)
        self.assertFalse(seen[0].is_set())

    def test_the_result_carries_what_was_asked_for(self):
        """A chart heading must describe its own data, not the combo boxes."""
        worker = _worker(_Recorder("claude"))
        results = []
        worker.finished.connect(results.append)
        worker.refresh(30, "Equivalent value", True, "", 7)
        self.assertEqual(results[0].days, 30)
        self.assertEqual(results[0].metric, "Equivalent value")
        self.assertEqual(results[0].request_id, 7)

    def test_a_provider_failure_still_does_not_stop_the_others(self):
        class _Boom(_Recorder):
            def fetch(self, days, metric, want_history):
                raise RuntimeError("boom")

        after = _Recorder("gemini")
        worker = _worker(_Boom("claude"), after)
        results = []
        worker.finished.connect(results.append)
        worker.refresh(14, "Total tokens", True, "", 1)
        self.assertIn("Unexpected error", results[0].snapshots["claude"].error)
        self.assertEqual(len(after.calls), 1)


class ProviderCancelTests(unittest.TestCase):
    def test_cancelled_is_false_without_an_event(self):
        self.assertFalse(Provider().cancelled())

    def test_cancelled_follows_the_event(self):
        provider = Provider()
        provider.cancel = threading.Event()
        self.assertFalse(provider.cancelled())
        provider.cancel.set()
        self.assertTrue(provider.cancelled())


class CodexWaitTests(unittest.TestCase):
    """The longest single wait in a refresh, and the one that had to give."""

    def _client(self, cancel):
        client = codex_usage._Client.__new__(codex_usage._Client)
        client.process = Mock()
        client.messages = __import__("queue").Queue()
        client.sequence = 0
        client.stopped = threading.Event()
        client.cancel = cancel
        client.send = lambda message: None
        return client

    def test_an_already_cancelled_call_gives_up_at_once(self):
        cancel = threading.Event()
        cancel.set()
        client = self._client(cancel)
        with self.assertRaises(codex_usage.CodexError) as caught:
            client.call("account/rateLimits/read")
        self.assertIn("cancelled", str(caught.exception).lower())

    def test_cancelling_mid_wait_ends_the_wait(self):
        """Without slicing, this sat on one 25-second blocking get()."""
        cancel = threading.Event()
        client = self._client(cancel)
        threading.Timer(0.1, cancel.set).start()
        with self.assertRaises(codex_usage.CodexError):
            client.call("account/rateLimits/read")

    def test_the_poll_interval_never_extends_the_deadline(self):
        self.assertLess(
            codex_usage.CANCEL_POLL_SECONDS, codex_usage.TIMEOUT_SECONDS
        )


class ShutdownBudgetTests(unittest.TestCase):
    """The numbers behind the old budget, so the mistake cannot come back."""

    def test_the_grace_period_is_not_expected_to_cover_a_whole_refresh(self):
        from ai_usage_monitor import main_window
        from ai_usage_monitor.providers import gemini_provider

        serial_worst_case = (
            codex_usage.TIMEOUT_SECONDS + gemini_provider.TIMEOUT_SECONDS
        )
        self.assertLess(
            main_window.SHUTDOWN_GRACE_MS / 1000,
            serial_worst_case,
            "a grace period long enough to wait out every provider is not a "
            "grace period; cancellation is what makes shutdown prompt",
        )

    def test_a_thread_that_overruns_is_parked_rather_than_deleted(self):
        import ast
        import pathlib

        source = (
            pathlib.Path(__file__).resolve().parent.parent
            / "ai_usage_monitor"
            / "main_window.py"
        ).read_text(encoding="utf-8")
        tree = ast.parse(source)
        release = next(
            node
            for node in ast.walk(tree)
            if isinstance(node, ast.FunctionDef) and node.name == "_release_worker"
        )
        body = ast.unparse(release)
        self.assertIn("cancel()", body, "shutdown must ask the worker to stop")
        self.assertIn(
            "_ABANDONED_THREADS",
            body,
            "an overrunning thread must outlive the window, not be deleted "
            "underneath itself",
        )


if __name__ == "__main__":
    unittest.main()


# -- the window's side of it ------------------------------------------------
#
# Constructing a real MainWindow is heavy, but the queueing decision lives in
# the interaction between `refresh`, `_dispatch` and `_on_result`, and a test
# that reimplements that interaction would prove nothing about it.

import os  # noqa: E402

# The settings scope is applied per test, not here: `Settings` reads it on
# every construction, so assigning it at import time would put whichever test
# file imported last in charge of all of them.
SCOPE = "RefreshLifecycleTests"
# Qt reads this once, when the first QApplication is built.
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from ai_usage_monitor import startup  # noqa: E402
from ai_usage_monitor.main_window import DASHBOARD, MainWindow  # noqa: E402
from ai_usage_monitor.settings import ORG, SCOPE_ENV_VAR, Settings  # noqa: E402
from ai_usage_monitor.tray import TrayController  # noqa: E402


class WindowQueueTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.sent: list[tuple] = []
        patches = [
            patch.dict(os.environ, {SCOPE_ENV_VAR: SCOPE}),
            # Never touch the real Run key from a test.
            patch.object(startup, "set_enabled", lambda enabled: None),
            patch.object(startup, "reconcile", lambda default_on, first_run: default_on),
            patch.object(TrayController, "available", staticmethod(lambda: False)),
            # No thread: this test is about which requests are sent, and when.
            patch.object(MainWindow, "_start_worker", self._fake_worker),
            patch.object(MainWindow, "_push_all_credentials", lambda self: None),
        ]
        for item in patches:
            item.start()
            self.addCleanup(item.stop)

        self.window = MainWindow(Settings())
        self.window.request_refresh.connect(
            lambda *args: self.sent.append(args)
        )
        self.window.mode = DASHBOARD
        self.addCleanup(self.window.deleteLater)
        self.addCleanup(lambda: QSettings(ORG, SCOPE).clear())

    @staticmethod
    def _fake_worker(window) -> None:
        window.thread = Mock()
        window.worker = Mock()

    def _result(self, request_id: int) -> RefreshResult:
        return RefreshResult(
            snapshots={}, days=14, metric="Total tokens", request_id=request_id
        )

    def test_a_request_made_while_busy_is_sent_when_the_first_finishes(self):
        """The reported bug: it used to be dropped on the floor."""
        self.window.refresh()
        self.assertEqual(len(self.sent), 1)

        self.window.range_combo.setCurrentIndex(2)  # 30 days; calls refresh()
        self.assertEqual(len(self.sent), 1, "a second fetch started concurrently")
        self.assertIsNotNone(self.window._pending_refresh)

        self.window._on_result(self._result(1))
        self.assertEqual(len(self.sent), 2, "the queued request was lost")
        self.assertEqual(self.sent[1][0], 30, "the queued request used stale days")

    def test_only_the_newest_queued_request_is_kept(self):
        """Changing the range twice fetches the second range, not both."""
        self.window.refresh()
        self.window.range_combo.setCurrentIndex(2)   # 30 days
        self.window.range_combo.setCurrentIndex(0)   # 7 days
        self.window._on_result(self._result(1))
        self.assertEqual(len(self.sent), 2)
        self.assertEqual(self.sent[1][0], 7)

    def test_nothing_queued_means_nothing_extra_is_sent(self):
        self.window.refresh()
        self.window._on_result(self._result(1))
        self.assertEqual(len(self.sent), 1)

    def test_each_dispatch_carries_a_new_request_id(self):
        self.window.refresh()
        self.window._on_result(self._result(1))
        self.window.refresh()
        self.assertEqual([args[-1] for args in self.sent], [1, 2])

    def test_the_report_describes_the_result_not_the_combo_boxes(self):
        """A log headed 30 days over 14 days of rows is what gets quoted."""
        self.window._on_result(
            RefreshResult(
                snapshots={}, days=14, metric="Total tokens", request_id=1
            )
        )
        self.window.range_combo.setCurrentIndex(2)          # now says 30 days
        self.window.metric_combo.setCurrentText("Output tokens")
        report = self.window._build_report()
        self.assertEqual(report.days, 14)
        self.assertEqual(report.metric, "Total tokens")


class EveryPathIsCancellableTests(unittest.TestCase):
    """Cancellation reached Codex and Gemini, but not Claude or Admin API.

    The worker set the event and parked an overrunning thread, so a QThread
    was never destroyed while running - but the two paths that never looked at
    the event carried on. Admin API can start four requests in a row at 15
    seconds each, and Claude does usage then profile. Closing the window left
    them running to completion.
    """

    def test_claude_stops_before_the_next_request(self):
        from ai_usage_monitor import api
        from ai_usage_monitor.providers.claude_provider import ClaudeProvider

        provider = ClaudeProvider()
        provider.cancel = threading.Event()
        provider.cancel.set()

        with self.assertRaises(api.ApiError) as caught:
            api._get("/v1/anything", "token", provider.cancel)
        self.assertIn("cancelled", str(caught.exception).lower())

    def test_claude_hands_the_event_to_both_of_its_endpoints(self):
        """A check the provider does not perform is a check that is not there.

        Asserts it found both calls first. Without that, deleting or renaming
        them would turn this into an assertion about an empty loop.
        """
        import ast
        import pathlib

        source = (
            pathlib.Path(__file__).resolve().parent.parent
            / "ai_usage_monitor"
            / "providers"
            / "claude_provider.py"
        ).read_text(encoding="utf-8")
        seen = set()
        for node in ast.walk(ast.parse(source)):
            if not isinstance(node, ast.Call):
                continue
            name = getattr(node.func, "attr", "")
            if name not in ("fetch_usage", "fetch_account"):
                continue
            seen.add(name)
            # The event itself, not a word that happens to contain "cancel":
            # every argument is checked for the attribute being passed.
            passes_event = any(
                isinstance(arg, ast.Attribute) and arg.attr == "cancel"
                for arg in list(node.args) + [kw.value for kw in node.keywords]
            )
            self.assertTrue(
                passes_event,
                f"line {node.lineno}: {ast.unparse(node)} does not pass "
                "self.cancel to the API helper",
            )
        self.assertEqual(
            seen,
            {"fetch_usage", "fetch_account"},
            "both Claude endpoints should be called from the provider; "
            f"found {sorted(seen)}",
        )

    def test_the_admin_api_stops_before_the_next_of_its_four_requests(self):
        from ai_usage_monitor.providers import openai_provider as module

        provider = module.OpenAIProvider()
        provider.configure("sk-admin-test", "")
        provider.cancel = threading.Event()
        provider.cancel.set()

        with self.assertRaises(module._OpenAIError) as caught:
            provider._get("/v1/organization/costs", {})
        self.assertIn("cancelled", str(caught.exception).lower())

    def test_an_uncancelled_request_is_not_blocked(self):
        """The guard must only fire when the event is actually set."""
        from ai_usage_monitor.providers import openai_provider as module

        provider = module.OpenAIProvider()
        provider.configure("sk-admin-test", "")
        provider.cancel = threading.Event()   # created, never set
        with patch("urllib.request.urlopen") as urlopen:
            urlopen.return_value.__enter__.return_value.read.return_value = b"{}"
            self.assertEqual(provider._get("/v1/organization/costs", {}), {})

    def test_gemini_stops_before_the_next_monitoring_request(self):
        """The third choke point, checked directly like the other two."""
        import datetime as dt

        from ai_usage_monitor.providers import gemini_provider as module

        provider = module.GeminiProvider()
        provider.cancel = threading.Event()
        provider.cancel.set()

        now = dt.datetime.now(dt.timezone.utc)
        with self.assertRaises(module._GeminiError) as caught:
            provider._query("token", "project", now, now, 86_400)
        self.assertIn("cancelled", str(caught.exception).lower())

    def test_a_cancelled_refresh_makes_no_second_request(self):
        """Each provider driven through its real fetch, with no login needed.

        The first version of this built providers from the environment and
        only asserted that `urlopen` was never called. On a machine signed in
        to nothing every provider returns before the network anyway, so
        deleting the guards outright would have left it green.

        The transport is what gets faked here, not the request helpers: the
        cancellation guards live inside `api._get`, `OpenAIProvider._get` and
        `GeminiProvider._query`, so patching those would step over the very
        thing under test. An earlier draft did exactly that and reported four
        Admin API requests where it should have seen one.
        """
        from ai_usage_monitor import api
        from ai_usage_monitor.providers.claude_provider import ClaudeProvider
        from ai_usage_monitor.providers import (
            gemini_provider,
            openai_provider,
            sources,
        )

        def transport(cancel, seen, payload=b"{}"):
            """Answer the first request, then declare the refresh cancelled.

            `cancel` may be None, which is the control: the same fetch with
            nobody cancelling it, used to show that the request the guard is
            meant to stop is a request that would otherwise be made.
            """

            def urlopen(request, *args, **kwargs):
                seen.append(getattr(request, "full_url", str(request)))
                if cancel is not None:
                    cancel.set()
                body = Mock()
                body.read.return_value = payload
                handle = Mock()
                handle.__enter__ = Mock(return_value=body)
                handle.__exit__ = Mock(return_value=False)
                return handle

            return urlopen

        # Claude asks for the quota, then for the profile - but only if the
        # quota reply contained limits. Review caught the first version of
        # this answering `{}`: with no limits there are no meters, the profile
        # branch is never entered, and the test passed because the second
        # request was unreachable rather than because it was stopped. The
        # payload below is the smallest one that makes it reachable.
        quota = json.dumps(
            {"limits": [{"kind": "session", "percent": 12, "is_active": True}]}
        ).encode()

        def claude_fetch(cancel, seen):
            provider = ClaudeProvider()
            provider.cancel = cancel or threading.Event()
            creds = Mock(expired=False, access_token="t")
            with (
                patch("urllib.request.urlopen", transport(cancel, seen, quota)),
                patch("ai_usage_monitor.credentials.load", return_value=creds),
            ):
                return provider.fetch(14, "Total tokens", False)

        with self.subTest(provider="claude-control"):
            # Nobody cancels: both endpoints are asked, which is what makes
            # the assertion below mean something.
            seen = []
            snapshot = claude_fetch(None, seen)
            self.assertTrue(snapshot.meters, "the quota reply produced no meters")
            self.assertEqual(
                len(seen), 2, f"the profile request is not reachable: {seen}"
            )

        with self.subTest(provider="claude"):
            cancel, seen = threading.Event(), []
            claude_fetch(cancel, seen)
            self.assertEqual(len(seen), 1, f"Claude kept going: {seen}")

        with self.subTest(provider="openai-admin"):
            cancel, seen = threading.Event(), []
            admin = openai_provider.OpenAIProvider()
            admin.configure("sk-admin-test", "")
            admin.cancel = cancel
            with (
                patch.object(sources, "read_json", return_value=None),
                patch("urllib.request.urlopen", transport(cancel, seen)),
            ):
                admin.fetch(14, "Total tokens", True)
            self.assertEqual(len(seen), 1, f"the Admin API kept going: {seen}")

        with self.subTest(provider="gemini"):
            cancel, seen = threading.Event(), []
            gemini = gemini_provider.GeminiProvider()
            gemini.cancel = cancel
            detected = Mock(
                usable=True,
                state="connected",
                account="a",
                credential=Mock(project="p", account="a"),
                hint="",
            )
            with (
                patch.object(gemini, "detect", return_value=detected),
                patch.object(gemini, "_bearer", return_value="token"),
                patch("urllib.request.urlopen", transport(cancel, seen)),
            ):
                gemini.fetch(14, "Total tokens", True)
            self.assertEqual(len(seen), 1, f"Gemini kept querying: {seen}")

        # Gemini can make three requests, not one: a token exchange, a project
        # lookup, and only then the Monitoring query. Review found the guard
        # sat on the last of them, so a refresh cancelled during the token
        # request still went on to ask Cloud Resource Manager - each with its
        # own 20-second timeout. The subtest above mocks `_bearer` and hands
        # over a project, so it never reaches either. This one does.
        with self.subTest(provider="gemini-two-step"):
            cancel, seen = threading.Event(), []
            gemini = gemini_provider.GeminiProvider()
            gemini.cancel = cancel
            detected = Mock(
                usable=True,
                state="connected",
                account="a",
                credential=Mock(project="", account="a"),   # nothing to skip to
                hint="",
            )
            # The lookup has to *succeed*, or `fetch` gives up on its own and
            # the count is 1 whether or not anything was cancelled - which is
            # what the first draft of this did. Only the contract test below
            # caught it, and a behavioural test that cannot fail is worse than
            # no behavioural test, because it reads like cover.
            projects = json.dumps({"projects": [{"projectId": "p"}]}).encode()
            with (
                patch.object(gemini, "detect", return_value=detected),
                patch.object(gemini, "_bearer", return_value="token"),
                patch("urllib.request.urlopen", transport(cancel, seen, projects)),
            ):
                gemini.fetch(14, "Total tokens", True)
            self.assertEqual(
                len(seen), 1, f"Gemini went on to the next endpoint: {seen}"
            )
            self.assertIn(
                "cloudresourcemanager",
                seen[0],
                "the one request made was not the project lookup",
            )

        with self.subTest(provider="gemini-already-cancelled"):
            # The shape that actually happens: the window is closed, so the
            # event is set before the provider is even reached. Nothing should
            # go out at all. With the guard on the Monitoring call alone, the
            # project lookup went out anyway - a request nobody is waiting for,
            # holding a 20-second timeout open after the window is gone.
            cancel, seen = threading.Event(), []
            cancel.set()
            gemini = gemini_provider.GeminiProvider()
            gemini.cancel = cancel
            detected = Mock(
                usable=True,
                state="connected",
                account="a",
                credential=Mock(project="", account="a"),
                hint="",
            )
            with (
                patch.object(gemini, "detect", return_value=detected),
                patch.object(gemini, "_bearer", return_value="token"),
                patch("urllib.request.urlopen", transport(None, seen)),
            ):
                gemini.fetch(14, "Total tokens", True)
            self.assertEqual(
                seen, [], f"a cancelled refresh still reached the network: {seen}"
            )

        # `api` is imported for the guard it holds; referencing it keeps the
        # import honest rather than decorative.
        self.assertTrue(hasattr(api, "_get"))

    def test_the_gemini_choke_point_refuses_before_opening_a_socket(self):
        from ai_usage_monitor.providers import gemini_provider

        gemini = gemini_provider.GeminiProvider()
        gemini.cancel = threading.Event()
        gemini.cancel.set()
        request = urllib.request.Request("https://example.invalid/")
        with patch("urllib.request.urlopen") as opened:
            with self.assertRaises(gemini_provider._GeminiError) as caught:
                gemini._request(request, "example.invalid")
        opened.assert_not_called()
        self.assertIn("cancelled", str(caught.exception).lower())

    def test_every_gemini_request_goes_through_the_choke_point(self):
        """A new endpoint must not be able to slip past the check.

        Three call sites each carried their own guard, or did not; that is
        how two of them ended up without one. The rule is now structural:
        `_send` is the transport and only `_request` may call it.
        """
        import ast
        import inspect

        from ai_usage_monitor.providers import gemini_provider

        tree = ast.parse(inspect.getsource(gemini_provider))
        callers = []
        for node in ast.walk(tree):
            if not isinstance(node, ast.FunctionDef):
                continue
            for call in ast.walk(node):
                if (
                    isinstance(call, ast.Call)
                    and isinstance(call.func, ast.Name)
                    and call.func.id == "_send"
                ):
                    callers.append(node.name)

        self.assertTrue(callers, "no call to _send found - has it been renamed?")
        self.assertEqual(
            sorted(set(callers)),
            ["_request"],
            f"these reach the network without a cancellation check: {callers}",
        )
