#!/bin/bash
# Builds "AI Usage Monitor.app" for macOS arm64 from the Avalonia presentation project.
#
# One directory, never a single file: a one-file bundle unpacks itself into a temporary
# directory on every launch, and an app that opens at login pays that cost every morning.
#
# The bundle is signed ad-hoc (`codesign -s -`), which is the minimum an arm64 binary needs in
# order to run at all. It is NOT signed with a Developer ID and NOT notarized, so macOS will warn
# the first time it is opened from the Finder. R-014 covers neither.
#
# Usage: scripts/build_macos_app.sh [output-directory]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/artifacts/macos}"
project="$root/src/AIMonitor.Presentation.Avalonia/AIMonitor.Presentation.Avalonia.csproj"
app="$out/AI Usage Monitor.app"
runtime="osx-arm64"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This builds a macOS bundle and has to run on macOS." >&2
  exit 1
fi

version="$(sed -n 's|.*<VersionPrefix>\(.*\)</VersionPrefix>.*|\1|p' "$root/Directory.Build.props" | head -1)"
if [[ -z "$version" ]]; then
  echo "Could not read VersionPrefix from Directory.Build.props." >&2
  exit 1
fi

echo "==> Publishing $runtime, self-contained, version $version"
publish="$out/publish"
rm -rf "$publish" "$app"
mkdir -p "$publish"
dotnet publish "$project" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  --output "$publish" \
  -p:PublishSingleFile=false \
  --nologo

echo "==> Assembling the bundle"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
mv "$app/Contents/MacOS/AIMonitor.Presentation.Avalonia" "$app/Contents/MacOS/AIUsageMonitor"

sed "s|__VERSION__|$version|g" "$root/build/macos/Info.plist.template" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist" > /dev/null

# GPL-3.0 does not care which platform the binary is for: the licence and the notices travel with
# it. Beside the executable is where the About page reads them from; Resources is where a person
# poking at the bundle in the Finder will look.
for doc in LICENSE THIRD-PARTY-NOTICES.md; do
  cp "$root/$doc" "$app/Contents/MacOS/$doc"
  cp "$root/$doc" "$app/Contents/Resources/$doc"
done

echo "==> Signing ad-hoc (not Developer ID, not notarized)"
codesign --force --deep --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"

echo
echo "Built: $app"
du -sh "$app" | awk '{print "Size:  " $1}'
