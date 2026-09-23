# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the single-file Windows build.

PySide6 ships far more than a QWidgets dashboard needs, and PyInstaller's hook
collects most of it by default. The exclude list below drops the Qt modules
this app never imports, which roughly halves the executable.
"""

import pathlib

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

# Qt's TLS plugin (`qopensslbackend.dll`) loads OpenSSL at run time by name,
# and the name it asks for is the Qt spelling: `libcrypto-3-x64.dll`. Nothing
# here answers that - Python's own copy is `libcrypto-3.dll`, no suffix - so
# PyInstaller goes looking down PATH and bundles whatever it finds. It has
# been Git's copy before; on this machine it was PHP 8.5, installed by WinGet
# and on PATH, carrying 8.2 MB of OpenSSL that has nothing to do with this
# application.
#
# It is safe to drop because **this app never uses Qt networking**: every
# HTTPS request goes through `urllib.request`, which uses Python's OpenSSL.
# `QNetwork` and `QSsl` appear nowhere in the source, and `openUrl` hands the
# address to the system browser rather than fetching anything.
#
# If Qt networking is ever introduced, this has to be revisited, and the
# right answer then is to ship a known OpenSSL rather than to inherit a
# stranger's. Verify by opening the built .exe and refreshing all three
# services - a TLS failure here takes out every provider at once.
STRAY_OPENSSL = {"libcrypto-3-x64.dll", "libssl-3-x64.dll"}

a = Analysis(
    # The ai_usage_monitor entry point. A second entry point once existed for a
    # retired predecessor and was briefly named here by mistake, which built the
    # wrong application under this name; both are gone now.
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

_kept = []
for entry in a.binaries:
    if pathlib.PurePath(entry[0]).name.lower() in STRAY_OPENSSL:
        print(f"spec: dropping {entry[1]} - see STRAY_OPENSSL")
        continue
    _kept.append(entry)
a.binaries = TOC(_kept)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="AIUsageMonitor",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,  # GUI app: no console window
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon="assets/icon.ico",
    version="version_info.txt",
)
