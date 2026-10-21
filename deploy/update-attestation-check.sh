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
# - the app embeds the trust root, and it is Sigstore's, with an authority and an ECDSA log the app can check.
#
# With --online, also compares the trust root with the current one in sigstore/root-signing: when Sigstore adds a
# certificate authority or a log, a release must carry it, or the next releases' attestations may not verify.
#
# Usage: deploy/update-attestation-check.sh [--online] [<release workflow>]
set -euo pipefail

online=false
if [ "${1:-}" = --online ]; then
  online=true
  shift
fi

cd "$(dirname "$0")/.."
source=src/SemanticRouter.Desktop/UpdateAttestation.cs
trust_root=src/SemanticRouter.Desktop/Assets/sigstore-trusted-root.json
upstream=https://raw.githubusercontent.com/sigstore/root-signing/main/targets/trusted_root.json
failures=0
fail() {
  echo "::error::$1"
  failures=$((failures + 1))
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
  fi
fi

grep -q 'Include="Assets\\sigstore-trusted-root.json" LogicalName="sigstore-trusted-root.json"' src/SemanticRouter.Desktop/SemanticRouter.Desktop.csproj \
  || fail "The desktop app must embed $trust_root as sigstore-trusted-root.json."
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

if [ "$online" = true ]; then
  current="$(mktemp)"
  trap 'rm -f "$current"' EXIT
  if ! curl -fsSL --retry 3 -o "$current" "$upstream"; then
    fail "Could not download Sigstore's trust root from $upstream."
  elif ! cmp -s "$current" "$trust_root"; then
    fail "Sigstore's trust root changed. Run: curl -fsSL -o $trust_root $upstream, check the diff (a new authority or log) and commit it, so the next releases verify."
  fi
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures problem(s) with the attestations the desktop app checks its updates against."
  exit 1
fi
echo "The release workflow attests every update package as the desktop app expects$([ "$online" = true ] && echo ', and the app carries the current Sigstore trust root')."
