#!/usr/bin/env bash
# Whether the release workflow publishes last, for CI (docs/releases.md#the-order-of-a-release): a failure in any job
# that builds, signs or attaches something to the draft release must leave nothing public. In
# .github/workflows/release.yml, or the workflow given:
#
# - the GitHub release is created as a draft (`gh release create ... --draft`), and `vpk upload` never publishes it
#   (`--publish`);
# - every job that publishes (`dotnet nuget push`, `docker push`, `docker buildx imagetools create`, a `push: true`,
#   `cosign sign`, `gh release edit ... --draft=false` or `gh api ... draft=false`)
#   waits, through its `needs`, for every job that doesn't publish, so a job that can still fail (a build, a check, an
#   SBOM) runs before anything is public, and for every job that writes to the draft release (`gh release create`,
#   `gh release upload`, `vpk upload`);
# - one job publishes the release, and it waits for every other job;
# - no step continues on error, and no job that publishes runs when a job it needs failed (always(), failure()).
#
#   deploy/release-order-check.sh [workflow]
#
# Reads the workflow as this repository writes it: jobs indented two spaces under `jobs:`, `needs:` four, as one name,
# a [ list ] or a - list. Comment lines are skipped, and a line ending in a backslash is joined with the next. Prints
# what is wrong as ::error:: annotations and exits 1 when anything is. Needs bash 4 or later (macOS: brew's bash).
set -euo pipefail

workflow="${1:-.github/workflows/release.yml}"

# One record per job (job<TAB>job), per job it needs (job<TAB>needs<TAB>name) and per line of the job
# (job<TAB>line<TAB>text).
records="$(tr -d '\r' < "$workflow" | awk '
  function flush() { if (text != "") { print job "\tline\t" text; text = "" } }
  /^jobs:/ { injobs = 1; next }
  !injobs { next }
  /^[^[:space:]#]/ { flush(); injobs = 0; next }
  /^[[:space:]]*#/ { next }
  /^  [A-Za-z0-9_-]+:[[:space:]]*$/ {
    flush(); job = $1; sub(/:$/, "", job); print job "\tjob"; list = 0; next
  }
  list && /^      - / {
    name = $0; sub(/^      - /, "", name); sub(/[[:space:]]#.*/, "", name); gsub(/[[:space:]]/, "", name)
    print job "\tneeds\t" name; next
  }
  { list = 0 }
  /^    needs:/ {
    value = $0; sub(/^    needs:/, "", value); sub(/[[:space:]]#.*/, "", value); gsub(/[[:space:]]|\[|\]/, "", value)
    if (value == "") { list = 1 } else { n = split(value, names, ","); for (i = 1; i <= n; i++) if (names[i] != "") print job "\tneeds\t" names[i] }
    next
  }
  job != "" {
    line = $0; sub(/^[[:space:]]+/, "", line)
    text = (text == "") ? line : (text " " line)
    if (text ~ /\\$/) { sub(/\\$/, "", text) } else { flush() }
  }
  END { flush() }
')"

failed=false
error() {
  echo "::error file=$workflow::$1"
  failed=true
}

jobs=()
declare -A needs=() staging=() publishing=() releasing=() guarded=()
while IFS=$'\t' read -r job kind text; do
  case "$kind" in
    job) jobs+=("$job") needs[$job]="" ;;
    needs) needs[$job]+=" $text" ;;
    line)
      if [[ "$text" =~ gh\ release\ (create|upload)|vpk\ upload ]]; then staging[$job]=1; fi
      if [[ "$text" =~ dotnet\ nuget\ push|docker\ push|docker\ buildx\ imagetools\ create|cosign\ sign|^push:\ *true ]]; then
        publishing[$job]=1 # push: true is docker/build-push-action's
      fi
      if [[ "$text" =~ gh\ release\ edit.*--draft=false|gh\ api.*draft[=:]\ *false ]]; then publishing[$job]=1 releasing[$job]=1; fi
      if [[ "$text" == *"vpk upload"* && "$text" =~ --publish([^-[:alnum:]]|$) ]]; then
        error "$job: vpk upload publishes the release (--publish). Drop --publish: the publish job publishes the release last."
      fi
      if [[ "$text" == *"gh release create"* && ! "$text" =~ --draft([^=]|$) ]]; then
        error "$job: gh release create makes the release public at once. Create it with --draft."
      fi
      # Waiting is only worth something if a failure stops what waits: no step or job may fail without failing the
      # run, and no job may run after one it needs has failed.
      if [[ "$text" =~ ^(-\ )?continue-on-error: ]]; then
        error "$job: continue-on-error lets the release go on after a failure. Remove it."
      fi
      if [[ "$text" =~ ^(-\ )?if:.*(always|failure|cancelled)\(\) ]]; then
        guarded[$job]=1
      fi
      ;;
  esac
done <<<"$records"

# The jobs a job waits for, directly or through the jobs it needs.
ancestors() {
  local -A seen=()
  local todo=(${needs[$1]:-}) name
  while [ ${#todo[@]} -gt 0 ]; do
    name="${todo[0]}" && todo=("${todo[@]:1}")
    [ -z "${seen[$name]:-}" ] || continue
    seen[$name]=1
    todo+=(${needs[$name]:-})
  done
  echo "${!seen[@]}"
}

# Should the workflow be rewritten so that this script no longer recognizes a step, it fails rather than passing on a
# workflow it did not read.
for what in "dotnet nuget push" "docker push" "gh release create" "vpk upload"; do
  grep -qF "$what" <<<"$records" || error "no job runs '$what'. If the release workflow changed, update deploy/release-order-check.sh."
done
if [ ${#releasing[@]} -ne 1 ]; then
  error "${#releasing[@]} jobs publish the release (gh release edit --draft=false); there should be exactly one, after every other job."
fi

for job in "${!publishing[@]}"; do
  if [ -n "${guarded[$job]:-}" ]; then
    error "$job publishes, but has an if: with always(), failure() or cancelled(), so it can run after a job it needs failed."
  fi
  before=" $(ancestors "$job") "
  for writer in "${!staging[@]}"; do
    if [ "$writer" != "$job" ] && [[ "$before" != *" $writer "* ]]; then
      error "$job publishes, but does not wait for $writer, which writes to the draft release. Add it to $job's needs, directly or through another job."
    fi
  done
  for other in "${jobs[@]}"; do
    if [ -z "${publishing[$other]:-}" ] && [ -z "${staging[$other]:-}" ] && [[ "$before" != *" $other "* ]]; then
      error "$job publishes, but does not wait for $other, which can still fail. Add it to $job's needs, directly or through another job."
    fi
  done
done
for job in "${!releasing[@]}"; do
  before=" $(ancestors "$job") "
  for other in "${jobs[@]}"; do
    if [ "$other" != "$job" ] && [[ "$before" != *" $other "* ]]; then
      error "$job publishes the release, but does not wait for $other. It should need every other job."
    fi
  done
done

if [ "$failed" = true ]; then
  exit 1
fi
sorted() { tr ' ' '\n' <<<"$*" | sort | tr '\n' ' ' | sed 's/ $//'; }
echo "$workflow publishes last. Jobs that write to the draft release: $(sorted "${!staging[@]}")."
echo "Jobs that publish, after all of those and every job that does not: $(sorted "${!publishing[@]}"); $(sorted "${!releasing[@]}") waits for all $(( ${#jobs[@]} - 1 )) other jobs."
