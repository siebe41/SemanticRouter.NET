#!/usr/bin/env bash
# Publishes the desktop app for one platform, self-contained (no .NET install needed to run it):
#
#   deploy/publish-desktop.sh <rid> <version> <output-dir>
#
# package-desktop.sh and package-installer.sh package what this produces. The release builds it in a job that holds no
# signing or publishing secrets, and signs it in another. THIRD-PARTY-NOTICES.txt next to the app lists the licences and
# notices of every package it ships, the .NET runtime included (deploy/third-party-notices.cs).
set -euo pipefail

rid="$1"
version="$2"
out="$3"
root="$(cd "$(dirname "$0")/.." && pwd)"

dotnet publish "$root/src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none \
  --output "$out"

dotnet run --file "$root/deploy/third-party-notices.cs" -- "$out/THIRD-PARTY-NOTICES.txt" "$out"
