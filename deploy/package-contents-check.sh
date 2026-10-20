#!/usr/bin/env bash
# Whether the NuGet packages that `dotnet pack` wrote carry what nuget.org shows and what a debugger needs, for CI and
# the release (Directory.Build.targets, docs/releases.md):
#
# - every package: README.md and icon.png at its root, named by its nuspec, and a copyright and repository in the nuspec;
# - every library package: a symbol package (.snupkg) beside it, whose PDB carries the SourceLink map to GitHub;
# - the tool package: its own PDB inside, with the same map.
#
#   deploy/package-contents-check.sh <folder>
#
# Prints what is missing as ::error:: annotations and exits 1 when anything is.
set -euo pipefail

folder="$1"
failed=false

missing() {
  echo "::error::$1"
  failed=true
}

# The SourceLink map is JSON in the portable PDB's blob heap, uncompressed; it maps the sources to this repository.
# grep -c reads to the end: grep -q would stop early and fail the pipe (pipefail) with unzip's SIGPIPE.
has_source_link() {
  unzip -p "$1" "$2" | LC_ALL=C grep -ac 'raw\.githubusercontent\.com/' >/dev/null
}

shopt -s nullglob
packages=("$folder"/SemanticRouter.*.nupkg)
if [ "${#packages[@]}" -eq 0 ]; then
  missing "No SemanticRouter.*.nupkg in $folder."
fi

for package in "${packages[@]}"; do
  file="$(basename "$package")"
  entries="$(unzip -Z1 "$package")"
  nuspec="$(unzip -p "$package" '*.nuspec')"
  id="$(sed -n 's:.*<id>\(.*\)</id>.*:\1:p' <<<"$nuspec")"

  for element in readme icon copyright; do
    grep -q "<$element>[^<]" <<<"$nuspec" || missing "$file has no <$element> in its nuspec."
  done
  grep -q '<repository [^>]*url="https://github.com/' <<<"$nuspec" || missing "$file has no <repository> in its nuspec."
  for name in README.md icon.png; do
    grep -qx "$name" <<<"$entries" || missing "$file has no $name at its root."
  done

  if grep -q '<packageType name="DotnetTool"' <<<"$nuspec"; then
    pdb="$(grep -m 1 '^tools/.*/semantic-router\.pdb$' <<<"$entries" || true)"
    if [ -z "$pdb" ]; then
      missing "$file has no semantic-router.pdb."
    elif ! has_source_link "$package" "$pdb"; then
      missing "$file: $pdb has no SourceLink map."
    fi
    continue
  fi

  symbols="${package%.nupkg}.snupkg"
  if [ ! -f "$symbols" ]; then
    missing "$file has no symbol package ($(basename "$symbols")) beside it."
    continue
  fi
  pdb="$(grep -m 1 "^lib/.*/$id\.pdb\$" <<<"$(unzip -Z1 "$symbols")" || true)"
  if [ -z "$pdb" ]; then
    missing "$(basename "$symbols") has no $id.pdb."
  elif ! has_source_link "$symbols" "$pdb"; then
    missing "$(basename "$symbols"): $pdb has no SourceLink map."
  fi
done

if [ "$failed" = true ]; then
  exit 1
fi
echo "${#packages[@]} packages in $folder have a readme, an icon, a copyright, a repository and symbols with SourceLink."
