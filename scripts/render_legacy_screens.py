"""Render Python 1.3.3 widgets from synthetic data; all output stays in the repository.

Usage: python scripts/render_legacy_screens.py <legacy-root> <fixture.json> <output-dir>
Dependencies are loaded from artifacts/verification/python, never installed globally.
No worker, network, credential detection, real settings or real tray icon is started.
"""
import datetime as dt
import json
import os
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "artifacts/verification/python"))
legacy, fixture_path, output_path = map(Path, sys.argv[1:])
output = output_path.resolve()
if not output.is_relative_to(ROOT):
    raise ValueError("Output must stay inside the repository")
output.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(legacy.resolve()))
os.environ["QT_QPA_PLATFORM"] = "offscreen"

from PySide6.QtCore import QSettings, QTimer, QSize
from PySide6.QtGui import QFont, QFontDatabase
from PySide6.QtWidgets import QApplication

def forbidden(*args, **kwargs):
    raise RuntimeError("Real startup/credentials/network are forbidden in screen evidence")

# Install the startup stub before any baseline module can bind a direct import.
startup_stub = ModuleType("ai_usage_monitor.startup")
startup_stub.supported = lambda: True
startup_stub.reconcile = lambda *args, **kwargs: False
startup_stub.current_command = forbidden
startup_stub.launch_command = forbidden
startup_stub.is_enabled = forbidden
startup_stub.apply = forbidden
startup_stub.set_enabled = forbidden
sys.modules["ai_usage_monitor.startup"] = startup_stub

from ai_usage_monitor import startup, theme as theming
from ai_usage_monitor.settings import Settings
from ai_usage_monitor.main_window import MainWindow
from ai_usage_monitor.providers.base import Meter, Stat, HistoryView, ProviderSnapshot
from ai_usage_monitor.detection import Detection
from ai_usage_monitor.worker import RefreshResult
from ai_usage_monitor.tray import TrayController
from ai_usage_monitor.settings_dialog import ProviderSettingsDialog
from ai_usage_monitor.connect_dialog import ConnectDialog
from ai_usage_monitor.about_dialog import VersionDialog, DeveloperDialog
from ai_usage_monitor.readme_dialog import DocumentDialog
from ai_usage_monitor.log_dialog import UsageLogDialog
from ai_usage_monitor import report as reporting

# Fail closed if a rendering path accidentally tries real startup access.
startup.reconcile = lambda *args, **kwargs: False
startup.apply = forbidden
startup.set_enabled = forbidden
TrayController.available = staticmethod(lambda: False)

class OfflineWindow(MainWindow):
    def _start_worker(self):
        pass
    def _push_all_credentials(self):
        pass
    def _restore_geometry(self, mode, default):
        self.resize(1120, 820)
    def _watch_screens(self):
        pass
    def refresh(self):
        pass
    def _tick(self):
        pass

data = json.loads(fixture_path.read_text(encoding="utf-8"))
app = QApplication([])
# Qt's offscreen Windows platform does not enumerate the system font database.
# Register real Windows fonts explicitly; otherwise every character becomes a box.
for font_file in ("segoeui.ttf", "segoeuib.ttf", "seguisb.ttf", "seguisym.ttf"):
    if QFontDatabase.addApplicationFont(str(Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / font_file)) < 0:
        raise RuntimeError(f"Could not load required Windows font {font_file}")
app.setFont(QFont("Segoe UI", 9))
manifest = []

def capture(widget, name):
    widget.show()
    app.processEvents()
    if not widget.grab().save(str(output / (name + ".png"))):
        raise RuntimeError(f"Could not save {name}")
    manifest.append(name)

for theme_name in ("light", "dark"):
    theme = theming.resolve(theme_name)
    app.setPalette(theming.build_qpalette(theme))
    # Bypass Settings.__init__, which performs Registry migration. Use a file-backed
    # fixture QSettings with fallback disabled so it cannot see user/system scopes.
    settings = Settings.__new__(Settings)
    settings._q = QSettings(str(output / (theme_name + ".ini")), QSettings.Format.IniFormat)
    settings._q.setFallbacksEnabled(False)
    settings.theme = theme_name
    settings.show_connections_at_startup = False
    settings.interval = 0
    settings.range_days = 7
    settings.start_with_windows = False
    settings.opacity = 1
    settings.always_on_top = False
    window = OfflineWindow(settings)
    snapshots = {}
    for p in data["providers"]:
        history = HistoryView(
            buckets=[SimpleNamespace(day=dt.date.fromisoformat(b["day"]), per_model=b["values"], total=sum(b["values"].values())) for b in data["buckets"]],
            series=["Model A", "Model B"], by_model=[("Model A", 42000), ("Model B", 21000)],
            by_project=[("Synthetic project", 63000)], days=7)
        snapshots[p["id"]] = ProviderSnapshot(
            p["id"], configured=True,
            meters=[Meter(m["key"], m["title"], m["subtitle"], m["percent"]) for m in p["meters"]],
            stats=[Stat(s["label"], s["value"]) for s in data["stats"]],
            history=history, account="Synthetic account", value_note="Equivalent API value is an estimate.",
            detection=Detection(p["id"], state="connected", source_id="fixture", source_label="Synthetic login", account="Synthetic account"))
    window._on_result(RefreshResult(snapshots=snapshots, days=7, metric="Total tokens"))
    window.status.setText("Synthetic fixture · Manual refresh")
    for i, provider in enumerate(window.providers):
        window.tabs.setCurrentIndex(i)
        capture(window, f"dashboard-{provider.id}-{theme_name}")
    window.stack.setCurrentWidget(window.connections)
    capture(window, f"connections-{theme_name}")
    window.stack.setCurrentWidget(window.dashboard_page)
    window.tabs.setCurrentIndex(0)
    window.resize(300, 220)
    capture(window, f"dashboard-minimum-{theme_name}")
    window.hide()
    for timer in window.findChildren(QTimer):
        timer.stop()

    compact = window.compact
    compact.setParent(None)
    compact.set_snapshot("Claude", snapshots["claude"], "")
    compact.set_pageable(True)
    for width, height in ((230, 175), (300, 300), (150, 140)):
        compact.resize(width, height)
        capture(compact, f"widget-{width}x{height}-{theme_name}")
    compact.hide()

    dialog = ProviderSettingsDialog(window.providers, settings, theme)
    capture(dialog, f"settings-{theme_name}")
    dialog.reject()
    for provider in window.providers:
        dialog = ConnectDialog(provider, snapshots[provider.id].detection, settings, theme)
        capture(dialog, f"connect-{provider.id}-{theme_name}")
        dialog.reject()
    for name, dialog in (
        ("version", VersionDialog(theme)), ("developer", DeveloperDialog(theme)),
        ("readme", DocumentDialog(theme, "AI Usage Monitor — Readme")),
        ("license", DocumentDialog(theme, "AI Usage Monitor — License Agreement", names=("LICENSE",), markdown=False)),
        ("notices", DocumentDialog(theme, "AI Usage Monitor — Third-party notices", names=("THIRD-PARTY-NOTICES.md",)))):
        capture(dialog, f"about-{name}-{theme_name}")
        dialog.reject()
    report = reporting.build(window.providers, snapshots, {}, 7, "Total tokens", generated_at=dt.datetime(2026, 10, 14, tzinfo=dt.timezone.utc))
    dialog = UsageLogDialog(report, theme)
    capture(dialog, f"usage-log-{theme_name}")
    dialog.reject()
    # The icon painter itself has no tray/system side effect.
    painter = SimpleNamespace(theme=theme)
    TrayController._draw(painter, 42, "normal").pixmap(64, 64).save(str(output / (f"tray-{theme_name}.png")))
    settings._q.sync()

(output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(f"Rendered {len(manifest)} synthetic Qt client-area screens; no worker/network/real startup.")
