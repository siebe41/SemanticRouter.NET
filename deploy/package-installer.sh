#!/usr/bin/env bash
# Builds the desktop app's installer for one platform with Velopack (https://velopack.io):
#
#   deploy/package-installer.sh <rid> <version> <output-dir>
#   deploy/package-installer.sh win-x64 0.3.0 installers
#
# win-*   SemanticRouter-<rid>-Setup.exe: a per-user install into %LocalAppData%\SemanticRouter (no admin)
# osx-*   SemanticRouter-<rid>-Setup.pkg: must be built on macOS
# linux-* SemanticRouter.AppImage (named per channel by Velopack)
#
# Plus the update feed for the channel (<rid>): releases.<rid>.json and the full (and, with a previous release, delta)
# packages the installed app updates from. Windows and Linux packages can be built on any OS; macOS needs macOS.
#
# Needs `vpk` (dotnet tool install -g vpk). With VPK_PREVIOUS_FROM=<repository url> (and GITHUB_TOKEN), the previous
# release's package is downloaded first so a delta update is built.
set -euo pipefail

rid="$1"
version="$2"
out="$(mkdir -p "$3" && cd "$3" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"
assets="$root/src/SemanticRouter.Desktop/Assets"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet publish "$root/src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none \
  --output "$work/publish"

# Cross-packaging needs a directive naming the target; packaging for the OS vpk runs on needs none.
case "$(uname -s)" in
  Linux) host=linux ;;
  Darwin) host=osx ;;
  *) host=win ;;
esac
target="${rid%%-*}"
directive=()
if [ "$target" != "$host" ]; then
  directive=("[$target]")
fi

if [ -n "${VPK_PREVIOUS_FROM:-}" ]; then
  vpk ${directive[@]+"${directive[@]}"} download github --repoUrl "$VPK_PREVIOUS_FROM" --token "${GITHUB_TOKEN:-}" --channel "$rid" --outputDir "$out" \
    || echo "No earlier release on channel $rid: no delta package."
fi

common=(
  --packId SemanticRouter
  --packVersion "$version"
  --packTitle SemanticRouter
  --packAuthors "SemanticRouter contributors"
  --packDir "$work/publish"
  --channel "$rid"
  --runtime "$rid"
  --outputDir "$out"
)

case "$rid" in
  win-*)
    # A tray app: a Start menu entry, no desktop icon. No portable zip: the zip downloads (package-desktop.sh) stay.
    vpk ${directive[@]+"${directive[@]}"} pack "${common[@]}" --mainExe SemanticRouter.exe --icon "$assets/icon.ico" --shortcuts StartMenuRoot --noPortable
    ;;
  linux-*)
    vpk ${directive[@]+"${directive[@]}"} pack "${common[@]}" --mainExe SemanticRouter --icon "$assets/icon.png" --categories "Network;Development;"
    ;;
  osx-*)
    # LSUIElement: a menu-bar app, no Dock icon.
    cat > "$work/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SemanticRouter</string>
  <key>CFBundleDisplayName</key><string>SemanticRouter</string>
  <key>CFBundleIdentifier</key><string>io.github.siebe41.semantic-router</string>
  <key>CFBundleExecutable</key><string>SemanticRouter</string>
  <key>CFBundleIconFile</key><string>SemanticRouter.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
    vpk ${directive[@]+"${directive[@]}"} pack "${common[@]}" --mainExe SemanticRouter --icon "$assets/icon.icns" \
      --bundleId io.github.siebe41.semantic-router --plist "$work/Info.plist" --noPortable
    ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

ls -1 "$out"
