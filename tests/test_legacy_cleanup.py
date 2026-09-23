"""The pre-rename settings must not outlive the migration - or precede it.

The application was renamed from ClaudeUsageMonitor, and the migration copied
the old settings forward without removing them - deliberately, so an older
build would still work. That build has since been retired and deleted from
the repository, and what the choice left behind was a `ClaudeUsageMonitor`
key in the registry of everyone who had ever run the old version, long after
the name had gone from everything else.

Removing it means deleting from someone's registry, so most of what is here
is about the ways that could go wrong. The one that nearly shipped: asking a
plain `QSettings` whether a key is "already here" does not ask about its own
storage. Fallbacks are on by default, so an organisation-wide default answers
for it - the copy is skipped as redundant and the only real copy is then
deleted as spent.
"""

import os
import unittest
import uuid

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings  # noqa: E402

from ai_usage_monitor import settings as settings_module  # noqa: E402

WINDOWS = os.name == "nt"


def wipe(path):
    """Delete a registry subtree, bottom up. Test cleanup only.

    Deliberately not `settings_module._prune_empty_registry_keys`: cleanup
    that runs the code under test cannot say whether the test left the
    machine clean or the subject simply failed the same way twice.
    """
    if not WINDOWS:
        return
    import winreg

    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            count, _, _ = winreg.QueryInfoKey(key)
            children = [winreg.EnumKey(key, i) for i in range(count)]
    except OSError:
        return
    for child in children:
        wipe(f"{path}\\{child}")
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def exists(path):
    if not WINDOWS:
        return False
    import winreg

    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, path).Close()
        return True
    except FileNotFoundError:
        return False


def values_under(path):
    """Every value in a registry subtree, read without QSettings.

    Only `FileNotFoundError` counts as "nothing there". Swallowing every
    `OSError` would let an access failure read as an empty key, and a
    cleanup assertion would then pass over litter it could not see.

    Constructing a `QSettings` and calling `allKeys()` **creates the key**,
    even when only reading - measured. Asserting "the old location is empty"
    through one therefore puts it back, and the test that followed then found
    a key its own assertion had just made.
    """
    if not WINDOWS:
        return []
    import winreg

    found = []
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            subkeys, count, _ = winreg.QueryInfoKey(key)
            for i in range(count):
                found.append(winreg.EnumValue(key, i)[0])
            children = [winreg.EnumKey(key, i) for i in range(subkeys)]
    except FileNotFoundError:
        return []
    for child in children:
        found += [f"{child}/{name}" for name in values_under(rf"{path}\{child}")]
    return sorted(found)


class MigrationTestCase(unittest.TestCase):
    def setUp(self):
        # Throwaway names, so a mistake here cannot reach anyone's real
        # settings - including the ones this project once destroyed.
        #
        # `ORG`/`APP` are patched rather than the scope environment variable,
        # because `Settings.__init__` skips the migration entirely when a test
        # scope is set. That guard is right - it keeps the suite away from the
        # real legacy key - but it also means a test driven that way never
        # reaches the code under test, which is how the first version of this
        # file managed to fail while proving nothing.
        tag = uuid.uuid4().hex[:10]
        self.org = f"AIMonitorTest{tag}"
        self.app = f"AIMonitorTestApp{tag}"
        self.legacy_org = f"AIMonitorLegacy{tag}"
        self.legacy_app = f"AIMonitorLegacyApp{tag}"

        self._real = (
            settings_module.ORG,
            settings_module.APP,
            settings_module.LEGACY_ORG,
            settings_module.LEGACY_APP,
        )
        settings_module.ORG = self.org
        settings_module.APP = self.app
        settings_module.LEGACY_ORG = self.legacy_org
        settings_module.LEGACY_APP = self.legacy_app
        self._scope = os.environ.pop("AI_USAGE_MONITOR_SETTINGS_SCOPE", None)
        self.addCleanup(self._restore)

    def _restore(self):
        (
            settings_module.ORG,
            settings_module.APP,
            settings_module.LEGACY_ORG,
            settings_module.LEGACY_APP,
        ) = self._real
        if self._scope is not None:
            os.environ["AI_USAGE_MONITOR_SETTINGS_SCOPE"] = self._scope
        for org in (self.legacy_org, self.org):
            wipe(rf"Software\{org}")
            self.assertFalse(
                exists(rf"Software\{org}"),
                f"the test left {org} behind in the registry",
            )

    def legacy(self):
        store = QSettings(self.legacy_org, self.legacy_app)
        store.setFallbacksEnabled(False)
        return store

    def legacy_path(self):
        return rf"Software\{self.legacy_org}\{self.legacy_app}"

    def legacy_values(self):
        """What is left in the old location, without creating it to find out."""
        return values_under(self.legacy_path())

    def current(self):
        store = QSettings(self.org, self.app)
        store.setFallbacksEnabled(False)
        return store

    def seed_legacy(self, **values):
        store = self.legacy()
        for key, value in values.items():
            store.setValue(key, value)
        store.sync()

    def seed_current(self, **values):
        store = self.current()
        for key, value in values.items():
            store.setValue(key, value)
        store.sync()


class MigrationTests(MigrationTestCase):
    def test_the_settings_come_across_before_anything_goes(self):
        self.seed_legacy(theme="dark", interval=240)
        fresh = settings_module.Settings()
        self.assertEqual(fresh.theme, "dark")
        self.assertEqual(self.legacy_values(), [])

    def test_a_value_this_application_already_has_wins(self):
        """The copy does not overwrite: what this application holds stands."""
        self.seed_current(theme="light", migratedFromLegacy=True)
        self.seed_legacy(theme="dark")

        settings_module.Settings()

        self.assertEqual(self.current().value("theme"), "light")

    def test_a_value_that_differs_is_not_deleted_to_tidy_a_name(self):
        """Matching names are not proof that anything was copied.

        The copy step declines to overwrite, so a name can match while the
        contents differ - a setting changed since the migration, or a sealed
        credential stored badly here over one still intact there. An earlier
        version deleted on the strength of the name alone, and the tests
        written for it asserted that as the desired outcome.
        """
        self.seed_current(theme="light", migratedFromLegacy=True)
        self.seed_legacy(theme="dark")

        settings_module.Settings()

        self.assertEqual(self.legacy_values(), ["theme"])
        self.assertIsNone(self.current().value("legacyDiscarded"))

    def test_an_install_that_migrated_long_ago_is_still_tidied(self):
        """Most machines are here: marked as migrated releases back, so the
        copy path returns before it could ever clean anything."""
        # The same values, because that is what the copy left behind.
        self.seed_current(migratedFromLegacy=True, theme="dark", interval=240)
        self.seed_legacy(theme="dark", interval=240)

        settings_module.Settings()

        self.assertEqual(self.legacy_values(), [])

    def test_nothing_is_removed_while_a_key_is_only_in_the_old_place(self):
        """The guard that matters. A key that failed to copy, or one an old
        build wrote afterwards, must not be thrown away to tidy a name."""
        self.seed_current(migratedFromLegacy=True)
        self.seed_legacy(strandedKey="only here")

        settings_module.Settings()

        self.assertIn("strandedKey", self.legacy_values())

    def test_an_organisation_default_is_not_mistaken_for_a_migrated_value(self):
        """The bug that nearly shipped.

        `QSettings` consults fallbacks by default, so an organisation-wide
        default answers `value()` for a key this application has never held,
        and `allKeys()` lists it. Migration would read that as "already
        carried over", skip the copy, and the cleanup would then delete the
        user's only copy.
        """
        if not WINDOWS:
            self.skipTest("organisation defaults are a registry arrangement")
        import winreg

        defaults = winreg.CreateKey(
            winreg.HKEY_CURRENT_USER,
            rf"Software\{self.org}\OrganizationDefaults",
        )
        winreg.SetValueEx(defaults, "theme", 0, winreg.REG_SZ, "from-org")
        defaults.Close()
        self.seed_legacy(theme="dark")

        settings_module.Settings()

        self.assertEqual(
            self.current().value("theme"),
            "dark",
            "the user's value was never copied across",
        )

    def test_the_marker_is_only_set_once_the_old_place_is_really_empty(self):
        """Both halves in one test: the marker asserts a fact about the old
        location, so checking it alone would pass if production set it
        without removing anything."""
        self.seed_legacy(theme="dark")
        settings_module.Settings()
        self.assertIsNotNone(self.current().value("legacyDiscarded"))
        self.assertEqual(self.legacy_values(), [])
        self.assertFalse(exists(self.legacy_path()))

    def test_a_second_run_does_not_touch_the_old_place_again(self):
        """The earlier version of this checked only that the marker was
        present after two runs, which stayed true with the guard deleted."""
        self.seed_legacy(theme="dark")
        settings_module.Settings()
        self.assertEqual(self.legacy_values(), [])

        # Something puts a key back afterwards. The tidy-up is done; it must
        # not reach in a second time.
        self.seed_legacy(writtenLater="still here")
        settings_module.Settings()

        self.assertIn("writtenLater", self.legacy_values())

    def test_the_registry_key_itself_is_gone_not_just_emptied(self):
        """`QSettings.clear()` leaves the key behind, so the old name stays
        visible in the registry editor - which is the entire complaint."""
        if not WINDOWS:
            self.skipTest("registry keys are a Windows concern")
        self.seed_legacy(theme="dark")
        settings_module.Settings()
        self.assertFalse(exists(rf"Software\{self.legacy_org}"))

    def test_a_machine_that_never_ran_the_old_build_ends_up_clean(self):
        """Checking the old location means opening it, and opening a
        `QSettings` creates the key. It has to be gone again afterwards, or
        the tidy-up would leave behind exactly what it came to remove."""
        if not WINDOWS:
            self.skipTest("registry keys are a Windows concern")
        settings_module.Settings()
        self.assertFalse(exists(rf"Software\{self.legacy_org}"))

    def test_a_nested_group_comes_across_and_is_cleared(self):
        store = self.legacy()
        store.setValue("geometry/dashboard", "blob")
        store.setValue("geometry/widget", "blob2")
        store.sync()

        settings_module.Settings()

        self.assertEqual(self.current().value("geometry/dashboard"), "blob")
        self.assertEqual(self.legacy_values(), [])
        self.assertFalse(exists(rf"Software\{self.legacy_org}"))


class _ArrivesLate:
    """A legacy store where one key shows up only after the list is taken.

    Stands in for the way the old location can gain a key mid-cleanup:
    something writes to it between the check and the removal. A registry key
    cannot be held against that, so the removal has to be narrow enough that
    a key it never checked survives - and the tidy-up must then notice and
    decline to mark itself done.

    The key is hidden from the *first* `allKeys()` only. Hiding it from every
    call would also hide it from the check that runs afterwards, which is the
    one expected to catch it, and the test would then be describing a
    different situation than the one it names.
    """

    def __init__(self, store, late):
        self._store = store
        self._late = late
        self._asked = False

    def allKeys(self):  # noqa: N802 - mirrors QSettings
        keys = self._store.allKeys()
        if not self._asked:
            self._asked = True
            return [k for k in keys if k != self._late]
        return keys

    def __getattr__(self, name):
        return getattr(self._store, name)


class ARaceWithTheOldBuildTests(MigrationTestCase):
    def late_arrival(self, late):
        """Patch `_own_scope` so one legacy key appears only after the check."""
        real = settings_module._own_scope
        legacy_org, legacy_app = self.legacy_org, self.legacy_app

        def scope(org, app):
            store = real(org, app)
            if (org, app) == (legacy_org, legacy_app):
                return _ArrivesLate(store, late)
            return store

        self.addCleanup(setattr, settings_module, "_own_scope", real)
        settings_module._own_scope = scope

    def test_a_key_that_was_never_checked_survives_the_removal(self):
        """`clear()` would take it: it empties the whole location rather than
        the keys that were verified, so anything that arrived after the check
        goes with them."""
        self.seed_current(migratedFromLegacy=True, theme="dark")
        self.seed_legacy(theme="dark", arrivedLate="unchecked")
        self.late_arrival("arrivedLate")

        settings_module.Settings()

        self.assertIn("arrivedLate", self.legacy_values())

    def test_a_value_beneath_a_checked_one_is_not_taken_with_it(self):
        """`QSettings.remove(key)` removes the key *and everything beneath
        it* - measured. Removing a checked `geometry/dashboard` would take a
        `geometry/dashboard/newValue` that arrived afterwards and never was
        checked, so the removal goes through the registry one value at a time.

        The descendant has to be hidden from the first listing. Present from
        the start it is in the snapshot, the equality guard stops on it, and
        nothing is deleted whichever way the removal is written.
        """
        if not WINDOWS:
            self.skipTest("registry keys are a Windows concern")
        import winreg

        self.seed_current(migratedFromLegacy=True)
        store = self.legacy()
        store.setValue("geometry/dashboard", "checked")
        store.sync()
        self.seed_current(**{"geometry/dashboard": "checked"})
        deep = winreg.CreateKey(
            winreg.HKEY_CURRENT_USER,
            self.legacy_path() + chr(92) + "geometry" + chr(92) + "dashboard",
        )
        winreg.SetValueEx(deep, "newValue", 0, winreg.REG_SZ, "unchecked")
        deep.Close()
        self.late_arrival("geometry/dashboard/newValue")

        settings_module.Settings()

        self.assertIn("geometry/dashboard/newValue", self.legacy_values())

    def test_the_tidy_up_is_not_marked_done_when_the_key_will_not_go(self):
        """The marker means "there is nothing left there". If the registry
        key could not be removed, that is not true, and the next launch has
        to try again rather than leave it forever."""
        self.seed_current(migratedFromLegacy=True, theme="dark")
        self.seed_legacy(theme="dark")
        real = settings_module._prune_empty_registry_keys
        self.addCleanup(
            setattr, settings_module, "_prune_empty_registry_keys", real
        )
        settings_module._prune_empty_registry_keys = lambda org, app: False

        settings_module.Settings()

        self.assertIsNone(self.current().value("legacyDiscarded"))

    def test_and_the_tidy_up_is_not_marked_done(self):
        """So the next launch looks again, instead of leaving it forever."""
        self.seed_current(migratedFromLegacy=True, theme="dark")
        self.seed_legacy(theme="dark", arrivedLate="unchecked")
        self.late_arrival("arrivedLate")

        settings_module.Settings()

        self.assertIsNone(self.current().value("legacyDiscarded"))


class WhenStorageFailsTests(MigrationTestCase):
    """If the copies cannot be written, the originals must stay put."""

    def fail_writes(self):
        self._real_status = QSettings.status
        self.addCleanup(self.restore_writes)
        QSettings.status = lambda self: QSettings.Status.AccessError

    def restore_writes(self):
        QSettings.status = self._real_status

    def test_the_originals_survive_when_the_store_reports_a_problem(self):
        """What this checks, exactly: that a non-`NoError` status stops the
        removal. It does not make a real write fail - `QSettings.status` is
        replaced, the storage underneath still works - so it is evidence
        about the guard, not about Qt's flushing. The earlier name claimed
        the latter."""
        self.seed_current(migratedFromLegacy=True, theme="dark")
        self.seed_legacy(theme="dark")
        self.fail_writes()

        settings_module.Settings()

        self.assertEqual(self.legacy_values(), ["theme"])

    def test_and_it_is_not_marked_done_either(self):
        self.seed_current(migratedFromLegacy=True, theme="dark")
        self.seed_legacy(theme="dark")
        self.fail_writes()

        settings_module.Settings()

        self.restore_writes()
        self.assertIsNone(self.current().value("legacyDiscarded"))


class PruneEmptyKeyTests(unittest.TestCase):
    """`_prune_empty_registry_keys` must refuse rather than force."""

    def setUp(self):
        if not WINDOWS:
            self.skipTest("registry keys are a Windows concern")
        self.org = f"AIMonitorProbe{uuid.uuid4().hex[:10]}"
        self.addCleanup(self._cleanup)

    def _cleanup(self):
        wipe(rf"Software\{self.org}")
        self.assertFalse(
            exists(rf"Software\{self.org}"),
            "the test left a probe key behind in the registry",
        )

    def test_a_sibling_application_keeps_the_organisation_key(self):
        for app in ("AppOne", "AppTwo"):
            store = QSettings(self.org, app)
            store.setValue("x", 1)
            store.sync()
        QSettings(self.org, "AppOne").clear()
        QSettings(self.org, "AppOne").sync()

        settings_module._prune_empty_registry_keys(self.org, "AppOne")

        self.assertTrue(exists(rf"Software\{self.org}\AppTwo"))
        self.assertFalse(exists(rf"Software\{self.org}\AppOne"))

    def test_a_key_that_still_holds_a_value_is_left_alone(self):
        store = QSettings(self.org, "AppOne")
        store.setValue("x", 1)
        store.sync()

        settings_module._prune_empty_registry_keys(self.org, "AppOne")

        self.assertTrue(exists(rf"Software\{self.org}\AppOne"))

    def test_an_empty_child_group_does_not_block_the_removal(self):
        """`allKeys()` cannot see an empty subkey, so nothing upstream could
        have checked it. Pruning bottom up removes it on its own terms."""
        import winreg

        winreg.CreateKey(
            winreg.HKEY_CURRENT_USER, rf"Software\{self.org}\AppOne\emptyGroup"
        ).Close()

        settings_module._prune_empty_registry_keys(self.org, "AppOne")

        self.assertFalse(exists(rf"Software\{self.org}\AppOne"))

    def test_a_child_group_that_holds_something_stops_it(self):
        import winreg

        key = winreg.CreateKey(
            winreg.HKEY_CURRENT_USER, rf"Software\{self.org}\AppOne\keepMe"
        )
        winreg.SetValueEx(key, "x", 0, winreg.REG_SZ, "data")
        key.Close()

        settings_module._prune_empty_registry_keys(self.org, "AppOne")

        self.assertTrue(exists(rf"Software\{self.org}\AppOne\keepMe"))

    def test_an_empty_sibling_application_is_not_swept_up(self):
        """The regression review round 11 found.

        Pruning from the leaves of the organisation reached every child, so
        an application that merely happened to be empty was deleted along
        with the one that was asked for. The recursion belongs inside the
        target; the organisation key is examined, never descended into.
        """
        sibling = QSettings(self.org, "EmptySibling")
        sibling.setValue("y", 1)
        sibling.sync()
        sibling.clear()
        sibling.sync()
        target = QSettings(self.org, "Target")
        target.setValue("x", 1)
        target.sync()
        target.clear()
        target.sync()

        settings_module._prune_empty_registry_keys(self.org, "Target")

        self.assertFalse(exists(rf"Software\{self.org}\Target"))
        self.assertTrue(
            exists(rf"Software\{self.org}\EmptySibling"),
            "an application nobody asked about was deleted",
        )

    def test_it_says_whether_the_organisation_key_is_gone(self):
        """The caller only marks the job done when it really is."""
        store = QSettings(self.org, "Target")
        store.setValue("x", 1)
        store.sync()
        self.assertFalse(
            settings_module._prune_empty_registry_keys(self.org, "Target")
        )
        store.clear()
        store.sync()
        self.assertTrue(
            settings_module._prune_empty_registry_keys(self.org, "Target")
        )

    def test_a_missing_key_reports_success(self):
        """Nothing there is the outcome the caller wanted, so it counts as
        done - the caller sets its marker on this answer."""
        self.assertTrue(
            settings_module._prune_empty_registry_keys(
                f"NoSuch{self.org}", "NoSuch"
            )
        )

    def test_the_delete_path_refuses_an_unsafe_name(self):
        """The check exists; this is what makes the removal consult it.

        Both halves matter. The organisation and application come from module
        constants, but the key comes from whatever the old location happens
        to hold, and it is split on `/` to build a path.
        """
        self.assertFalse(
            settings_module._delete_registry_value("a" + chr(92) + "b", "App", "key")
        )
        self.assertFalse(
            settings_module._delete_registry_value(
                self.org, "App" + chr(92) + "Else", "key"
            )
        )
        self.assertFalse(
            settings_module._delete_registry_value(self.org, "App", "trailing/")
        )
        self.assertFalse(
            settings_module._delete_registry_value(
                self.org, "App", "a" + chr(92) + "b/name"
            )
        )

    def test_a_value_rewritten_between_the_check_and_the_delete_survives(self):
        """The compare happens again under the handle that does the delete,
        so the window is microseconds rather than the whole scan. It cannot
        be closed - Windows has no atomic compare-and-delete for a registry
        value - but a value that changed in between is left alone."""
        store = QSettings(self.org, "AppOne")
        store.setValue("theme", "as-checked")
        store.sync()

        refused = settings_module._delete_registry_value(
            self.org, "AppOne", "theme", expected="something-else"
        )

        self.assertFalse(refused)
        self.assertEqual(QSettings(self.org, "AppOne").value("theme"), "as-checked")

    def test_a_value_that_still_matches_is_removed(self):
        store = QSettings(self.org, "AppOne")
        store.setValue("theme", "as-checked")
        store.sync()

        self.assertTrue(
            settings_module._delete_registry_value(
                self.org, "AppOne", "theme", expected="as-checked"
            )
        )
        self.assertEqual(values_under(rf"Software\{self.org}\AppOne"), [])

    def test_the_default_value_name_qt_uses_is_understood(self):
        """Qt's Windows backend hands `Default` back from `allKeys()` for the
        key's unnamed value. Deleting a value literally called "Default"
        would miss it, the old key would still hold something, and the
        tidy-up would never finish."""
        import winreg

        key = winreg.CreateKey(
            winreg.HKEY_CURRENT_USER, rf"Software\{self.org}\AppOne"
        )
        winreg.SetValueEx(key, "", 0, winreg.REG_SZ, "unnamed")
        key.Close()

        self.assertTrue(
            settings_module._delete_registry_value(self.org, "AppOne", "Default")
        )
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, rf"Software\{self.org}\AppOne"
        ) as handle:
            _, values, _ = winreg.QueryInfoKey(handle)
        self.assertEqual(values, 0)

    def test_a_name_carrying_a_separator_is_refused(self):
        """The path is built by interpolation, so a separator would move
        which key is examined and deleted."""
        for bad in ("", r"Foo\Bar", "Foo/Bar"):
            with self.subTest(name=bad):
                self.assertFalse(settings_module._is_safe_key_name(bad))
        self.assertTrue(settings_module._is_safe_key_name("ClaudeUsageMonitor"))


if __name__ == "__main__":
    unittest.main()
