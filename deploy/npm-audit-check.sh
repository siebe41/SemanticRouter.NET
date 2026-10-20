#!/usr/bin/env bash
# The npm projects' advisories, for CI (README.md, "Tests"): every directory with a package-lock.json (the documentation
# site, the dashboard's end-to-end tests, the demo-video tool) is audited and has an npm entry in .github/dependabot.yml.
#
#   deploy/npm-audit-check.sh
#
# Run from the repository root. Fails on a high or critical advisory in what a project ships (its `dependencies`, e.g.
# Mermaid, which the site loads in every visitor's browser) and on a lock file Dependabot does not update. Advisories in
# development dependencies (VitePress's dev server, Playwright for the tests) and lower ones are printed and counted in
# the job summary without failing it. npm audit reads only the lock file, so nothing is installed.
set -euo pipefail

summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
failed=false

# The directories of the npm entries in dependabot.yml, as /path.
covered="$(tr -d '\r' < .github/dependabot.yml | awk '/package-ecosystem:/ { npm = ($NF == "npm") } npm && /directory:/ { print $NF }')"

{
  echo "| npm project | High or critical in what it ships | All advisories |"
  echo "|---|---|---|"
} >> "$summary"

locks="$(git ls-files '*package-lock.json')"
if [ -z "$locks" ]; then
  echo "::error::No package-lock.json found; run this from the repository root."
  exit 1
fi

while IFS= read -r lock; do
  dir="$(dirname "$lock")"
  path="/$dir"
  [ "$dir" != . ] || path=/
  echo "::group::$dir"
  if ! grep -qxF "$path" <<<"$covered"; then
    echo "::error file=.github/dependabot.yml::$lock has no npm entry in .github/dependabot.yml, so Dependabot does not update it."
    failed=true
  fi
  if (cd "$dir" && npm audit --omit=dev --audit-level=high </dev/null); then
    shipped="none"
  else
    shipped="**found**"
    failed=true
  fi
  report="$(cd "$dir" && npm audit </dev/null 2>&1 || true)"
  echo "$report"
  echo "::endgroup::"
  if [ "$shipped" != "none" ]; then
    echo "::error file=$lock::npm audit found a high or critical advisory in what $dir ships (or could not run); see its group in the log."
  fi
  counts="$(grep -E '^(found )?[0-9]+ .*vulnerabilit' <<<"$report" | tail -n 1 || true)"
  echo "| \`$dir\` | $shipped | ${counts:-see the log} |" >> "$summary"
done <<<"$locks"

if [ "$failed" = true ]; then
  exit 1
fi
echo "No high or critical advisory in what the npm projects ship, and Dependabot updates all of them."
