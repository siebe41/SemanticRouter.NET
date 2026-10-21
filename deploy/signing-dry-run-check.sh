#!/usr/bin/env bash
# Whether the signing dry run still signs as the release does and uploads nothing, for CI (docs/releases.md#code-signing).
# In .github/workflows/signing-dry-run.yml and release.yml, or the workflows given:
#
# - the dry run starts only by hand (workflow_dispatch), asks for no write permission (apart from id-token, for the
#   Azure login by OIDC), uses no GITHUB_TOKEN, and runs
#   nothing that publishes or uploads (vpk upload or download, gh, nuget push, docker push, cosign, git push); the only
#   artifact it uploads is the unsigned build (publish-*), for its own signing jobs;
# - both workflows set up signing and check signatures with the same scripts (deploy/signing-setup.sh,
#   deploy/check-signature-windows.ps1, deploy/check-signature-macos.sh) and pack with deploy/package-installer.sh,
#   and no workflow in the directory signs or checks a signature inline instead (security import, notarytool,
#   stapler, pkgutil --check-signature, Get-AuthenticodeSignature, the Trusted Signing metadata), so there is one copy
#   of each signing step;
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
# id-token: write is the one exception: it lets a job ask for its own OIDC token (for the Azure login), which writes to
# nothing on GitHub.
writes="$(grep -E ':[[:space:]]*write([[:space:]]|$)' <<<"$dry" | grep -vE '^[[:space:]]*id-token:[[:space:]]*write[[:space:]]*$' || true)"
if [ -n "$writes" ]; then
  error "$dry_run" "the dry run asks for a write permission: $(sed 's/^[[:space:]]*//' <<<"$writes" | tr '\n' ' ')It needs contents: read only (and id-token: write for the Azure login)."
fi
if grep -qE 'GITHUB_TOKEN|github\.token' <<<"$dry"; then
  error "$dry_run" "the dry run uses the workflow's token. It reads the repository with the default checkout and needs no token."
fi
while IFS= read -r line; do
  [ -n "$line" ] || continue
  error "$dry_run" "the dry run publishes or uploads: '$(sed 's/^[[:space:]]*//' <<<"$line")'. It must upload nothing."
done < <(grep -E 'vpk (upload|download)|(^|[^[:alnum:]-])gh |nuget push|docker push|cosign|git push' <<<"$dry" || true)
while IFS= read -r path; do
  [ -n "$path" ] || continue
  if [[ "$path" != publish-* ]]; then
    error "$dry_run" "the dry run uploads '$path' as an artifact; only the unsigned build (publish-*) may be uploaded."
  fi
done < <(awk '/uses: actions\/upload-artifact@/ { up = 1; next } up && /^[[:space:]]*path:/ { sub(/^[[:space:]]*path:[[:space:]]*/, ""); print; up = 0 } /^[[:space:]]*- / { up = 0 }' <<<"$dry")

# One copy of each signing step: both workflows call the scripts.
for file in "$dry_run" "$release"; do
  text="$(code "$file")"
  for call in "deploy/signing-setup.sh windows" "deploy/signing-setup.sh macos" "deploy/check-signature-windows.ps1" \
    "deploy/check-signature-macos.sh" "deploy/package-installer.sh win-x64" "deploy/package-installer.sh"; do
    grep -qF "$call" <<<"$text" || error "$file" "does not run '$call'. The release and the signing dry run sign and check with the same scripts."
  done
done
for file in "$workflows"/*.yml "$workflows"/*.yaml; do
  [ -f "$file" ] || continue
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    error "$file" "signs or checks a signature inline: '$(sed 's/^[[:space:]]*//' <<<"$line")'. Use deploy/signing-setup.sh or deploy/check-signature-*, which the release and the signing dry run share."
  done < <(code "$file" | grep -E 'security import|notarytool|stapler|pkgutil --check-signature|Get-AuthenticodeSignature|CodeSigningAccountName' || true)
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
