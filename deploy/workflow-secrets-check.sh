#!/usr/bin/env bash
# Whether the workflows keep their secrets away from build code, for CI (docs/releases.md#where-the-secrets-are). In
# every workflow under .github/workflows, or the workflows given:
#
# - a secret (any use of the secrets context in a ${{ }} expression: secrets.NAME, secrets['NAME'], toJSON(secrets);
#   or the workflow's token, github.token) is given to the one step that needs it, in that step's env: or with:, never
#   to a whole job or workflow (a job's or the workflow's env:). `secrets.NAME != ''`, which passes only whether a
#   secret is set, is allowed anywhere;
# - a job that builds (dotnet restore, build, publish, test or pack, deploy/publish-desktop.sh, or
#   deploy/package-installer.sh without PUBLISH_DIR= on the same line, which then publishes the app itself) holds no
#   secret and can ask for no OIDC token, so no package's build code runs where a credential is;
# - in a job, no step runs `dotnet` (a tool install included) once a step has held a secret or logged in to Azure:
#   such a step can leave a credential behind for the rest of the job (the Azure CLI's login, a keychain holding the
#   signing keys). Nor does a step that holds a secret itself, which would hand it to what dotnet restores and runs;
# - only the jobs in oidc_jobs below may ask for an OIDC token (id-token: write, or write-all), and never a whole
#   workflow: every step of such a job can ask for one. Only the jobs in signing_jobs may run in the windows-signing
#   environment, whose OIDC token Azure accepts for signing (docs/desktop.md#signing-in-to-azure), and a job that logs
#   in to Azure (azure/login) must be one of them.
#
#   deploy/workflow-secrets-check.sh [workflow...]
#
# Comment lines are skipped. A credential that an action makes at run time and passes on as a step output (NuGet/login's
# API key) is not a secret to this check. Prints what is wrong as ::error:: annotations and exits 1 when anything is,
# or when it finds no workflow to check.
set -euo pipefail

# <workflow file name>:<job>
oidc_jobs="release.yml:nuget release.yml:image-sign release.yml:installer-windows release.yml:checksums signing-dry-run.yml:windows pages.yml:deploy scorecard.yml:analysis"
signing_jobs="release.yml:installer-windows signing-dry-run.yml:windows"

if [ $# -eq 0 ]; then
  set -- .github/workflows/*.yml .github/workflows/*.yaml
fi

failed=false
checked=0
for file in "$@"; do
  [ -f "$file" ] || continue
  checked=$((checked + 1))
  # Each line as job <tab> step <tab> where <tab> line: where is workflow (outside jobs:), job (a job's own keys, its
  # env: and permissions: among them) or step, and step numbers a job's steps from 1.
  errors="$(tr -d '\r' < "$file" | awk '
    /^[[:space:]]*#/ { next }
    /^jobs:/ { jobs = 1; next }
    /^[^[:space:]]/ { jobs = 0 }
    !jobs { print "\t0\tworkflow\t" $0; next }
    /^  [A-Za-z0-9_-]+:[[:space:]]*(#.*)?$/ { job = $0; sub(/^  /, "", job); sub(/:.*/, "", job); step = 0; where = "job"; next }
    /^    steps:/ { where = "steps"; next }
    /^    [A-Za-z0-9_-]+:/ { where = "job" }
    where != "job" && /^      - / { step++; where = "step" }
    { print job "\t" step "\t" where "\t" $0 }' | awk -F '\t' -v file="$file" -v name="$(basename "$file")" -v q="'" \
      -v oidc_jobs="$oidc_jobs" -v signing_jobs="$signing_jobs" '
    BEGIN {
      presence = "secrets\\.[A-Za-z0-9_]+[[:space:]]*[!=]=[[:space:]]*" q q
      n = split(oidc_jobs, list, " "); for (i = 1; i <= n; i++) oidc_allowed[list[i]] = 1
      n = split(signing_jobs, list, " "); for (i = 1; i <= n; i++) signing_allowed[list[i]] = 1
    }
    function trim(s) { sub(/^[[:space:]]+/, "", s); sub(/[[:space:]]+$/, "", s); return s }
    function error(message) { print "::error file=" file "::" message }
    # Whether a ${{ }} expression in the line uses the secrets context or the workflow token.
    function has_secret(s,    e) {
      gsub(presence, "", s)
      while (match(s, /[$][{][{][^}]*[}][}]/)) {
        e = substr(s, RSTART, RLENGTH); s = substr(s, RSTART + RLENGTH)
        if (e ~ /(^|[^A-Za-z0-9_.-])secrets([^A-Za-z0-9_-]|$)/) return 1
        if (e ~ /github[.]token/ || e ~ ("github[[][[:space:]]*[\"" q "]token[\"" q "]")) return 1
      }
      return 0
    }
    {
      job = $1; step = $2 + 0; where = $3
      line = $0; sub(/^[^\t]*\t[^\t]*\t[^\t]*\t/, "", line)
      secret = has_secret(line)
      if (secret && where != "step") {
        error((where == "workflow" ? "the workflow" : "job " job) " gives every step a secret: " q trim(line) q ". Give it to the steps that need it, in their env: or with:.")
      }
      if (where != "step" && (line ~ /^[[:space:]]*id-token:[[:space:]]*write/ || line ~ /permissions:[[:space:]]*write-all/)) {
        if (where == "workflow") {
          error("the workflow lets every job ask for an OIDC token: " q trim(line) q ". Ask for id-token: write in the jobs that need it.")
        } else {
          oidc[job] = 1
          if (!((name ":" job) in oidc_allowed)) error("job " job " asks for an OIDC token (" q trim(line) q "), which every step of it can then use. Only " oidc_jobs " may; add a job to oidc_jobs in deploy/workflow-secrets-check.sh only if it needs one.")
        }
      }
      # environment: windows-signing, or its name: under environment: (quoted or not)
      if (where == "job" && line ~ /^    environment:[[:space:]]*.?windows-signing|^      name:[[:space:]]*.?windows-signing/) {
        if ((name ":" job) in signing_allowed) signing[job] = 1
        else error("job " job " runs in the windows-signing environment, whose OIDC token can sign Windows binaries. Only " signing_jobs " may.")
      }
      if (where != "step") next
      key = job SUBSEP step
      if (step > last[job]) last[job] = step
      # A step is named by its name:, or else by its first line (uses: ...).
      if (line ~ /^      - /) { l = line; sub(/^      - /, "", l); label[key] = trim(l) }
      if (line ~ /^(      - |        )name:/) { l = line; sub(/^[[:space:]]*(- )?name:[[:space:]]*/, "", l); label[key] = trim(l) }
      if (secret) { holds[key] = 1; held[job] = 1 }
      if (line ~ /uses:[[:space:]]*azure\/login@/) { holds[key] = 1; azure[job] = 1 }
      if (line ~ /dotnet[[:space:]]+(restore|build|publish|test|pack)([[:space:]]|$)|deploy\/publish-desktop\.sh/) builds[job] = 1
      if (line ~ /deploy\/package-installer\.sh/ && line !~ /PUBLISH_DIR=/) builds[job] = 1
      if (line ~ /(^|[^[:alnum:]_.\/-])dotnet[[:space:]]/) dotnet[key] = 1
    }
    END {
      for (job in builds) {
        if (job in held) error("job " job " builds (dotnet, deploy/publish-desktop.sh, or deploy/package-installer.sh without PUBLISH_DIR) and holds a secret. Build in a job without secrets and hand the result on as an artifact.")
        if (job in oidc) error("job " job " builds (dotnet, deploy/publish-desktop.sh, or deploy/package-installer.sh without PUBLISH_DIR) and can ask for an OIDC token. Build in a job without one and hand the result on as an artifact.")
      }
      for (job in azure) {
        if (!(job in signing)) error("job " job " logs in to Azure but does not run in the windows-signing environment, the one subject Azure trusts for signing.")
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

if [ "$checked" -eq 0 ]; then
  echo "::error::Found no workflow to check (given: $*). Run this from the repository root, or name the workflows."
  exit 1
fi
if [ "$failed" = true ]; then
  exit 1
fi
echo "In $checked workflows, every secret goes to the step that needs it, no step that builds or runs dotnet holds or follows one, and only the jobs allowed to ask for an OIDC token do."
