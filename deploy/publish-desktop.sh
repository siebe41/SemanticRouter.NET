#!/usr/bin/env bash
# Publishes the desktop app for one platform, self-contained (no .NET install needed to run it):
#
#   deploy/publish-desktop.sh <rid> <version> <output-dir>
#
# package-desktop.sh and package-installer.sh package what this produces. The release builds it in a job that holds no
# signing or publishing secrets, and signs it in another.
set -euo pipefail

rid="$1"
version="$2"
out="$3"
root="$(cd "$(dirname "$0")/.." && pwd)"

dotnet publish "$root/src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none \
  --output "$out"
