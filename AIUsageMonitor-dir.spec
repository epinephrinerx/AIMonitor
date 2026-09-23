# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the INSTALLED (one-directory) AI Usage Monitor build.

The onefile build is the portable one: a single .exe you can copy anywhere, at
the cost of unpacking its whole payload into %TEMP% on every launch. An app you
install and open daily should not pay that on each start, so the installer
ships a one-directory tree instead - same code, faster cold start (measured
at roughly 0.6 s against 1.4 s for onefile).

"Same excludes" is not a claim this file can make on its own, and for a while
it made it while being wrong: both specs read `build_excludes.py` now.
"""

import sys as _sys

# One source for what both specs leave out. See `build_excludes.py` - the two
# specs drifted apart once already, and the installer went on shipping what
# the portable had dropped.
_sys.path.insert(0, SPECPATH)
from build_excludes import (  # noqa: E402
    EXCLUDED_QT,
    EXCLUDED_STDLIB,
    without_stray_openssl,
)

block_cipher = None

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
    TOC(without_stray_openssl(a.binaries)),
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="AIUsageMonitor",
)
