#!/usr/bin/env bash
# Whether a CycloneDX SBOM completed by deploy/sbom-complete.cs is complete, for CI and the release (docs/releases.md):
# its subject (metadata.component) has the name and version given and a purl; it has NuGet components, each with a
# licence and none under Syft's runtimepack. name, which no advisory matches; and each component named on the command
# line is in it, with a licence.
#
#   deploy/sbom-check.sh <sbom.cdx.json> <name> <version> [component]...
#   deploy/sbom-check.sh SemanticRouter-0.6.0-win-x64-installer.cdx.json SemanticRouter-win-x64-installer 0.6.0 "Velopack native binaries"
#
# Prints what is missing as ::error:: annotations and exits 1 when anything is. Needs jq.
set -euo pipefail

sbom="$1"
name="$2"
version="$3"
shift 3
failed=false
error() {
  echo "::error::$sbom: $1"
  failed=true
}

subject_name="$(jq -r '.metadata.component.name // ""' "$sbom")"
subject_version="$(jq -r '.metadata.component.version // ""' "$sbom")"
subject_purl="$(jq -r '.metadata.component.purl // ""' "$sbom")"
[ "$subject_name" = "$name" ] || error "its subject (metadata.component) is named '$subject_name', expected '$name'."
[ "$subject_version" = "$version" ] || error "its subject's version is '$subject_version', expected '$version'."
[[ "$subject_purl" == pkg:* ]] || error "its subject has no purl."

nuget='.components[]? | select((.purl // "") | startswith("pkg:nuget/"))'
licensed='[.licenses[]? | .expression // .license.id // .license.name // empty] | length > 0'
if [ "$(jq -r "[$nuget] | length" "$sbom")" -eq 0 ]; then
  error "it has no NuGet components."
fi

while IFS= read -r purl; do
  if [ -n "$purl" ]; then error "$purl has no licence."; fi
done < <(jq -r "[$nuget | select(($licensed) | not) | .purl] | unique | .[]" "$sbom")

while IFS= read -r purl; do
  if [ -n "$purl" ]; then error "$purl is named after Syft's runtime pack, not its package."; fi
done < <(jq -r "[$nuget | select(.purl | startswith(\"pkg:nuget/runtimepack.\")) | .purl] | unique | .[]" "$sbom")

for component in "$@"; do
  if ! jq -e --arg name "$component" "any(.components[]?; .name == \$name and ($licensed))" "$sbom" >/dev/null; then
    error "it does not list $component with a licence."
  fi
done

if [ "$failed" = true ]; then
  exit 1
fi

echo "$sbom: $name $version, every NuGet component has a licence${1:+, and it lists $*}."
