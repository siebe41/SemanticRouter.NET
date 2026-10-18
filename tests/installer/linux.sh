#!/usr/bin/env bash
# Exercises the Linux AppImage (#63): runs 1.0.0 on a home the newest release set up and used (its upgrade fixture,
# #206), updates it to 1.0.1 from a local feed, and checks the updated AppImage reports itself up to date and still has
# that home's usage.
#
#   tests/installer/linux.sh <SemanticRouter-1.0.0.AppImage> <feed directory with 1.0.1>
#
# Runs without FUSE (APPIMAGE_EXTRACT_AND_RUN), as on CI machines and containers.
set -euo pipefail

source="$1"
feed="$(cd "$2" && pwd)"
work="$(mktemp -d)"
policy=/etc/semantic-router/policy.json
trap 'rm -rf "$work"; sudo rm -f "$policy"' EXIT
app="$work/SemanticRouter.AppImage"
config="$work/config"
cp "$source" "$app"
chmod +x "$app"
# A release build takes SEMANTICROUTER_UPDATE_SOURCE only when the machine policy allows a custom feed.
sudo mkdir -p "$(dirname "$policy")"
echo '{ "Updates": { "AllowCustomFeed": true } }' | sudo tee "$policy" >/dev/null
export APPIMAGE_EXTRACT_AND_RUN=1 SEMANTICROUTER_UPDATE_SOURCE="$feed" SEMANTIC_ROUTER_KEYS_DIR="$config/keys"

run() { "$app" "$@" --config-dir "$config" 2>&1 | grep -v '^\[' || true; }
fixtures="$(cd "$(dirname "$0")/../fixtures/upgrade" && pwd)"
tools="$(cd "$(dirname "$0")/../../tools/upgrade-fixtures" && pwd)"
# Runs the router headless on the home until the usage check is done.
check_usage() {
  "$app" --headless --config-dir "$config" --urls http://127.0.0.1:5292 >"$work/router.log" 2>&1 &
  local router=$!
  python3 "$tools/check-usage.py" http://127.0.0.1:5292 "$usage" "$1" || { tail -40 "$work/router.log"; kill "$router"; exit 1; }
  kill "$router"
  wait "$router" 2>/dev/null || true
}

echo "::group::A home from the newest release"
newest="$(ls "$fixtures"/*.zip | sort -V | tail -n 1)"
usage="$(python3 "$tools/seed-home.py" "$newest" "$config")"
echo "Seeded $config from $(basename "$newest") ($usage usage records)"
check_usage "1.0.0 on the $(basename "$newest" .zip) home"
echo "::endgroup::"

echo "::group::Update 1.0.0 to 1.0.1"
before="$(sha256sum "$app" | cut -d' ' -f1)"
out="$(run --update)"
echo "$out"
grep -q "Updating SemanticRouter 1.0.0 to 1.0.1" <<<"$out" || { echo "::error::--update did not start the update"; exit 1; }
for _ in $(seq 1 60); do
  [ "$(sha256sum "$app" | cut -d' ' -f1)" != "$before" ] && break
  sleep 1
done
[ "$(sha256sum "$app" | cut -d' ' -f1)" != "$before" ] || { echo "::error::The AppImage was not replaced"; cat /tmp/velopack_SemanticRouter.log 2>/dev/null | tail -40; exit 1; }
echo "::endgroup::"

echo "::group::The new version"
sleep 2
out="$(run --update)"
echo "$out"
grep -q "SemanticRouter 1.0.1 is up to date" <<<"$out" || { echo "::error::The updated AppImage is not 1.0.1"; exit 1; }
check_usage "1.0.1 after the update"
echo "::endgroup::"
echo "Updated the AppImage from 1.0.0 to 1.0.1, keeping the home's usage."
