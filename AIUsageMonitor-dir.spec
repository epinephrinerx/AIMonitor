# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the INSTALLED (one-directory) AI Usage Monitor build.

The onefile build is the portable one: a single .exe you can copy anywhere, at
the cost of unpacking its whole payload into %TEMP% on every launch. An app you
install and open daily should not pay that on each start, so the installer
ships a one-directory tree instead - same code, same excludes, faster cold
start (measured at roughly 0.6 s against 1.4 s for onefile).
"""

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
    # file, so there is never a second copy of the text to drift.
    # LICENSE and the third-party notices travel with the binary: GPL-3.0
    # requires the licence to be conveyed along with the program, and the
    # Apache-2.0 and BSD-2-Clause components require their notices to be
    # reproduced in binary distributions.
    datas=[
        ("assets/icon.ico", "assets"),
        ("README.md", "."),
        ("LICENSE", "."),
        ("THIRD-PARTY-NOTICES.md", "."),
    ],
    # `rsa` is imported lazily inside the Gemini provider (only needed when a
    # service account is configured), so name it explicitly.
    hiddenimports=["rsa", "pyasn1"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=EXCLUDED_QT + EXCLUDED_STDLIB,
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
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
    console=False,                  # GUI app: no console window
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon="assets/icon.ico",
    version="version_info.txt",
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
