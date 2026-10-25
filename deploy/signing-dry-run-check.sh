#!/usr/bin/env bash
# Whether the signing dry run still signs as the release does and uploads nothing, for CI (docs/releases.md#code-signing).
# In .github/workflows/signing-dry-run.yml and release.yml, or the workflows given:
#
# - the dry run starts only by hand (workflow_dispatch). Its token can only read: the workflow's permissions: is
#   exactly contents: read, no job but windows has a permissions: of its own, windows's is exactly contents: read and
#   id-token: write (for the Azure login by OIDC), and there is no write-all or read-all. It uses no GITHUB_TOKEN, and
#   runs nothing that publishes or uploads (vpk upload or download, with or without a [platform] directive, gh, nuget
#   push, docker push, cosign, git push); the only artifact it uploads is the unsigned build (publish-*), for its own
#   signing jobs; and its signature checks require a signature (-Required, --required);
# - both workflows set up signing and check signatures with the same scripts (deploy/signing-setup.sh,
#   deploy/check-signature-windows.ps1, deploy/check-signature-macos.sh) and pack with deploy/package-installer.sh for
#   Windows and macOS, and no workflow in the directory signs or checks a signature inline instead (security import,
#   notarytool, stapler, pkgutil --check-signature, codesign, productsign, signtool, spctl, Get-AuthenticodeSignature,
#   the Trusted Signing metadata), so there is one copy of each signing step;
# - both install the same vpk version, and log in to Azure with the same azure/login commit.
#
#   deploy/signing-dry-run-check.sh [dry-run-workflow] [release-workflow]
#
# Comment lines are skipped. Prints what is wrong as ::error:: annotations and exits 1 when anything is.
set -euo pipefail

dry_run="${1:-.github/workflows/signing-dry-run.yml}"
release="${2:-.github/workflows/release.yml}"
workflows="$(dirname "$release")"

failed=false
error() {
  echo "::error file=$1::$2"
  failed=true
}
# The workflow without comment lines and carriage returns.
code() { tr -d '\r' < "$1" | grep -vE '^[[:space:]]*#' || true; }

dry="$(code "$dry_run")"

# Started by hand only: every trigger under on: is workflow_dispatch.
triggers="$(awk '/^on:/ { on = 1; next } on && /^[^[:space:]]/ { exit } on && /^  [A-Za-z_]+:/ { sub(/^  /, ""); sub(/:.*/, ""); print }' <<<"$dry")"
if [ "$triggers" != workflow_dispatch ]; then
  error "$dry_run" "the dry run should start only by hand (on: workflow_dispatch), but its triggers are: $(tr '\n' ' ' <<<"$triggers")"
fi
# Every permissions: block, as <job, or - for the workflow> <tab> <permission:level> lines; an inline one
# (permissions: write-all) is one line too. id-token: write in the windows job is the one exception to read-only: it
# lets that job ask for its own OIDC token (for the Azure login), which writes to nothing on GitHub.
permissions="$(awk '
  /^jobs:/ { jobs = 1 }
  jobs && /^  [A-Za-z0-9_-]+:/ { job = $0; sub(/^  /, "", job); sub(/:.*/, "", job) }
  /^permissions:/ || /^    permissions:/ {
    block = 1; indent = match($0, /[^ ]/); who = jobs ? job : "-"; seen[who] = 1
    v = $0; sub(/^[[:space:]]*permissions:[[:space:]]*/, "", v); sub(/[[:space:]]*#.*/, "", v)
    if (v != "") { print who "\t" v; block = 0 }
    next
  }
  block && /^[[:space:]]*$/ { next }
  block && match($0, /[^ ]/) > indent { v = $0; sub(/[[:space:]]+#.*/, "", v); gsub(/[[:space:]]/, "", v); print who "\t" v; next }
  { block = 0 }
  END { if (!("-" in seen)) print "-\t(none)" }' <<<"$dry")"
# The permissions given to one job (or -), sorted, on one line.
granted() { awk -F '\t' -v who="$1" '$1 == who { print $2 }' <<<"$permissions" | sort | tr '\n' ' '; }
if [ "$(granted -)" != "contents:read " ]; then
  error "$dry_run" "the dry run's permissions: should be exactly contents: read, but it is: $(granted -)"
fi
while IFS= read -r job; do
  case "$job" in
    - | "") ;;
    windows)
      [ "$(granted windows)" = "contents:read id-token:write " ] \
        || error "$dry_run" "the windows job's permissions: should be exactly contents: read and id-token: write, but it is: $(granted windows)" ;;
    *) error "$dry_run" "job $job has a permissions: of its own: $(granted "$job")Only the windows job may (contents: read and id-token: write, for the Azure login)." ;;
  esac
done < <(cut -f 1 <<<"$permissions" | sort -u)
if grep -qE '(write|read)-all' <<<"$dry"; then
  error "$dry_run" "the dry run asks for $(grep -oE '(write|read)-all' <<<"$dry" | head -n 1). It needs contents: read only (and id-token: write for the Azure login)."
fi
if grep -qE 'GITHUB_TOKEN|github\.token' <<<"$dry"; then
  error "$dry_run" "the dry run uses the workflow's token. It reads the repository with the default checkout and needs no token."
fi
while IFS= read -r line; do
  [ -n "$line" ] || continue
  error "$dry_run" "the dry run publishes or uploads: '$(sed 's/^[[:space:]]*//' <<<"$line")'. It must upload nothing."
done < <(grep -E 'vpk( \[[a-z]+\])? (upload|download)|(^|[^[:alnum:]-])gh |nuget push|docker push|cosign|git push' <<<"$dry" || true)
# Without -Required (--required), a check reports a missing or broken signature as "unsigned" and passes.
grep -qE 'check-signature-windows\.ps1 .*-Required([[:space:]]|$)' <<<"$dry" \
  || error "$dry_run" "the dry run does not run deploy/check-signature-windows.ps1 with -Required, so an unsigned installer would pass."
grep -qE 'check-signature-macos\.sh --required' <<<"$dry" \
  || error "$dry_run" "the dry run does not run deploy/check-signature-macos.sh with --required, so an unsigned installer would pass."
while IFS= read -r path; do
  [ -n "$path" ] || continue
  if [[ "$path" != publish-* ]]; then
    error "$dry_run" "the dry run uploads '$path' as an artifact; only the unsigned build (publish-*) may be uploaded."
  fi
done < <(awk '/uses: actions\/upload-artifact@/ { up = 1; next } up && /^[[:space:]]*path:/ { sub(/^[[:space:]]*path:[[:space:]]*/, ""); print; up = 0 } /^[[:space:]]*- / { up = 0 }' <<<"$dry")

# One copy of each signing step: both workflows call the scripts.
# The macOS packaging call names its platform in the dry run and loops over the platforms ("$rid") in the release.
for file in "$dry_run" "$release"; do
  text="$(code "$file")"
  # shellcheck disable=SC2016 # "$rid" is the workflow's text, not this script's variable.
  if [ "$file" = "$dry_run" ]; then macos_package="deploy/package-installer.sh osx-arm64"; else macos_package='deploy/package-installer.sh "$rid"'; fi
  for call in "deploy/signing-setup.sh windows" "deploy/signing-setup.sh macos" "deploy/check-signature-windows.ps1" \
    "deploy/check-signature-macos.sh" "deploy/package-installer.sh win-x64" "$macos_package"; do
    grep -qF "$call" <<<"$text" || error "$file" "does not run '$call'. The release and the signing dry run sign and check with the same scripts."
  done
done
for file in "$workflows"/*.yml "$workflows"/*.yaml; do
  [ -f "$file" ] || continue
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    error "$file" "signs or checks a signature inline: '$(sed 's/^[[:space:]]*//' <<<"$line")'. Use deploy/signing-setup.sh or deploy/check-signature-*, which the release and the signing dry run share."
  done < <(code "$file" | grep -E 'security import|notarytool|stapler|pkgutil --check-signature|(^|[^[:alnum:]_/-])(codesign|productsign|signtool|spctl)([^[:alnum:]_-]|$)|Get-AuthenticodeSignature|CodeSigningAccountName' || true)
done

# The same vpk.
versions="$(cat "$dry_run" "$release" | tr -d '\r' | grep -oE 'tool install -g vpk --version [^[:space:]]+' | awk '{ print $NF }' | sort -u)"
if [ -z "$versions" ]; then
  error "$dry_run" "found no 'dotnet tool install -g vpk --version'. If the workflows changed, update deploy/signing-dry-run-check.sh."
elif [ "$(wc -l <<<"$versions")" -ne 1 ]; then
  error "$dry_run" "the release and the signing dry run install different vpk versions: $(tr '\n' ' ' <<<"$versions")"
fi

# The same Azure login, pinned to the same commit.
logins="$(cat "$dry_run" "$release" | tr -d '\r' | grep -vE '^[[:space:]]*#' | grep -oE 'azure/login@[^[:space:]]+' | sort -u || true)"
for file in "$dry_run" "$release"; do
  code "$file" | grep -q 'uses: azure/login@' \
    || error "$file" "does not log in to Azure (azure/login). The release and the signing dry run sign on Windows with the same OIDC login."
done
if [ -n "$logins" ] && [ "$(wc -l <<<"$logins")" -ne 1 ]; then
  error "$dry_run" "the release and the signing dry run use different azure/login versions: $(tr '\n' ' ' <<<"$logins")"
fi

if [ "$failed" = true ]; then
  exit 1
fi
echo "$dry_run uploads nothing, and signs and checks as $release does (vpk $versions, $logins)."
