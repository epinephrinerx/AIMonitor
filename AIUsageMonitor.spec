# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the single-file Windows build.

What is left out lives in `build_excludes.py`, shared with the one-directory
spec the installer uses. Keeping the lists here meant the two builds could
disagree about what ships, and they did.
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

a.binaries = TOC(without_stray_openssl(a.binaries))

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
