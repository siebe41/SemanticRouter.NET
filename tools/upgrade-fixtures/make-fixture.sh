#!/usr/bin/env bash
# Makes the upgrade-test fixture for one released version: a configuration directory that the release set up and used
# through its own API (drive.py, against stub-openai.py), zipped as tests/fixtures/upgrade/<version>.zip. The upgrade
# tests (UpgradeFromReleasedVersionsTests) start the current build on each one.
#
#   tools/upgrade-fixtures/make-fixture.sh 0.5.0
#
# Versions on nuget.org are installed as a dotnet tool; older tags (0.1.0, 0.2.0) are built from the tag, which needs
# their SDK (.NET 9). Releases up to 0.2.1 need the .NET 9 runtime. Everything runs with its own HOME, XDG_* and
# SEMANTIC_ROUTER_HOME, so nothing of yours is read or written. The key ring in the zip holds a throwaway key that
# protects only the fixture's fake API key.
set -euo pipefail

version="${1:?usage: make-fixture.sh <version> [output.zip]}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${2:-$repo/tests/fixtures/upgrade/$version.zip}"
work="$(mktemp -d)"
router_pid=""
stub_pid=""
cleanup() {
  [ -n "$router_pid" ] && kill "$router_pid" 2>/dev/null || true
  [ -n "$stub_pid" ] && kill "$stub_pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# The release: from nuget.org, or built from its tag.
if dotnet tool install SemanticRouter.Host --version "$version" --tool-path "$work/tool" >"$work/install.log" 2>&1; then
  router=("$work/tool/semantic-router")
else
  echo "SemanticRouter.Host $version is not on nuget.org; building v$version from its tag."
  mkdir -p "$work/src"
  git -C "$repo" archive "v$version" | tar -x -C "$work/src"
  (cd "$work/src" && dotnet publish src/SemanticRouter.Host -c Release -o "$work/app" -p:Version="$version" >"$work/build.log" 2>&1) \
    || { tail -n 30 "$work/build.log"; exit 1; }
  router=(dotnet "$work/app/semantic-router.dll")
fi

free_port() { python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'; }
router_port="$(free_port)"
stub_port="$(free_port)"
home="$work/home"
config="$home/sr"
mkdir -p "$config"
isolated=(env HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" XDG_CACHE_HOME="$home/.cache"
  SEMANTIC_ROUTER_HOME="$config" SEMANTIC_ROUTER_KEYS_DIR="$config/keys")

python3 "$here/stub-openai.py" "$stub_port" "$work/authorization.log" &
stub_pid=$!

"${isolated[@]}" "${router[@]}" init --no-detect --no-cli
"${isolated[@]}" "${router[@]}" --urls "http://127.0.0.1:$router_port" >"$work/router.log" 2>&1 &
router_pid=$!
for _ in $(seq 1 120); do
  curl -fs -o /dev/null "http://127.0.0.1:$router_port/router/api/status" && break
  kill -0 "$router_pid" 2>/dev/null || { cat "$work/router.log"; exit 1; }
  sleep 0.5
done

python3 "$here/drive.py" "http://127.0.0.1:$router_port" "http://127.0.0.1:$stub_port" "$version" "$config"
grep -q "Bearer sk-fixture-$version" "$work/authorization.log" \
  || { echo "The stub never saw the fixture's API key."; cat "$work/authorization.log"; exit 1; }

kill -TERM "$router_pid"
wait "$router_pid" || true
router_pid=""
rm -f "$config/semantic-router.instance.json"

mkdir -p "$(dirname "$out")"
python3 - "$config" "$out" <<'PY'
import os, sys, zipfile
source, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(source):
        for name in sorted(files):
            path = os.path.join(root, name)
            archive.write(path, os.path.relpath(path, source))
PY
echo "Wrote $out"
