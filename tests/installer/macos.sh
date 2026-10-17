#!/usr/bin/env bash
# Exercises the macOS installer (#63): installs the 1.0.0 .pkg for the current user (into ~/Applications, no admin),
# sets up the background service with the real launchctl, starts the app and hands a second launch over to it, updates
# to 1.0.1 from a local feed with the service running, checks the service runs the new version, and removes it (#212).
#
#   tests/installer/macos.sh <SemanticRouter-1.0.0.pkg> <feed directory with 1.0.1>
set -euo pipefail

pkg="$1"
feed="$(cd "$2" && pwd)"
app="$HOME/Applications/SemanticRouter.app"
exe="$app/Contents/MacOS/SemanticRouter"
config="$(mktemp -d)"
version() { /usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null || true; }
label="io.github.siebe41.semantic-router"
plist="$HOME/Library/LaunchAgents/$label.plist"
instance="$config/semantic-router.instance.json"

# Waits up to 90 s for a command to succeed.
wait_for() {
  local what="$1"
  shift
  for _ in $(seq 1 90); do
    "$@" && return 0
    sleep 1
  done
  echo "::error::Timed out waiting for $what"
  return 1
}
service_running() { launchctl print "gui/$(id -u)/$label" 2>/dev/null | grep -q "state = running"; }
service_gone() { ! launchctl print "gui/$(id -u)/$label" >/dev/null 2>&1; }
# The version of the router recorded on a configuration directory (semantic-router.instance.json), if one runs.
router_version() { sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "${1:-$instance}" 2>/dev/null || true; }
service_on() { service_running && [ "$(router_version)" = "$1" ]; }
logs() {
  tail -40 "$HOME/Library/Logs/semantic-router/semantic-router.log" 2>/dev/null || true
  launchctl print "gui/$(id -u)/$label" 2>&1 | head -40 || true
}

# A release build takes SEMANTICROUTER_UPDATE_SOURCE only when the machine policy allows a custom feed.
policy="/Library/Managed Preferences/io.github.siebe41.semantic-router.plist"
trap 'sudo rm -f "$policy"' EXIT
sudo mkdir -p "$(dirname "$policy")"
sudo tee "$policy" >/dev/null <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>Updates</key><dict><key>AllowCustomFeed</key><true/></dict></dict></plist>
PLIST
sudo chmod 644 "$policy"

echo "::group::Install 1.0.0"
installer -pkg "$pkg" -target CurrentUserHomeDirectory
[ -x "$exe" ] || { echo "::error::Not installed at $app"; ls -la "$HOME/Applications" /Applications | head -40; exit 1; }
[ "$(version)" = "1.0.0" ] || { echo "::error::Installed version is '$(version)', expected 1.0.0"; exit 1; }
# The installer starts the app (the menu-bar icon) after installing; this test drives it from the command line.
sleep 5
pkill -f "$exe" || true
echo "::endgroup::"

echo "::group::Background service"
"$exe" --install-service --config-dir "$config"
[ -f "$plist" ] || { echo "::error::No launchd agent at $plist"; exit 1; }
wait_for "the background service to run 1.0.0" service_on 1.0.0 || { logs; exit 1; }
grep -q "<string>$exe</string>" "$plist" || { echo "::error::The agent does not run $exe"; cat "$plist"; exit 1; }
echo "launchd runs $label, which answers as 1.0.0"
echo "::endgroup::"

echo "::group::The app starts in the background, takes a second launch and quits"
other="$(mktemp -d)"
"$exe" --background --config-dir "$other" --urls http://127.0.0.1:5291 &
app_pid=$!
wait_for "the app's router to listen" test -f "$other/semantic-router.instance.json" || { tail -40 "$other/logs/semantic-router.log" 2>/dev/null; exit 1; }
"$exe" --background --config-dir "$other" --urls http://127.0.0.1:5291 &
second=$!
for _ in $(seq 1 20); do kill -0 "$second" 2>/dev/null || break; sleep 1; done
if kill -0 "$second" 2>/dev/null; then kill "$second"; echo "::error::A second launch did not hand over to the running app"; exit 1; fi
wait "$second" || { echo "::error::The second launch exited with $?"; exit 1; }
"$exe" --quit --config-dir "$other"
for _ in $(seq 1 30); do kill -0 "$app_pid" 2>/dev/null || break; sleep 1; done
if kill -0 "$app_pid" 2>/dev/null; then kill "$app_pid"; echo "::error::--quit did not stop the running app"; exit 1; fi
[ ! -f "$other/semantic-router.instance.json" ] || { echo "::error::The app quit but left its router's instance file"; exit 1; }
echo "Started, handed a second launch over, and quit."
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
# The update stopped the service; the new version installed and started it again.
wait_for "the background service to run 1.0.1 after the update" service_on 1.0.1 || { logs; exit 1; }
echo "::endgroup::"

echo "::group::The new version"
out="$(SEMANTICROUTER_UPDATE_SOURCE="$feed" "$exe" --update --config-dir "$config" 2>&1 | grep -v '^\[' || true)"
echo "$out"
grep -q "SemanticRouter 1.0.1 is up to date" <<<"$out" || { echo "::error::The updated app does not report 1.0.1"; exit 1; }
echo "::endgroup::"

echo "::group::Remove the background service"
"$exe" --uninstall-service --config-dir "$config"
[ ! -f "$plist" ] || { echo "::error::$plist is still there"; exit 1; }
wait_for "launchd to forget $label" service_gone || exit 1
echo "::endgroup::"
echo "Installed 1.0.0 into ~/Applications, ran its service under launchd, updated it to 1.0.1 with the service running, and removed the service."
