#!/usr/bin/env bash
# Whether the release workflow still makes the attestations the desktop app checks its updates against (#181,
# docs/desktop.md#updates). Without a publisher in the machine policy, the app applies an update from GitHub only when
# GitHub has an attestation for its package that names the package's SHA-256, made by
# https://github.com/<repo>/.github/workflows/release.yml@refs/tags/v<version>, and that verifies against the Sigstore
# trust root the app carries (src/SemanticRouter.Desktop/UpdateAttestation.cs). So:
#
# - the workflow the app names exists, and runs only for v* tags (a branch or a reusable call would sign as another ref);
# - its checksums job downloads every asset of the release, the *-full.nupkg packages included, after the installer
#   jobs have attached them, and attests all of them (id-token and attestations write, actions/attest on release/*);
# - but only after checking them against the manifests of what this run's jobs uploaded (#404): it downloads the
#   manifest-* artifacts and has an 'Only what this run uploaded' step before it attests; every job that uploads to the
#   draft leaves a manifest-* artifact (a vpk upload through deploy/release-upload-manifest.sh); and the job that
#   creates the release refuses one that exists on the run's first attempt (GITHUB_RUN_ATTEMPT);
# - the app embeds the trust root, and it is Sigstore's, with an authority and an ECDSA log the app can check, and its
#   build records when that trust root was current.
#
# With --online, also compares the trust root with Sigstore's current one: when Sigstore adds a certificate authority or
# a log, a release must carry it, or the next releases' attestations may not verify. The current one comes from
# `gh attestation trusted-root`, which fetches it from Sigstore's TUF repository and verifies it against the TUF root
# gh carries (signed, versioned metadata that expires, so a stale or altered copy fails), rather than from a branch of
# a git repository. It prints one trust root per line, Sigstore's public-good one and GitHub's own, and needs no token;
# the public-good one (with the Fulcio authority https://fulcio.sigstore.dev) is compared, as JSON, with the app's copy.
# --trusted-root <file> compares with a file that command wrote instead, without the network.
#
# With --advisory, a trust root that differs or cannot be fetched is a warning, not a failure: CI's pull request runs
# use it, since Sigstore changing its trust root is not the pull request's doing. The release runs without it and fails.
#
# Usage: deploy/update-attestation-check.sh [--online | --trusted-root <file>] [--advisory] [<release workflow>]
set -euo pipefail

usage="Usage: $0 [--online | --trusted-root <file>] [--advisory] [<release workflow>]"
online=false
advisory=false
current_root=""
while [ $# -gt 0 ]; do
  case "$1" in
    --online) online=true; shift ;;
    --trusted-root)
      [ $# -ge 2 ] && [ -f "$2" ] || { echo "--trusted-root needs a file. $usage" >&2; exit 2; }
      current_root="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
      shift 2 ;;
    --advisory) advisory=true; shift ;;
    -*) echo "Unknown option $1. $usage" >&2; exit 2 ;;
    *) break ;;
  esac
done

cd "$(dirname "$0")/.."
source=src/SemanticRouter.Desktop/UpdateAttestation.cs
trust_root=src/SemanticRouter.Desktop/Assets/sigstore-trusted-root.json
failures=0
fail() {
  echo "::error::$1"
  failures=$((failures + 1))
}
# A problem with Sigstore's current trust root: a warning with --advisory, else a failure.
trust_root_problem() {
  if [ "$advisory" = true ]; then
    echo "::warning::$1"
  else
    fail "$1"
  fi
}

named="$(sed -n 's/.*const string Workflow = "\([^"]*\)";.*/\1/p' "$source")"
workflow="${1:-$named}"
if [ -z "$named" ]; then
  fail "$source names no release workflow (const string Workflow)."
elif [ "$workflow" = "$named" ] && [ ! -f "$workflow" ]; then
  fail "$source checks updates against attestations by $named, which does not exist."
fi

if [ -f "$workflow" ]; then
  # The `on:` block, and the checksums job up to the next job.
  triggers="$(awk '/^on:/ { on = 1; next } on && /^[^ #]/ { exit } on' "$workflow")"
  checksums="$(awk '/^  checksums:/ { job = 1; print; next } job && /^  [A-Za-z0-9_-]+:/ { exit } job' "$workflow")"

  grep -Eq "^  push:" <<<"$triggers" && grep -Eq "^    tags: \[ *'v\*' *\]" <<<"$triggers" \
    || fail "$workflow must run on pushed v* tags: the app expects attestations signed by $workflow@refs/tags/v<version>."
  for other in workflow_call workflow_dispatch branches pull_request schedule release; do
    if grep -Eq "^ +$other:" <<<"$triggers"; then
      fail "$workflow also runs on '$other': its attestations would not name a v* tag, and the app would refuse its updates."
    fi
  done

  if [ -z "$checksums" ]; then
    fail "$workflow has no checksums job, which attests the release's assets."
  else
    for needed in installers installer-windows installer-macos; do
      grep -Eq "^    needs: \[.*[ ,]$needed[] ,]" <<<"$checksums" \
        || fail "The checksums job must need $needed, which attaches update packages it attests."
    done
    grep -Eq '^      id-token: write' <<<"$checksums" || fail "The checksums job needs id-token: write to attest."
    grep -Eq '^      attestations: write' <<<"$checksums" || fail "The checksums job needs attestations: write to attest."
    grep -Eq 'gh release download "\$GITHUB_REF_NAME"[^#]*--dir release' <<<"$checksums" \
      || fail "The checksums job must download every asset of the release into release/, the update packages included."
    grep -Eq 'uses: actions/attest(-build-provenance)?@' <<<"$checksums" || fail "The checksums job no longer attests with actions/attest."
    grep -Eq '^ +subject-path: release/\*$' <<<"$checksums" \
      || fail "The checksums job must attest every file in release/ (subject-path: release/*)."
    # What it attests is what this run uploaded (#404), not whatever is on the draft: it reads the uploading jobs'
    # manifests and checks the draft against them before it attests.
    grep -Eq '^ +pattern: manifest-\*$' <<<"$checksums" \
      || fail "The checksums job must download the uploading jobs' manifests (pattern: manifest-*)."
    verify="$(grep -nE '^ +- name: Only what this run uploaded$' <<<"$checksums" | head -n 1 | cut -d: -f1 || true)"
    attest="$(grep -nE 'uses: actions/attest(-build-provenance)?@' <<<"$checksums" | head -n 1 | cut -d: -f1 || true)"
    if [ -z "$verify" ]; then
      fail "The checksums job must check the draft against this run's manifests (a step 'Only what this run uploaded') before it attests."
    elif [ -n "$attest" ] && [ "$verify" -gt "$attest" ]; then
      fail "The checksums job attests before it checks the draft against this run's manifests."
    fi
  fi

  # Every job that writes to the draft (gh release upload, vpk upload) leaves a manifest of what it uploaded, and the
  # release is created by the run, not found: an existing one is used only on a re-run attempt.
  jobs="$(awk '/^jobs:/ { on = 1; next } on && /^[^ #]/ { exit } on && /^  [A-Za-z0-9_-]+:/ { sub(/^  /, ""); sub(/:.*/, ""); print }' "$workflow")"
  for job in $jobs; do
    [ "$job" = checksums ] && continue
    body="$(awk -v job="  $job:" '$0 == job { on = 1; next } on && /^  [A-Za-z0-9_-]+:/ { exit } on && !/^[[:space:]]*#/' "$workflow")"
    if grep -Eq 'gh release upload|vpk upload' <<<"$body"; then
      grep -Eq '^ +name: manifest-' <<<"$body" \
        || fail "Job $job uploads to the draft release but leaves no manifest-* artifact of what it uploaded, so the checksums job would refuse its files."
      if grep -q 'vpk upload' <<<"$body" && ! grep -q 'deploy/release-upload-manifest.sh' <<<"$body"; then
        fail "Job $job uploads with vpk but does not record what vpk uploaded (deploy/release-upload-manifest.sh)."
      fi
    fi
    if grep -q 'gh release create' <<<"$body" && ! grep -q 'GITHUB_RUN_ATTEMPT' <<<"$body"; then
      fail "Job $job must refuse a release for the tag that exists on the run's first attempt (GITHUB_RUN_ATTEMPT): this run did not make it."
    fi
  done
fi

grep -q 'Include="Assets\\sigstore-trusted-root.json" LogicalName="sigstore-trusted-root.json"' src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj \
  || fail "The desktop app must embed $trust_root as sigstore-trusted-root.json."
# Without the date its trust root was current, the app refuses every certificate from an authority it does not know,
# even long after Sigstore added one (SigstoreTrustRoot.StaleAfter).
grep -q -- '-p:SigstoreTrustRootCurrentAt=' deploy/publish-desktop.sh \
  && grep -q '<AssemblyMetadata Include="SigstoreTrustRootCurrentAt"' src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj \
  || fail "deploy/publish-desktop.sh must record when the app's trust root was current (-p:SigstoreTrustRootCurrentAt), and the desktop app must carry it as assembly metadata."
# python3 on Linux and macOS runners; on Windows, python3 can be a stub that only opens the Store.
python=python3
"$python" -c "" 2>/dev/null || python=python
if ! "$python" - "$trust_root" <<'EOF'
import json, sys
root = json.load(open(sys.argv[1], encoding="utf-8"))
assert root["mediaType"].startswith("application/vnd.dev.sigstore.trustedroot+json"), "not a Sigstore trusted_root.json"
assert any(a["uri"] == "https://fulcio.sigstore.dev" and "end" not in a["validFor"] for a in root["certificateAuthorities"]), "no current Fulcio authority"
assert any(t["publicKey"]["keyDetails"] == "PKIX_ECDSA_P256_SHA_256" and "end" not in t["publicKey"]["validFor"] for t in root["tlogs"]), "no current ECDSA P-256 log"
EOF
then
  fail "$trust_root is not a Sigstore trust root the app can check attestations against."
fi

if [ "$online" = true ] && [ -z "$current_root" ]; then
  current_root="$(mktemp)"
  trap 'rm -f "$current_root"' EXIT
  if ! command -v gh >/dev/null; then
    trust_root_problem "Could not check Sigstore's current trust root: the GitHub CLI (gh) is not installed."
    current_root=""
  elif ! gh attestation trusted-root >"$current_root"; then
    trust_root_problem "Could not fetch Sigstore's current trust root (gh attestation trusted-root, from Sigstore's TUF repository)."
    current_root=""
  fi
fi

compared=false
if [ -n "$current_root" ]; then
  # The public-good root in the file (one JSON document per line, as gh prints them, or a single trusted_root.json)
  # against the app's copy, as JSON: exits 0 when they are the same, 1 when they differ (printing what), 2 when the file
  # has no single public-good root.
  status=0
  differences="$("$python" - "$current_root" "$trust_root" <<'EOF'
import json, sys
text = open(sys.argv[1], encoding="utf-8").read()
try:
    roots = [json.loads(text)]
except ValueError:
    try:
        roots = [json.loads(line) for line in text.splitlines() if line.strip()]
    except ValueError as error:
        print(f"it is not JSON ({error})")
        sys.exit(2)
public = [r for r in roots if isinstance(r, dict)
          and any(a.get("uri") == "https://fulcio.sigstore.dev" for a in r.get("certificateAuthorities", []))]
if len(public) != 1:
    print(f"it has {len(public)} public-good trust roots (with the authority https://fulcio.sigstore.dev), not one")
    sys.exit(2)
current, carried = public[0], json.load(open(sys.argv[2], encoding="utf-8"))
if current == carried:
    sys.exit(0)
def entries(root):
    found = set()
    for kind, key in (("certificateAuthorities", "uri"), ("tlogs", "baseUrl"), ("ctlogs", "baseUrl"), ("timestampAuthorities", "uri")):
        for entry in root.get(kind, []):
            found.add((kind, entry.get(key), json.dumps(entry, sort_keys=True)))
    return found
now, then = entries(current), entries(carried)
changes = [f"new or changed in {kind}: {name}" for kind, name, _ in sorted(now - then)]
changes += [f"gone or changed in {kind}: {name}" for kind, name, _ in sorted(then - now)]
print("; ".join(changes) or "they differ outside the authorities and logs")
sys.exit(1)
EOF
  )" || status=$?
  fix="gh attestation trusted-root | jq 'select(any(.certificateAuthorities[]?; .uri == \"https://fulcio.sigstore.dev\"))' > $trust_root"
  if [ "$status" -eq 0 ]; then
    compared=true
  elif [ "$status" -eq 1 ]; then
    trust_root_problem "Sigstore's trust root changed ($differences). Run: $fix, check the diff (a new authority or log) and commit it, so the next releases verify."
  else
    trust_root_problem "Could not read Sigstore's current trust root: ${differences:-the comparison failed}."
  fi
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures problem(s) with the attestations the desktop app checks its updates against."
  exit 1
fi
echo "The release workflow attests every update package as the desktop app expects$([ "$compared" = true ] && echo ', and the app carries the current Sigstore trust root')."
