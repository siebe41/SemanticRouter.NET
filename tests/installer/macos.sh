#!/usr/bin/env bash
# Exercises the macOS installer (#63): installs the 1.0.0 .pkg for the current user (into ~/Applications, no admin),
# updates it to 1.0.1 from a local feed, and checks the app bundle is the new version.
#
#   tests/installer/macos.sh <SemanticRouter-1.0.0.pkg> <feed directory with 1.0.1>
set -euo pipefail

pkg="$1"
feed="$(cd "$2" && pwd)"
app="$HOME/Applications/SemanticRouter.app"
exe="$app/Contents/MacOS/SemanticRouter"
config="$(mktemp -d)"
version() { /usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null || true; }

echo "::group::Install 1.0.0"
installer -pkg "$pkg" -target CurrentUserHomeDirectory
[ -x "$exe" ] || { echo "::error::Not installed at $app"; ls -la "$HOME/Applications" /Applications | head -40; exit 1; }
[ "$(version)" = "1.0.0" ] || { echo "::error::Installed version is '$(version)', expected 1.0.0"; exit 1; }
# The installer starts the app (the menu-bar icon) after installing; this test drives it from the command line.
sleep 5
pkill -f "$exe" || true
echo "::endgroup::"

echo "::group::Update to 1.0.1"
out="$(SEMANTICROUTER_UPDATE_SOURCE="$feed" "$exe" --update --config-dir "$config" 2>&1 | grep -v '^\[' || true)"
echo "$out"
grep -q "Updating SemanticRouter 1.0.0 to 1.0.1" <<<"$out" || { echo "::error::--update did not start the update"; exit 1; }
for _ in $(seq 1 90); do
  [ "$(version)" = "1.0.1" ] && break
  sleep 1
done
[ "$(version)" = "1.0.1" ] || { echo "::error::The app is '$(version)' after the update, expected 1.0.1"; tail -40 "$HOME/Library/Logs/velopack_SemanticRouter.log" /tmp/velopack_SemanticRouter.log 2>/dev/null; exit 1; }
echo "::endgroup::"

echo "::group::The new version"
out="$(SEMANTICROUTER_UPDATE_SOURCE="$feed" "$exe" --update --config-dir "$config" 2>&1 | grep -v '^\[' || true)"
echo "$out"
grep -q "SemanticRouter 1.0.1 is up to date" <<<"$out" || { echo "::error::The updated app does not report 1.0.1"; exit 1; }
echo "::endgroup::"
echo "Installed 1.0.0 into ~/Applications and updated it to 1.0.1."
