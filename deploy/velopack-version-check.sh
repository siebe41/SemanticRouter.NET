#!/usr/bin/env bash
# Whether every Velopack version in the repository is the Velopack package's, for CI and the release
# (docs/releases.md). The installers are built by the vpk the workflows install, and its native binaries (Update.exe,
# Setup.exe, the stub, UpdateMac, UpdateNix) are the ones vpk adds; the third-party notices name them by the package's
# version, and deploy/third-party/velopack-crates.json lists the Rust crates of one Velopack release. All three have to
# be the same, or the notices describe binaries the release does not ship.
#
#   deploy/velopack-version-check.sh
#
# Prints each mismatch as an ::error:: annotation and exits 1 when there is one.
set -euo pipefail

cd "$(dirname "$0")/.."
failed=false

package="$(sed -n 's/.*<PackageVersion Include="Velopack" Version="\([^"]*\)".*/\1/p' Directory.Packages.props)"
if [ -z "$package" ]; then
  echo "::error::Directory.Packages.props has no Velopack PackageVersion."
  exit 1
fi

# Every `dotnet tool install|update ... vpk` in a workflow, as file:line:text.
installs="$(grep -nE 'tool (install|update)[^#]* vpk( |$)' .github/workflows/*.yml || true)"
if [ -z "$installs" ]; then
  echo "::error::No workflow installs vpk: is this check still looking in the right place?"
  failed=true
fi

while IFS= read -r install; do
  [ -n "$install" ] || continue
  file="${install%%:*}" rest="${install#*:}"
  line="${rest%%:*}" text="${rest#*:}"
  version="$(sed -nE 's/.*--version[ =]+([^ "]+).*/\1/p' <<<"$text")"
  if [ -z "$version" ]; then
    echo "::error file=$file,line=$line::vpk is installed without --version: pin it to the Velopack package's version ($package)."
    failed=true
  elif [ "$version" != "$package" ]; then
    echo "::error file=$file,line=$line::vpk $version is installed here, but the Velopack package is $package."
    failed=true
  fi
done <<<"$installs"

crates="$(sed -n 's/^  "velopack": "\([^"]*\)",$/\1/p' deploy/third-party/velopack-crates.json)"
if [ "$crates" != "$package" ]; then
  echo "::error file=deploy/third-party/velopack-crates.json::It lists the crates of Velopack ${crates:-(none)}, but the package is $package: run python deploy/third-party/velopack-crates.py $package."
  failed=true
fi

if [ "$failed" = true ]; then
  exit 1
fi

echo "vpk in every workflow ($(wc -l <<<"$installs") installs), the Velopack package and deploy/third-party/velopack-crates.json are all $package."
