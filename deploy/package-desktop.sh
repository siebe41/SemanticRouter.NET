#!/usr/bin/env bash
# Builds the desktop app for one platform as a self-contained archive (no .NET install needed to run it).
#
#   deploy/package-desktop.sh <rid> <version> <output-dir>
#   deploy/package-desktop.sh osx-arm64 0.2.0 artifacts
#
# win-*   SemanticRouter-<version>-<rid>.zip with SemanticRouter.exe
# linux-* SemanticRouter-<version>-<rid>.tar.gz with SemanticRouter and semantic-router.png
# osx-*   SemanticRouter-<version>-<rid>.zip with SemanticRouter.app (unsigned: see docs/desktop.md)
set -euo pipefail

rid="$1"
version="$2"
out="$(mkdir -p "$3" && cd "$3" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet publish "$root/src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none \
  --output "$work/publish"

name="SemanticRouter-$version-$rid"
case "$rid" in
  win-*)
    (cd "$work/publish" && zip -qr "$out/$name.zip" .)
    ;;
  linux-*)
    mv "$work/publish" "$work/SemanticRouter"
    tar -C "$work" -czf "$out/$name.tar.gz" SemanticRouter
    ;;
  osx-*)
    app="$work/SemanticRouter.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$work/publish/." "$app/Contents/MacOS/"
    cp "$root/src/SemanticRouter.Desktop/Assets/icon.icns" "$app/Contents/Resources/SemanticRouter.icns"
    # LSUIElement: a menu-bar app, no Dock icon.
    cat > "$app/Contents/Info.plist" <<PLIST
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
    chmod +x "$app/Contents/MacOS/SemanticRouter"
    (cd "$work" && zip -qry "$out/$name.zip" SemanticRouter.app)
    ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

echo "$out/$name"*
