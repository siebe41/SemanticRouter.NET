#!/usr/bin/env bash
# Whether the workflows keep their secrets away from build code, for CI (docs/releases.md#where-the-secrets-are). In
# every workflow under .github/workflows, or the workflows given:
#
# - a secret (secrets.NAME, or github.token) is given to the one step that needs it, in that step's env: or with:,
#   never to a whole job or workflow (a job's or the workflow's env:). `secrets.NAME != ''`, which passes only whether
#   a secret is set, is allowed anywhere;
# - a job that builds (dotnet restore, build, publish, test or pack, or deploy/publish-desktop.sh) holds no secret at
#   all, so no package's build code runs where a secret is;
# - in a job, no step runs `dotnet` (a tool install included) once a step has held a secret or logged in to Azure:
#   such a step can leave a credential behind for the rest of the job (the Azure CLI's login, a keychain holding the
#   signing keys). Nor does a step that holds a secret itself, which would hand it to what dotnet restores and runs.
#
#   deploy/workflow-secrets-check.sh [workflow...]
#
# Comment lines are skipped. Prints what is wrong as ::error:: annotations and exits 1 when anything is.
set -euo pipefail

if [ $# -eq 0 ]; then
  set -- .github/workflows/*.yml .github/workflows/*.yaml
fi

failed=false
checked=0
for file in "$@"; do
  [ -f "$file" ] || continue
  checked=$((checked + 1))
  # Each line as job <tab> step <tab> where <tab> line: where is workflow (outside jobs:), job (a job's own keys, its
  # env: among them) or step, and step numbers a job's steps from 1.
  errors="$(tr -d '\r' < "$file" | awk '
    /^[[:space:]]*#/ { next }
    /^jobs:/ { jobs = 1; next }
    /^[^[:space:]]/ { jobs = 0 }
    !jobs { print "\t0\tworkflow\t" $0; next }
    /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { job = $0; sub(/^  /, "", job); sub(/:.*/, "", job); step = 0; where = "job"; next }
    /^    steps:/ { where = "steps"; next }
    /^    [A-Za-z0-9_-]+:/ { where = "job" }
    where != "job" && /^      - / { step++; where = "step" }
    { print job "\t" step "\t" where "\t" $0 }' | awk -F '\t' -v file="$file" -v q="'" '
    BEGIN { presence = "secrets\\.[A-Za-z0-9_]+[[:space:]]*[!=]=[[:space:]]*" q q }
    function trim(s) { sub(/^[[:space:]]+/, "", s); sub(/[[:space:]]+$/, "", s); return s }
    function error(message) { print "::error file=" file "::" message }
    {
      job = $1; step = $2 + 0; where = $3
      line = $0; sub(/^[^\t]*\t[^\t]*\t[^\t]*\t/, "", line)
      text = line; gsub(presence, "", text)
      secret = text ~ /secrets\.[A-Za-z0-9_]+|github\.token/
      if (secret && where != "step") {
        error((where == "workflow" ? "the workflow" : "job " job) " gives every step a secret: " q trim(line) q ". Give it to the steps that need it, in their env: or with:.")
      }
      if (where != "step") next
      key = job SUBSEP step
      if (step > last[job]) last[job] = step
      # A step is named by its name:, or else by its first line (uses: ...).
      if (line ~ /^      - /) { l = line; sub(/^      - /, "", l); label[key] = trim(l) }
      if (line ~ /^(      - |        )name:/) { l = line; sub(/^[[:space:]]*(- )?name:[[:space:]]*/, "", l); label[key] = trim(l) }
      if (secret) { holds[key] = 1; held[job] = 1 }
      if (line ~ /uses:[[:space:]]*azure\/login@/) holds[key] = 1
      if (line ~ /dotnet[[:space:]]+(restore|build|publish|test|pack)([[:space:]]|$)|deploy\/publish-desktop\.sh/) builds[job] = 1
      if (line ~ /(^|[^[:alnum:]_.\/-])dotnet[[:space:]]/) dotnet[key] = 1
    }
    END {
      for (job in builds) {
        if (job in held) error("job " job " builds (dotnet, or deploy/publish-desktop.sh) and holds a secret. Build in a job without secrets and hand the result on as an artifact.")
      }
      for (job in last) {
        after = ""
        for (s = 1; s <= last[job]; s++) {
          key = job SUBSEP s
          if ((key in dotnet) && (key in holds)) {
            error("job " job ", step " q label[key] q " runs dotnet and holds a secret. Run dotnet in a step of its own, before any step that holds one.")
          } else if ((key in dotnet) && after != "") {
            error("job " job ", step " q label[key] q " runs dotnet after " q after q " held a secret or logged in to Azure, which can leave it behind. Run dotnet first.")
          }
          if ((key in holds) && after == "") after = label[key]
        }
      }
    }')"
  if [ -n "$errors" ]; then
    echo "$errors"
    failed=true
  fi
done

if [ "$failed" = true ]; then
  exit 1
fi
echo "In $checked workflows, every secret goes to the step that needs it, and no step that builds or runs dotnet holds or follows one."
