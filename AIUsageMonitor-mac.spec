# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the macOS .app bundle.

One-directory, never onefile. A onefile build on macOS unpacks its whole
payload to a temp directory on every launch, which for a Qt app is most of a
second of nothing happening before the first window - and it is the cold start
a menu-bar app pays every login. The .app that BUNDLE wraps around the
COLLECT tree starts straight from its own Contents.

The Qt exclude list is shared with the Windows specs: PySide6 ships far more
than a QWidgets dashboard needs, and PyInstaller's hook collects most of it by
default.
"""

APP_NAME = "AI Usage Monitor"
APP_VERSION = "1.1.0"
BUNDLE_ID = "net.apichart.aiusagemonitor"

block_cipher = None

EXCLUDED_QT = [
    "PySide6.Qt3DAnimation", "PySide6.Qt3DCore", "PySide6.Qt3DExtras",
    "PySide6.Qt3DInput", "PySide6.Qt3DLogic", "PySide6.Qt3DRender",
    "PySide6.QtBluetooth", "PySide6.QtCharts", "PySide6.QtConcurrent",
    "PySide6.QtDataVisualization", "PySide6.QtDesigner", "PySide6.QtGraphs",
    "PySide6.QtGraphsWidgets", "PySide6.QtHelp", "PySide6.QtHttpServer",
    "PySide6.QtLocation", "PySide6.QtMultimedia",
    "PySide6.QtMultimediaWidgets", "PySide6.QtNetworkAuth", "PySide6.QtNfc",
    "PySide6.QtOpenGL", "PySide6.QtOpenGLWidgets", "PySide6.QtPdf",
    "PySide6.QtPdfWidgets", "PySide6.QtPositioning", "PySide6.QtQml",
    "PySide6.QtQuick", "PySide6.QtQuick3D", "PySide6.QtQuickControls2",
    "PySide6.QtQuickWidgets", "PySide6.QtRemoteObjects", "PySide6.QtScxml",
    "PySide6.QtSensors", "PySide6.QtSerialBus", "PySide6.QtSerialPort",
    "PySide6.QtSpatialAudio", "PySide6.QtSql", "PySide6.QtStateMachine",
    "PySide6.QtTest", "PySide6.QtTextToSpeech", "PySide6.QtUiTools",
    "PySide6.QtWebChannel", "PySide6.QtWebEngineCore",
    "PySide6.QtWebEngineQuick", "PySide6.QtWebEngineWidgets",
    "PySide6.QtWebSockets", "PySide6.QtXml",
]

EXCLUDED_STDLIB = ["tkinter", "unittest", "pydoc_data", "test", "distutils"]

a = Analysis(
    ["run_ai_monitor.py"],
    pathex=[],
    binaries=[],
    # The README ships with the app: the in-app Readme window reads this exact
    # file, so there is never a second copy of the text to drift. The PNG is
    # the runtime window icon; the .icns below is the Dock tile, which macOS
    # reads from the bundle rather than from the running process.
    datas=[
        ("assets/icon.png", "assets"),
        ("assets/icon.ico", "assets"),
        ("README.md", "."),
    ],
    # `rsa` is imported lazily inside the Gemini provider (only needed when a
    # service account is configured), so name it explicitly.
    hiddenimports=["rsa", "pyasn1"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=EXCLUDED_QT + EXCLUDED_STDLIB,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,          # the one-directory difference
    name="AIUsageMonitor",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,                  # GUI app: no terminal window
    disable_windowed_traceback=False,
    # argv_emulation turns Finder "open with" events into argv. This app takes
    # no file arguments and the shim installs an event handler that can swallow
    # the first activation, so leave it off.
    argv_emulation=False,
    # None means "build for the interpreter's own architecture". A universal2
    # build needs a universal2 Python as well, so forcing it here would fail on
    # an arm64-only interpreter rather than doing anything useful.
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="AIUsageMonitor",
)

app = BUNDLE(
    coll,
    name=f"{APP_NAME}.app",
    icon="assets/icon.icns",
    bundle_identifier=BUNDLE_ID,
    version=APP_VERSION,
    info_plist={
        "CFBundleName": APP_NAME,
        "CFBundleDisplayName": APP_NAME,
        "CFBundleShortVersionString": APP_VERSION,
        "CFBundleVersion": APP_VERSION,
        # Qt renders at the display's real resolution; without this macOS
        # upscales a 1x rendering and every label looks soft on a Retina panel.
        "NSHighResolutionCapable": True,
        # PySide6 wheels are built against the macOS 13 SDK.
        "LSMinimumSystemVersion": "13.0",
        # False: this app has a real window, so it belongs in the Dock and the
        # app switcher. Set it True for a menu-bar-only build - the tray icon
        # keeps working either way.
        "LSUIElement": False,
        # Qt reads the system appearance itself and the app paints its own
        # surfaces, so let macOS report dark mode rather than forcing Aqua.
        "NSRequiresAquaSystemAppearance": False,
        "NSHumanReadableCopyright": "GPL-3.0. © Apichart Chantanis.",
    },
)
