#!/usr/bin/env bash
#
# Builds both macOS deliverables:
#
#   dist/AI Usage Monitor.app                    the app bundle
#   dist/AIUsageMonitor-<version>-<arch>.dmg     drag-to-Applications disk image
#
# The .app is ad-hoc signed (PyInstaller does this by default), which is all
# Apple Silicon needs to run it locally. It is NOT notarized, so a copy that
# travels to another Mac over the network arrives quarantined - see the note
# the script prints at the end.
#
# Usage:  ./build_mac.sh          [PYTHON=/path/to/python3.12 ./build_mac.sh]

set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="AI Usage Monitor"
APP_VERSION="1.1.0"
ARCH="$(uname -m)"
VENV=".venv"

say() { printf '\033[36m%s\033[0m\n' "$*"; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

[[ "$(uname -s)" == "Darwin" ]] || die "This script builds the macOS app; run build_ai_installer.ps1 on Windows."

# -- 1. an interpreter PySide6 will actually install into ---------------------
# PySide6 6.11 requires Python 3.10 or newer. macOS ships 3.9, so a build here
# fails at pip rather than at runtime unless a newer one is found first.
find_python() {
    if [[ -n "${PYTHON:-}" ]]; then echo "$PYTHON"; return; fi
    local candidate
    for candidate in python3.13 python3.12 python3.11 python3.10; do
        if command -v "$candidate" >/dev/null 2>&1; then
            command -v "$candidate"; return
        fi
    done
    for candidate in /Library/Frameworks/Python.framework/Versions/3.1[0-9]/bin/python3 \
                     /opt/homebrew/bin/python3.1[0-9]; do
        [[ -x "$candidate" ]] && { echo "$candidate"; return; }
    done
    # A bare python3 counts only if it is new enough.
    if command -v python3 >/dev/null 2>&1 &&
       python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)'; then
        command -v python3; return
    fi
    echo ""
}

if [[ ! -x "$VENV/bin/python" ]]; then
    PYTHON_BIN="$(find_python)"
    if [[ -z "$PYTHON_BIN" ]]; then
        die "No Python 3.10+ found, and PySide6 6.11 needs one (this Mac's /usr/bin/python3 is $(python3 -V 2>&1 | cut -d' ' -f2)).

Install one, then re-run this script:
  python.org   https://www.python.org/downloads/macos/   (universal2: also builds for Intel)
  Homebrew     brew install python@3.12
  uv           curl -LsSf https://astral.sh/uv/install.sh | sh && uv python install 3.12

Or point this script at an interpreter you already have:
  PYTHON=/path/to/python3.12 ./build_mac.sh"
    fi
    say "Creating virtual environment with $PYTHON_BIN ($("$PYTHON_BIN" -V 2>&1))..."
    "$PYTHON_BIN" -m venv "$VENV"
    "$VENV/bin/python" -m pip install --upgrade pip --quiet
    "$VENV/bin/python" -m pip install -r requirements.txt
fi

PY="$VENV/bin/python"
"$PY" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' \
    || die "$VENV holds Python $("$PY" -V 2>&1 | cut -d' ' -f2); delete it and re-run to rebuild with 3.10+."

# -- 2. icons ----------------------------------------------------------------
say "Generating application icons..."
"$PY" tools/make_icon.py
[[ -f assets/icon.icns ]] || die "assets/icon.icns was not produced; the bundle has no icon to use."

# -- 3. the bundle -----------------------------------------------------------
# A running copy holds its single-instance socket, not the build tree, so
# there is nothing to stop and nothing to retry around.
rm -rf build "dist/$APP_NAME.app" dist/AIUsageMonitor

say "Building $APP_NAME.app for $ARCH (this takes a minute)..."
"$PY" -m PyInstaller AIUsageMonitor-mac.spec --noconfirm

APP="dist/$APP_NAME.app"
[[ -d "$APP" ]] || die "PyInstaller finished but $APP was not produced."

# Apple Silicon refuses to execute an unsigned binary. PyInstaller ad-hoc signs
# the bundle it builds; this re-signs only if that somehow did not take.
if ! codesign --verify --deep --strict "$APP" 2>/dev/null; then
    say "Ad-hoc signing the bundle..."
    codesign --force --deep --sign - "$APP"
    codesign --verify --deep --strict "$APP" || die "The bundle will not verify; macOS will refuse to open it."
fi

# -- 4. the disk image -------------------------------------------------------
DMG="dist/AIUsageMonitor-$APP_VERSION-$ARCH.dmg"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

say "Building $DMG..."
cp -R "$APP" "$STAGE/"
# The Applications symlink is what makes the window a drag-to-install target.
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create \
    -volname "$APP_NAME" \
    -srcfolder "$STAGE" \
    -ov -format UDZO \
    -quiet \
    "$DMG"

# -- 5. what you got ---------------------------------------------------------
echo
ok "App  $PWD/$APP ($(du -sh "$APP" | cut -f1))"
ok "DMG  $PWD/$DMG ($(du -sh "$DMG" | cut -f1))"
echo
echo "Open the DMG and drag the app to Applications."
echo "Ad-hoc signed, not notarized: it runs on this Mac, but a copy downloaded"
echo "onto another one needs a right-click -> Open the first time."
