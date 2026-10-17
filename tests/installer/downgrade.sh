#!/usr/bin/env bash
# The way back after an upgrade (#205, #206), as docs/host.md "Upgrading and going back" describes it:
#   1. the current build takes over a home the newest release set up and used (its upgrade fixture);
#   2. the newest release from nuget.org, started on that migrated home, refuses it with a clear message (exit code 2)
#      or reports the state database as unusable (releases up to 0.5.0) - it never quietly uses it;
#   3. with the copy the upgrade kept moved back into place, that release starts with the home's usage.
# When the current build reads the same schema as that release, nothing is migrated and step 2 is that the release
# simply keeps working with the home's usage.
#
#   tests/installer/downgrade.sh <current semantic-router.dll>
#
# Needs network access (nuget.org). Runs with its own HOME, XDG_* and SEMANTIC_ROUTER_HOME.
set -euo pipefail

current="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
root="$(cd "$(dirname "$0")/../.." && pwd)"
tools="$root/tools/upgrade-fixtures"
work="$(mktemp -d)"
router_pid=""
cleanup() { [ -n "$router_pid" ] && kill "$router_pid" 2>/dev/null; rm -rf "$work"; }
trap cleanup EXIT
home="$work/home"
config="$home/sr"
port=5293
export HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" SEMANTIC_ROUTER_HOME="$config" \
  SEMANTIC_ROUTER_KEYS_DIR="$config/keys" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$home"

newest="$(ls "$root/tests/fixtures/upgrade"/*.zip | sort -V | tail -n 1)"
usage="$(python3 "$tools/seed-home.py" "$newest" "$config")"
schema() { python3 -c 'import sqlite3, sys; print(sqlite3.connect(sys.argv[1]).execute("SELECT value FROM meta WHERE key = \"schema_version\"").fetchone()[0])' "$1"; }
before="$(schema "$config/semantic-router.db")"
echo "Seeded a home from $(basename "$newest") ($usage usage records, state schema $before)."

# Starts a router in the background on the home and waits until it answers, or until it exits.
start() {
  "$@" --urls "http://127.0.0.1:$port" >"$work/router.log" 2>&1 &
  router_pid=$!
  for _ in $(seq 1 90); do
    curl -fs -o /dev/null "http://127.0.0.1:$port/router/api/status" && return 0
    kill -0 "$router_pid" 2>/dev/null || return 1
    sleep 1
  done
  return 1
}
stop() { kill "$router_pid" 2>/dev/null || true; wait "$router_pid" 2>/dev/null || true; router_pid=""; }

echo "::group::The current build takes over the home"
start dotnet "$current" || { cat "$work/router.log"; echo "::error::The current build did not start on the home"; exit 1; }
python3 "$tools/check-usage.py" "http://127.0.0.1:$port" "$usage" "the current build" || exit 1
stop
after="$(schema "$config/semantic-router.db")"
echo "State schema $before -> $after"
echo "::endgroup::"

echo "::group::The newest published release"
dotnet tool install SemanticRouter.Host --tool-path "$work/release" >/dev/null
release="$work/release/semantic-router"
version="$("$release" --version)"
echo "SemanticRouter $version from nuget.org"
echo "::endgroup::"

if [ "$after" = "$before" ]; then
  echo "::group::$version on the home (same schema)"
  start "$release" || { cat "$work/router.log"; echo "::error::$version did not start on the home"; exit 1; }
  python3 "$tools/check-usage.py" "http://127.0.0.1:$port" "$usage" "$version on the home" || exit 1
  stop
  echo "::endgroup::"
  echo "Nothing was migrated: $version keeps working on the home."
  exit 0
fi

backup="$config/semantic-router.db.v$before.bak"
[ -f "$backup" ] || { echo "::error::The upgrade from schema $before left no copy at $backup"; ls -la "$config"; exit 1; }

echo "::group::$version on the migrated home"
if start "$release"; then
  # Releases up to 0.5.0 do not refuse a newer database: they must at least say they cannot use it.
  sleep 2
  grep -q "cannot be used" "$work/router.log" || { cat "$work/router.log"; echo "::error::$version used a schema-$after database without a word"; exit 1; }
  echo "$version runs on the migrated home with its state in memory, and says so."
  stop
else
  code=0
  wait "$router_pid" || code=$?
  router_pid=""
  cat "$work/router.log"
  [ "$code" = 2 ] && grep -q "written by a newer SemanticRouter" "$work/router.log" \
    || { echo "::error::$version neither started nor refused the newer database clearly (exit code $code)"; exit 1; }
  echo "$version refuses the migrated home, naming the copy to restore."
fi
[ "$(schema "$config/semantic-router.db")" = "$after" ] || { echo "::error::$version changed the migrated database"; exit 1; }
echo "::endgroup::"

echo "::group::Going back: the copy from before the upgrade"
mv "$config/semantic-router.db" "$work/migrated.db"
rm -f "$config/semantic-router.db-wal" "$config/semantic-router.db-shm"
mv "$backup" "$config/semantic-router.db"
start "$release" || { cat "$work/router.log"; echo "::error::$version did not start on the restored copy"; exit 1; }
python3 "$tools/check-usage.py" "http://127.0.0.1:$port" "$usage" "$version on the restored copy" || { cat "$work/router.log"; exit 1; }
stop
echo "::endgroup::"
echo "Downgraded to $version: refused or reported the migrated database, and started with the home's usage from the copy the upgrade kept."
