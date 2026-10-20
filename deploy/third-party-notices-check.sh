#!/usr/bin/env bash
# Whether a THIRD-PARTY-NOTICES.txt written by deploy/third-party-notices.cs is complete, for CI and the release
# (docs/releases.md): every package and component in it has a licence that starts with an SPDX id and at least one
# licence text, and each component named on the command line is listed.
#
#   deploy/third-party-notices-check.sh <notices file> [component]...
#   deploy/third-party-notices-check.sh notices/THIRD-PARTY-NOTICES.txt "Inter (font)" "Fira Sans (font)"
#
# A component matches an entry with that name, with or without a version after it. Prints what is missing as ::error::
# annotations and exits 1 when anything is.
set -euo pipefail

file="$1"
shift
failed=false

# The entries run from the first rule to the first text ("[1] ..."); each starts with an unindented title.
problems="$(awk '
  function check() {
    if (title == "") return
    if (licence !~ /^\(?[A-Za-z0-9.+-]+( |\)|$)/ || licence ~ /^(the|not) /) print title ": the licence names no SPDX id (" licence ")"
    if (!texts) print title ": no licence text"
  }
  /^\[[0-9]+\] / { exit }
  /^=+$/ { started = 1; next }
  !started || /^$/ || /^Components that are not NuGet packages$/ { next }
  /^[^ ]/ { check(); title = $0; licence = ""; texts = 0; count++; next }
  /^  Licence:   / { licence = substr($0, 14) }
  /^  Texts:     / { texts = 1 }
  END { check(); if (count == 0) print "no packages or components listed" }
' "$file")"

if [ -n "$problems" ]; then
  while IFS= read -r line; do
    echo "::error::$file: $line"
  done <<<"$problems"
  failed=true
fi

for component in "$@"; do
  if ! awk -v name="$component" '
    /^\[[0-9]+\] / { exit }
    index($0, name) == 1 && (length($0) == length(name) || substr($0, length(name) + 1, 1) == " ") { found = 1; exit }
    END { exit !found }
  ' "$file"; then
    echo "::error::$file does not list $component."
    failed=true
  fi
done

if [ "$failed" = true ]; then
  exit 1
fi

echo "$file: every entry has an SPDX licence and its text${1:+, and it lists $*}."
