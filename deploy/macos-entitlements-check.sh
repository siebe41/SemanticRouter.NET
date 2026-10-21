#!/usr/bin/env bash
# Whether the entitlements the macOS app is signed with (deploy/macos/SemanticRouter.entitlements, passed to vpk by
# package-installer.sh) are exactly the ones docs/desktop.md#signing explains, for CI:
#
# - com.apple.security.cs.allow-jit and com.apple.security.cs.allow-unsigned-executable-memory: the .NET runtime's JIT
#   under the hardened runtime;
# - com.apple.security.automation.apple-events: Velopack's updater quits the app with osascript before an update.
#
# vpk signs the whole bundle with --deep, so these apply to every executable and library in it. Anything else, such as
# allow-dyld-environment-variables or disable-library-validation from vpk's default set, fails the check: adding one
# means changing this list and the docs in the same pull request.
#
#   deploy/macos-entitlements-check.sh [<entitlements file>]
#
# Prints what is wrong as ::error:: annotations and exits 1 when anything is. With plutil (macOS), the file is also
# checked to be a valid property list.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
file="${1:-$root/deploy/macos/SemanticRouter.entitlements}"
expected=(
  com.apple.security.automation.apple-events
  com.apple.security.cs.allow-jit
  com.apple.security.cs.allow-unsigned-executable-memory
)
failed=false

wrong() {
  echo "::error::$1"
  failed=true
}

if [ ! -f "$file" ]; then
  echo "::error::No entitlements file at $file"
  exit 1
fi

if command -v plutil >/dev/null && ! plutil -lint "$file"; then
  wrong "$file is not a valid property list"
fi

# Each entry as key=value, with the whitespace between the tags removed: com.apple.security.cs.allow-jit=true
entries="$(tr -d ' \t\r\n' <"$file" | grep -o '<key>[^<]*</key><[a-z]*/\{0,1\}>' | sed -E 's|<key>([^<]*)</key><([a-z]*)/?>|\1=\2|' || true)"

for entry in $entries; do
  key="${entry%%=*}"
  value="${entry#*=}"
  if [[ " ${expected[*]} " != *" $key "* ]]; then
    wrong "$file grants $key, which docs/desktop.md#signing does not explain; see deploy/macos-entitlements-check.sh"
  elif [ "$value" != true ]; then
    wrong "$file sets $key to <$value>: list only what the app is granted, as <true/>"
  fi
done

for key in "${expected[@]}"; do
  if ! grep -qx "$key=true" <<<"$entries"; then
    wrong "$file does not grant $key, which the app needs (docs/desktop.md#signing)"
  fi
done

if [ "$failed" = true ]; then
  exit 1
fi
echo "The macOS entitlements are the documented set: ${expected[*]}"
