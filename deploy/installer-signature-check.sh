#!/usr/bin/env bash
# Checks the code signatures of a macOS release's installer and of the app it installs, for the release workflow
# (docs/releases.md#code-signing). macOS only.
#
#   deploy/installer-signature-check.sh <dir> <version> <rid>
#   deploy/installer-signature-check.sh installers/osx-arm64 0.5.0 osx-arm64
#
# - SemanticRouter-<rid>-Setup.pkg in <dir>: signed by the team (pkgutil --check-signature), with its notarization
#   ticket stapled (xcrun stapler validate);
# - the .app inside it (pkgutil --expand-full): codesign --verify --deep --strict with the team's Developer ID
#   signature, and accepted by Gatekeeper as notarized (spctl --assess, source=Notarized Developer ID);
# - the app in the update package SemanticRouter-<version>-<rid>-full.nupkg in <dir>, which the installed app updates
#   to: codesign --verify --deep --strict with the team's Developer ID signature, the signature and team ID the desktop
#   app checks an update for with Updates:Publisher (src/SemanticRouter.Desktop/UpdatePublisher.cs).
#
# The team is TEAM_ID, or the (TEAMID) at the end of APP_IDENTITY ("Developer ID Application: Name (TEAMID)"). With
# SIGNING=signed one of them must be set.
#
# With SIGNING=signed each must pass, or the script exits 1; otherwise it reports what is unsigned. A file that is
# missing fails either way: then nothing was checked. A list item per check goes to $GITHUB_STEP_SUMMARY when it is
# set. Only the version's own package is read: the folder can also hold the previous release's, for a delta update.
set -euo pipefail

dir="$1"
version="$2"
rid="$3"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
# Apple's requirement for Developer ID applications, as CodesignSignatures checks it: an ad-hoc signature (which vpk
# gives an app it does not sign) or another certificate does not pass.
developer_id='anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists'
team="${TEAM_ID:-}"
if [ -z "$team" ] && [[ "${APP_IDENTITY:-}" =~ \(([A-Z0-9]+)\)$ ]]; then
  team="${BASH_REMATCH[1]}"
fi
if [ -n "$team" ]; then
  if ! [[ "$team" =~ ^[A-Z0-9]{10}$ ]]; then
    echo "::error::The team ID '$team' is not ten capital letters and digits."
    exit 1
  fi
  # The team's own certificate, as UpdatePublisher.Matches compares the team ID: another team's Developer ID fails.
  developer_id="$developer_id and certificate leaf[subject.OU] = \"$team\""
elif [ "${SIGNING:-}" = signed ]; then
  echo "::error::No team ID to check the signatures against: set TEAM_ID, or APP_IDENTITY ending in (TEAMID)."
  exit 1
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failed=false

# report <what> <passed: true|false> <line when it passed> <the tool's output when it did not>
report() {
  local line
  if [ "$2" = true ]; then
    line="$3"
  elif [ "${SIGNING:-}" = signed ]; then
    printf '%s\n' "$4"
    echo "::error::$1 should be signed and notarized, but is not."
    failed=true line="**failed**"
  else
    line="**unsigned**"
  fi
  echo "$1: $line"
  echo "- $1: $line" >> "$summary"
}

pkg="$dir/SemanticRouter-$rid-Setup.pkg"
package="$dir/SemanticRouter-$version-$rid-full.nupkg"
for file in "$pkg" "$package"; do
  if [ ! -f "$file" ]; then
    echo "::error::No $(basename "$file") in $dir."
    exit 1
  fi
done

ok=false
if detail="$(pkgutil --check-signature "$pkg" 2>&1)" && grep -q 'Status: signed' <<<"$detail" \
  && { [ -z "$team" ] || grep -qF "($team)" <<<"$detail"; } \
  && detail="$(xcrun stapler validate "$pkg" 2>&1)"; then
  ok=true
fi
report "macOS installer \`$(basename "$pkg")\`" "$ok" "signed and notarized, checked" "$detail"

pkgutil --expand-full "$pkg" "$work/pkg"
app="$(find "$work/pkg" -type d -name '*.app' -prune -print -quit)"
if [ -z "$app" ]; then
  echo "::error::$(basename "$pkg") holds no .app."
  exit 1
fi
ok=false
# spctl also says "accepted" when Gatekeeper is switched off (override=security disabled), so require the source.
if detail="$(codesign --verify --deep --strict -R "=$developer_id" "$app" 2>&1)" \
  && detail="$(spctl --assess --type execute -vv "$app" 2>&1)" \
  && grep -q '^source=Notarized Developer ID$' <<<"$detail"; then
  ok=true
fi
report "\`$(basename "$app")\` in \`$(basename "$pkg")\`" "$ok" "signed and notarized, checked" "$detail"

# The update package holds the bundle's Contents folder, without file modes: unpack it as an .app and make the
# executable runnable, as the updater does.
mkdir "$work/update"
ditto -x -k "$package" "$work/update"
bundle="$work/SemanticRouter.app"
if [ ! -f "$work/update/lib/app/Contents/MacOS/SemanticRouter" ]; then
  echo "::error::$(basename "$package") has no lib/app/Contents/MacOS/SemanticRouter."
  exit 1
fi
mv "$work/update/lib/app" "$bundle"
chmod +x "$bundle/Contents/MacOS/SemanticRouter"
ok=false
if detail="$(codesign --verify --deep --strict -R "=$developer_id" "$bundle" 2>&1)"; then
  ok=true
fi
report "\`SemanticRouter.app\` in \`$(basename "$package")\`" "$ok" "signed, checked" "$detail"

if [ "$failed" = true ]; then
  exit 1
fi
