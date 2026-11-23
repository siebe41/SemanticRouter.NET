#!/usr/bin/env bash
# Opens an issue with the given title, or comments on the open one that has exactly that title, so a workflow that
# reports the same thing every week keeps one issue instead of opening a new one each time. For the scheduled workflows
# (image-freshness.yml, release-sbom-scan.yml, and the report-scheduled-failure jobs of ci.yml, downgrade.yml,
# egress-hosts.yml and gateway-benchmark.yml). Needs gh with GH_TOKEN (issues: write) and jq.
#
#   deploy/report-issue.sh <title> <body> [<label>...]
#
# A new issue gets the labels given; a comment does not change the open issue's labels.
set -euo pipefail

if [ $# -lt 2 ] || [ -z "$1" ] || [ -z "$2" ]; then
  echo "Usage: $0 <title> <body> [<label>...]" >&2
  exit 2
fi
title="$1"
body="$2"
shift 2
repo="${GITHUB_REPOSITORY:?Set GITHUB_REPOSITORY to owner/name.}"

# The search matches words; the exact title decides.
existing="$(gh issue list --repo "$repo" --state open --search "\"$title\" in:title" --json number,title --limit 100 \
  | jq -r --arg title "$title" '[.[] | select(.title == $title)][0].number // empty')"
if [ -n "$existing" ]; then
  gh issue comment "$existing" --repo "$repo" --body "$body"
else
  labels=()
  for label in "$@"; do
    labels+=(--label "$label")
  done
  gh issue create --repo "$repo" --title "$title" --body "$body" "${labels[@]}"
fi
