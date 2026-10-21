#!/usr/bin/env bash
# Whether a release's installers will be code-signed, per platform, for the release workflows (docs/releases.md).
#
# Each platform's signing is complete (signed), absent (fails, unless ALLOW_UNSIGNED_RELEASE is `true`: unsigned) or
# partial (always fails: the installer job would stop half-way, and the release would stay a draft). The workflow
# passes only whether each secret and variable is set, as NAME=true or NAME=false in WINDOWS and MACOS, never a value.
# ALLOW_UNSIGNED_HOW, when set, says in the error how to allow unsigned installers (cut-release.yml also has a box).
#
# Writes windows=<state> and macos=<state> to $GITHUB_OUTPUT and a table to $GITHUB_STEP_SUMMARY when they are set,
# and exits 1 when a release would fail.
set -euo pipefail

failed=false
output="${GITHUB_OUTPUT:-/dev/null}"
# A variable typed into the settings page can carry stray spaces or capitals: `True ` means true too.
allow_unsigned="$(tr -d '[:space:]' <<<"${ALLOW_UNSIGNED_RELEASE:-}" | tr '[:upper:]' '[:lower:]')"
allow_how="${ALLOW_UNSIGNED_HOW:-set the variable ALLOW_UNSIGNED_RELEASE to true}"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
{
  echo "## Code signing"
  echo
  echo "| | |"
  echo "|---|---|"
} >> "$summary"
for platform in windows macos; do
  case "$platform" in
    windows) name=Windows settings="${WINDOWS:-}" how="Azure Trusted Signing" ;;
    macos) name=macOS settings="${MACOS:-}" how="Developer ID, notarized" ;;
  esac
  present=() missing=()
  for pair in $settings; do
    if [ "${pair#*=}" = true ]; then present+=("${pair%%=*}"); else missing+=("${pair%%=*}"); fi
  done
  if [ ${#missing[@]} -eq 0 ]; then
    state=signed line="**signed** ($how)"
  elif [ ${#present[@]} -gt 0 ]; then
    echo "::error::$name signing is only partly set up. Missing: ${missing[*]}. Set them, or remove the others (docs/desktop.md#signing)."
    failed=true state=partial line="**failed**: signing only partly set up, missing ${missing[*]}"
  elif [ "$allow_unsigned" = true ]; then
    echo "::warning::The $name installers will be unsigned: no signing secrets are set and ALLOW_UNSIGNED_RELEASE is true."
    state=unsigned line="**unsigned**: no signing secrets, and ALLOW_UNSIGNED_RELEASE is true"
  else
    echo "::error::No $name signing secrets are set. Add them (docs/desktop.md#signing), or, to publish unsigned installers, $allow_how."
    failed=true state=missing line="**failed**: no signing secrets, and ALLOW_UNSIGNED_RELEASE is not true"
  fi
  echo "$platform=$state" >> "$output"
  echo "| $name installers | $line |" >> "$summary"
done
{
  echo "| Linux AppImage, archives (.zip, .tar.gz) | not code-signed |"
  echo "| Container image | signed with keyless cosign |"
  echo "| Every asset | build provenance attestation and SHA256SUMS |"
} >> "$summary"
if [ "$failed" = true ]; then
  exit 1
fi
