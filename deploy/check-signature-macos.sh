#!/usr/bin/env bash
# Checks the signature and the stapled notarization ticket of each macOS installer (*-Setup.pkg) in the directories
# given, for the release's installer job and the signing dry run (docs/desktop.md#signing), so that both check the same
# way:
#
#   deploy/check-signature-macos.sh [--required] <installer-dir>...
#
# With --required, fails unless every installer is signed (pkgutil --check-signature) and has a valid notarization
# ticket stapled to it (xcrun stapler validate); without it, an unsigned installer is only reported as such. Fails when
# a directory holds no installer. Adds a line per installer to $GITHUB_STEP_SUMMARY when that is set. macOS only.
set -euo pipefail
shopt -s nullglob

required=false
if [ "${1:-}" = --required ]; then
  required=true
  shift
fi
if [ $# -eq 0 ]; then
  echo "Usage: $0 [--required] <installer-dir>..." >&2
  exit 2
fi
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"

for dir in "$@"; do
  pkgs=("$dir"/*-Setup.pkg)
  if [ ${#pkgs[@]} -eq 0 ]; then
    echo "::error::No Setup.pkg in $dir."
    exit 1
  fi
  for pkg in "${pkgs[@]}"; do
    status="$(pkgutil --check-signature "$pkg" || true)"
    if grep -q 'Status: signed' <<<"$status" && xcrun stapler validate "$pkg" >/dev/null 2>&1; then
      # The first certificate of the chain pkgutil prints is the one that signed.
      signer="$(grep -m1 -E '^[[:space:]]*1\. ' <<<"$status" | sed -E 's/^[[:space:]]*1\. //' || true)"
      line="signed${signer:+ by $signer} and notarized, checked"
    elif [ "$required" = true ]; then
      echo "$status"
      echo "::error::$(basename "$pkg") should be signed and notarized, but is not."
      exit 1
    else
      line="**unsigned**"
    fi
    echo "$(basename "$pkg"): $line"
    echo "macOS installer \`$(basename "$pkg")\`: $line" >> "$summary"
  done
done
