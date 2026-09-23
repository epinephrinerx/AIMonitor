"""What both PyInstaller specs leave out, in one place.

There are two specs - `AIUsageMonitor.spec` builds the portable onefile,
`AIUsageMonitor-dir.spec` the one-directory tree the installer wraps - and
they have to agree about what ships. They did not: each carried its own copy
of the lists, and when the stray-OpenSSL filter was added to one of them the
installer kept shipping 8.2 MB that the portable had already dropped. The
dir spec's own docstring said "same excludes" while it was no longer true.

This is the same failure `CLAUDE.md` and `AGENTS.md` had, and the same answer:
one source, two thin readers. `tests/test_build_specs.py` holds it there.
"""

# PySide6 ships far more than a QWidgets dashboard needs, and PyInstaller's
# hook collects most of it by default. Dropping what this app never imports
# roughly halves the executable.
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
# and the name it asks for is the Qt spelling, with a `-x64` suffix. Nothing
# in this build answers to that - Python's own copy is `libcrypto-3.dll`, no
# suffix - so PyInstaller goes looking down PATH and bundles whatever it
# finds there. It has been Git's copy before; on one machine it was PHP 8.5,
# installed by WinGet and on PATH, carrying 8.2 MB of OpenSSL that has
# nothing to do with this application.
#
# Dropping it is safe because **this app never uses Qt networking**: every
# HTTPS request goes through `urllib.request`, which uses Python's OpenSSL.
# `QNetwork` and `QSsl` appear nowhere in the source, and `openUrl` hands the
# address to the system browser rather than fetching anything itself.
#
# If Qt networking is ever introduced this has to be revisited, and the right
# answer then is to ship a known OpenSSL rather than to inherit a stranger's.
# Verify a build by opening the .exe and refreshing: a TLS failure here takes
# out every provider at once.
STRAY_OPENSSL = {"libcrypto-3-x64.dll", "libssl-3-x64.dll"}


def without_stray_openssl(binaries):
    """`binaries` minus anything in `STRAY_OPENSSL`, announcing each drop."""
    import pathlib

    kept = []
    for entry in binaries:
        if pathlib.PurePath(entry[0]).name.lower() in STRAY_OPENSSL:
            print(f"spec: dropping {entry[1]} - see build_excludes.STRAY_OPENSSL")
            continue
        kept.append(entry)
    return kept
