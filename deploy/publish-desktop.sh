#!/usr/bin/env bash
# Publishes the desktop app for one platform, self-contained (no .NET install needed to run it):
#
#   deploy/publish-desktop.sh <rid> <version> <output-dir>
#
# package-desktop.sh and package-installer.sh package what this produces; check-native-libraries.sh checks it first. The release builds it in a job that holds no
# signing or publishing secrets, and signs it in another. THIRD-PARTY-NOTICES.txt next to the app lists the licences and
# notices of every package it ships, the .NET runtime included (deploy/third-party-notices.cs).
set -euo pipefail

rid="$1"
version="$2"
out="$3"
root="$(cd "$(dirname "$0")/.." && pwd)"

# When the app's Sigstore trust root was last known to be current: the date of the commit it is built from, which
# deploy/update-attestation-check.sh --online checked in the release. The app tells an authority Sigstore added after
# that from a certificate someone made by it (src/SemanticRouter.Desktop/SigstoreTrustRoot.cs); a build without it
# refuses every certificate from an authority it does not know. The commit date keeps the build reproducible.
trust_root_current_at="$(git -C "$root" log -1 --format=%cI)"

# Restore is locked: the packages are exactly those in the projects' packages.lock.json files, whose runtime sections
# cover every <rid> the release publishes (DesktopRuntimeIdentifiers in Directory.Build.props).
dotnet publish "$root/src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained -p:RestoreLockedMode=true \
  -p:Version="$version" -p:DebugType=none -p:SigstoreTrustRootCurrentAt="$trust_root_current_at" \
  --output "$out"

# Fails when a native library the app needs on this platform is missing from the publish.
"$root/deploy/check-native-libraries.sh" "$rid" "$out"

dotnet run --file "$root/deploy/third-party-notices.cs" -- "$out/THIRD-PARTY-NOTICES.txt" "$out"

# Windows: SemanticRouter.Files.dll, the digest of every file of the app, programs and libraries included, which vpk
# signs with the rest so the app's publisher check covers every file (deploy/update-file-list.cs). It first deletes what
# vpk leaves out of a package (the runtime's createdump.exe), so the zip download holds the same files. Last, so it
# lists the notices as well; package-installer.sh checks it against the package. It records <version>, the version
# package-installer.sh gives vpk, which the app requires the package's .nuspec and the feed to give too.
case "$rid" in
  win-*) dotnet run --file "$root/deploy/update-file-list.cs" -- write "$out" "$version" ;;
esac
