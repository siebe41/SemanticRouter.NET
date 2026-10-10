#!/usr/bin/env bash
# Exercises the Linux AppImage (#63): runs 1.0.0, updates it to 1.0.1 from a local feed, and checks the updated
# AppImage reports itself up to date.
#
#   tests/installer/linux.sh <SemanticRouter-1.0.0.AppImage> <feed directory with 1.0.1>
#
# Runs without FUSE (APPIMAGE_EXTRACT_AND_RUN), as on CI machines and containers.
set -euo pipefail

source="$1"
feed="$(cd "$2" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
app="$work/SemanticRouter.AppImage"
config="$work/config"
cp "$source" "$app"
chmod +x "$app"
export APPIMAGE_EXTRACT_AND_RUN=1 SEMANTICROUTER_UPDATE_SOURCE="$feed"

run() { "$app" "$@" --config-dir "$config" 2>&1 | grep -v '^\[' || true; }

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
echo "::endgroup::"
echo "Updated the AppImage from 1.0.0 to 1.0.1."
