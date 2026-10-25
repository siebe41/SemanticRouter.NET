#!/usr/bin/env bash
# The SBOM of one platform's installer and update package, for the release and CI (docs/releases.md):
#
#   deploy/installer-sbom.sh <rid> <version> <full.nupkg> <sbom.cdx.json> <purl>
#   deploy/installer-sbom.sh win-x64 0.6.0 installers/SemanticRouter-0.6.0-win-x64-full.nupkg sbom.cdx.json pkg:generic/...
#
# It is read from the full update package vpk built, which holds what Setup.exe, the .pkg and the AppImage install and
# what the installed app updates from: Syft reads its contents (on Linux, the AppImage's, which the AppImage runtime
# extracts without FUSE), deploy/sbom-complete.cs adds the subject, the packages' licences, the fonts and what vpk adds
# (Velopack's native binaries and, on Linux, the AppImage runtime), and deploy/sbom-check.sh checks the result. Needs
# syft (or SYFT=<path>), jq, unzip, the .NET SDK and the desktop app's packages in the NuGet cache (a restore).
set -euo pipefail

rid="$1"
version="$2"
package="$3"
sbom="$4"
purl="$5"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

unzip -q "$package" -d "$work/contents"
for appimage in "$work"/contents/lib/app/*.AppImage; do
  if [ -f "$appimage" ]; then
    chmod +x "$appimage"
    (cd "$work/contents" && "$appimage" --appimage-extract >/dev/null)
  fi
done

"${SYFT:-syft}" scan "dir:$work/contents" -q -o "cyclonedx-json=$sbom"

name="SemanticRouter-$rid-installer"
dotnet run --file "$root/deploy/sbom-complete.cs" -- "$sbom" --type application --name "$name" --version "$version" \
  --purl "$purl" --description "The desktop app's installer and update package for $rid" --contents "$work/contents" --installer

expected=("Inter (font)" "Fira Sans (font)" "Velopack native binaries")
if [[ "$rid" == win-* ]]; then
  expected+=("SemanticRouter.Files")
fi
if [[ "$rid" == linux-* ]]; then
  expected+=("AppImage type2-runtime")
fi
"$root/deploy/sbom-check.sh" "$sbom" "$name" "$version" "${expected[@]}"
