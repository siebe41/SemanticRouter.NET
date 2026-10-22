#!/usr/bin/env bash
# Whether the entitlements the macOS app is signed with (deploy/macos/SemanticRouter.entitlements, passed to vpk by
# package-installer.sh) are exactly the ones docs/desktop.md#signing explains, for CI:
#
# - com.apple.security.cs.allow-jit and com.apple.security.cs.allow-unsigned-executable-memory: the .NET runtime's JIT
#   under the hardened runtime;
# - com.apple.security.automation.apple-events: Velopack's updater quits the app with osascript before an update.
#   macOS asks the user first, which takes the NSAppleEventsUsageDescription in the Info.plist package-installer.sh
#   writes; without it the event is denied with no prompt.
#
# vpk signs the whole bundle with --deep, so these apply to every executable and library in it. Anything else, such as
# allow-dyld-environment-variables or disable-library-validation from vpk's default set, fails the check: adding one
# means changing this list and the docs in the same pull request.
#
# CI packs unsigned, so it never runs the signing path itself. The check also reads package-installer.sh for the
# --signEntitlements argument and the usage string, so that dropping either fails here rather than in the first signed
# release.
#
#   deploy/macos-entitlements-check.sh [<entitlements file>] [<package-installer.sh>]
#
# Prints what is wrong as ::error:: annotations and exits 1 when anything is. With plutil (macOS), the file is also
# checked to be a valid property list, and read in plutil's normalised form.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
file="${1:-$root/deploy/macos/SemanticRouter.entitlements}"
installer="${2:-$root/deploy/package-installer.sh}"
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

# plutil rewrites the file without comments or formatting. Without it, comments are removed here: a value after a
# comment, as in <key>…</key><!-- x --><true/>, must not hide its key.
if command -v plutil >/dev/null; then
  if ! plutil -lint "$file"; then
    wrong "$file is not a valid property list"
  fi
  plist="$(plutil -convert xml1 -o - "$file" 2>/dev/null || cat "$file")"
else
  plist="$(cat "$file")"
fi

# Each entry as key=value, with the whitespace between the tags removed: com.apple.security.cs.allow-jit=true
entries="$(tr -d ' \t\r\n' <<<"$plist" | sed -E 's/<!--([^-]|-[^-])*-->//g' | grep -o '<key>[^<]*</key><[a-z]*/\{0,1\}>' |
  sed -E 's|<key>([^<]*)</key><([a-z]*)/?>|\1=\2|' || true)"

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

if [ ! -f "$installer" ]; then
  wrong "No $installer to check the signing arguments in"
else
  if ! grep -qF -- '--signEntitlements "$root/deploy/macos/SemanticRouter.entitlements"' "$installer"; then
    wrong "$installer does not pass --signEntitlements \"\$root/deploy/macos/SemanticRouter.entitlements\" to vpk, so a signed release would get vpk's default entitlements"
  fi
  if ! grep -q '<key>NSAppleEventsUsageDescription</key><string>[^<]' "$installer"; then
    wrong "$installer writes no NSAppleEventsUsageDescription into the Info.plist, so macOS denies the updater's Apple Event without asking"
  fi
fi

if [ "$failed" = true ]; then
  exit 1
fi
echo "The macOS entitlements are the documented set: ${expected[*]}"
