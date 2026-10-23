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
# release's package is downloaded first so a delta update is built. With PUBLISH_DIR=<dir>, the app already published
# there (deploy/publish-desktop.sh) is packaged and nothing is built: the release signs in a job that builds nothing.
#
# Signing, when configured (unsigned otherwise):
#   Windows (on Windows): AZURE_SIGN_METADATA=<metadata.json> for Azure Trusted Signing; the Azure credentials come
#     from the environment (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET).
#   macOS: APPLE_SIGN_APP_IDENTITY, APPLE_SIGN_INSTALL_IDENTITY, APPLE_NOTARY_PROFILE and APPLE_KEYCHAIN.
set -euo pipefail

rid="$1"
version="$2"
out="$(mkdir -p "$3" && cd "$3" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"
assets="$root/src/SemanticRouter.Desktop/Assets"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "${PUBLISH_DIR:-}" ]; then
  publish="$(cd "$PUBLISH_DIR" && pwd)"
else
  publish="$work/publish"
  "$root/deploy/publish-desktop.sh" "$rid" "$version" "$publish"
fi

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
  --packDir "$publish"
  --channel "$rid"
  --runtime "$rid"
  --outputDir "$out"
)

signing=()
case "$rid" in
  win-*)
    if [ -n "${AZURE_SIGN_METADATA:-}" ]; then
      signing=(--azureTrustedSignFile "$AZURE_SIGN_METADATA")
    fi
    ;;
  osx-*)
    # Our entitlements, not vpk's default set, which also allows DYLD_* variables and unsigned libraries
    # (docs/desktop.md#signing, checked by macos-entitlements-check.sh).
    if [ -n "${APPLE_SIGN_APP_IDENTITY:-}" ]; then
      signing=(--signAppIdentity "$APPLE_SIGN_APP_IDENTITY" --signInstallIdentity "$APPLE_SIGN_INSTALL_IDENTITY"
        --signEntitlements "$root/deploy/macos/SemanticRouter.entitlements"
        --notaryProfile "$APPLE_NOTARY_PROFILE" --keychain "$APPLE_KEYCHAIN")
    fi
    ;;
esac

case "$rid" in
  win-*)
    # A tray app: a Start menu entry, no desktop icon. No portable zip: the zip downloads (package-desktop.sh) stay.
    vpk ${directive[@]+"${directive[@]}"} pack "${common[@]}" --mainExe SemanticRouter.exe --icon "$assets/icon.ico" --shortcuts StartMenuRoot --noPortable \
      ${signing[@]+"${signing[@]}"}
    # Every file of the app that is not a program or library must be in its signed list (publish-desktop.sh), or
    # an updater with a publisher policy refuses the update (docs/enterprise.md#update-publisher).
    dotnet run --file "$root/deploy/update-file-list.cs" -- check "$out/SemanticRouter-$version-$rid-full.nupkg"
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
  <key>NSAppleEventsUsageDescription</key><string>SemanticRouter quits itself to install an update.</string>
</dict>
</plist>
PLIST
    # The plist names the bundle id and the icon, so the icon goes in under that name.
    cp "$assets/icon.icns" "$work/SemanticRouter.icns"
    vpk ${directive[@]+"${directive[@]}"} pack "${common[@]}" --mainExe SemanticRouter --icon "$work/SemanticRouter.icns" \
      --plist "$work/Info.plist" --noPortable ${signing[@]+"${signing[@]}"}
    ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

ls -1 "$out"
