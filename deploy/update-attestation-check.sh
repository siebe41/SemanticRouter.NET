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
# - what it attests is what the run built, not whatever is on the draft, which anyone with write access to the
#   repository can change (#404): the desktop job does not take over a release it did not create (GITHUB_RUN_ATTEMPT),
#   each installer job records what it attaches (deploy/record-attached.sh) in an attached-* artifact, and the checksums
#   job compares the draft with those records and the desktop artifact in its step "The draft holds only what this
#   run attached", before it attests;
# - the app embeds the trust root, and it is Sigstore's, with an authority and an ECDSA log the app can check.
#
# With --online, also compares the trust root with Sigstore's current one, fetched through Sigstore's TUF repository
# with `gh attestation trusted-root`, which checks the signed TUF metadata (the GitHub CLI carries TUF's root of
# trust): when Sigstore adds a certificate authority or a log, a release must carry it, or the next releases'
# attestations may not verify. --online-advisory makes the same comparison but only warns (for pull requests, which
# cannot have caused a new trust root). --update writes the current trust root over the app's copy, in the same format
# (two-space indented JSON), for you to check the diff and commit.
#
# Usage: deploy/update-attestation-check.sh [--online | --online-advisory | --update] [<release workflow>]
set -euo pipefail

online=false
case "${1:-}" in
  --online) online=strict && shift ;;
  --online-advisory) online=advisory && shift ;;
  --update) online=update && shift ;;
esac

cd "$(dirname "$0")/.."
source=src/SemanticRouter.Desktop/UpdateAttestation.cs
trust_root=src/SemanticRouter.Desktop/Assets/sigstore-trusted-root.json
failures=0
fail() {
  echo "::error::$1"
  failures=$((failures + 1))
}

# python3 on Linux and macOS runners; on Windows, python3 can be a stub that only opens the Store.
python=python3
"$python" -c "" 2>/dev/null || python=python

named="$(sed -n 's/.*const string Workflow = "\([^"]*\)";.*/\1/p' "$source")"
workflow="${1:-$named}"
if [ -z "$named" ]; then
  fail "$source names no release workflow (const string Workflow)."
elif [ "$workflow" = "$named" ] && [ ! -f "$workflow" ]; then
  fail "$source checks updates against attestations by $named, which does not exist."
fi

# A job of the workflow, up to the next job; comment lines left out.
job() {
  awk -v name="  $1:" '$0 == name { job = 1; print; next } job && /^  [A-Za-z0-9_-]+:/ { exit } job && !/^[[:space:]]*#/' "$workflow"
}
# The number of the first line of $2 that matches the extended regular expression $1, or nothing.
line_of() {
  grep -n -m 1 -E "$1" <<<"$2" | cut -d: -f1 || true
}

if [ -f "$workflow" ]; then
  # The `on:` block, and the checksums job up to the next job.
  triggers="$(awk '/^on:/ { on = 1; next } on && /^[^ #]/ { exit } on' "$workflow")"
  checksums="$(job checksums)"

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
    for needed in desktop installers installer-windows installer-macos; do
      grep -Eq "^    needs: \[.*[ ,]${needed}[] ,]" <<<"$checksums" \
        || fail "The checksums job must need $needed, which attaches update packages it attests."
    done
    grep -Eq '^      id-token: write' <<<"$checksums" || fail "The checksums job needs id-token: write to attest."
    grep -Eq '^      attestations: write' <<<"$checksums" || fail "The checksums job needs attestations: write to attest."
    grep -Eq 'gh release download "\$GITHUB_REF_NAME"[^#]*--dir release' <<<"$checksums" \
      || fail "The checksums job must download every asset of the release into release/, the update packages included."
    grep -Eq 'uses: actions/attest(-build-provenance)?@' <<<"$checksums" || fail "The checksums job no longer attests with actions/attest."
    grep -Eq '^ +subject-path: release/\*$' <<<"$checksums" \
      || fail "The checksums job must attest every file in release/ (subject-path: release/*)."

    # Only what the run attached is attested: the records are downloaded, and the draft is compared with them before
    # the attest step.
    grep -Eq '^ +pattern: attached-\*$' <<<"$checksums" \
      || fail "The checksums job must download the installer jobs' records of what they attached (pattern: attached-*)."
    grep -Eq '^ +name: desktop$' <<<"$checksums" \
      || fail "The checksums job must download the desktop job's artifact, to compare the archives on the draft with it."
    compared="$(line_of '^      - name: The draft holds only what this run attached$' "$checksums")"
    attested="$(line_of 'uses: actions/attest(-build-provenance)?@' "$checksums")"
    if [ -z "$compared" ]; then
      fail "The checksums job must compare the draft with what the run attached (its step 'The draft holds only what this run attached'), or it attests whatever is on the draft (#404)."
    elif [ -n "$attested" ] && [ "$compared" -gt "$attested" ]; then
      fail "The checksums job must compare the draft with what the run attached before it attests."
    fi
  fi

  grep -q 'GITHUB_RUN_ATTEMPT' <<<"$(job desktop)" \
    || fail "The desktop job must refuse a release for the tag that exists on the run's first attempt: this run did not create it (#404)."
  for installer in installers installer-windows installer-macos; do
    steps="$(job "$installer")"
    recorded="$(line_of 'deploy/record-attached\.sh' "$steps")"
    uploaded="$(line_of 'vpk upload' "$steps")"
    if [ -z "$steps" ]; then
      fail "$workflow has no $installer job, which attaches an update package the checksums job attests."
    elif [ -z "$recorded" ] || [ -z "$uploaded" ] || [ "$recorded" -gt "$uploaded" ]; then
      fail "The $installer job must record what it attaches with deploy/record-attached.sh before vpk upload."
    elif ! grep -Eq '^ +name: attached-' <<<"$steps"; then
      fail "The $installer job must upload its record of what it attached as an attached-* artifact, for the checksums job."
    fi
  done
fi

grep -q 'Include="Assets\\sigstore-trusted-root.json" LogicalName="sigstore-trusted-root.json"' src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj \
  || fail "The desktop app must embed $trust_root as sigstore-trusted-root.json."
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

if [ "$online" != false ]; then
  # A problem with Sigstore's current trust root: an error, or with --online-advisory a warning.
  online_problem() {
    if [ "$online" = advisory ]; then
      echo "::warning::$1"
    else
      fail "$1"
    fi
  }
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  # gh prints one trust root per line: Sigstore's public-good instance (whose Fulcio is https://fulcio.sigstore.dev),
  # which signs the attestations of public repositories, and GitHub's own, for private ones.
  if ! command -v gh >/dev/null 2>&1; then
    online_problem "Comparing the trust root with Sigstore's current one needs the GitHub CLI (gh attestation trusted-root)."
  elif ! gh attestation trusted-root > "$work/roots.jsonl"; then
    online_problem "Could not fetch Sigstore's trust root through its TUF repository (gh attestation trusted-root)."
  else
    status=0
    "$python" - "$work/roots.jsonl" "$trust_root" "$online" <<'EOF' || status=$?
import json, sys
roots = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
public = [r for r in roots if any(a.get("uri") == "https://fulcio.sigstore.dev" for a in r.get("certificateAuthorities", []))]
if len(public) != 1:
    sys.exit(2)
if sys.argv[3] == "update":
    with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(public[0], indent=2) + "\n")
    sys.exit(0)
sys.exit(0 if json.load(open(sys.argv[2], encoding="utf-8")) == public[0] else 1)
EOF
    case "$status" in
      0) [ "$online" != update ] || echo "Wrote Sigstore's current trust root to $trust_root: check the diff (a new authority or log) and commit it." ;;
      1) online_problem "Sigstore's trust root changed. Run deploy/update-attestation-check.sh --update, check the diff (a new authority or log) and commit it, so the next releases verify." ;;
      2) online_problem "gh attestation trusted-root printed no single trust root for Sigstore's public-good instance (https://fulcio.sigstore.dev)." ;;
      *) online_problem "Could not compare $trust_root with Sigstore's current trust root." ;;
    esac
  fi
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures problem(s) with the attestations the desktop app checks its updates against."
  exit 1
fi
echo "The release workflow attests every update package, and only what its run attached, as the desktop app expects$([ "$online" = strict ] && echo ', and the app carries the current Sigstore trust root')."
