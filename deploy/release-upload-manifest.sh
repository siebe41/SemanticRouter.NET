#!/usr/bin/env bash
# Records what an installer job of the release workflow uploaded to the draft release, for the checksums job
# (docs/releases.md#what-the-attestation-proves). Run right after `vpk upload github`, with the token that uploaded:
#
#   deploy/release-upload-manifest.sh <manifest> <dir> <channel> <version>
#   deploy/release-upload-manifest.sh manifest/linux-x64.sha256 installers/linux-x64 linux-x64 0.7.0
#
# vpk decides which files of <dir> it uploads, and writes the channel's update feed (releases.<channel>.json) itself.
# So this reads the draft release of $GITHUB_REF_NAME back: every asset named like a file in <dir> must have that
# file's SHA-256 (GitHub's digest of the asset, or the asset downloaded when GitHub gives none), and the feed is
# downloaded and hashed as it is now. Each is appended to <manifest> as a sha256sum line (`<sha256>  <name>`), which the
# job uploads as a workflow artifact. The checksums job refuses any asset of the draft whose name and SHA-256 are not
# in one of the run's manifests, and checks that each feed lists only packages that are. A file of <dir> that is not on
# the draft (the previous release's package, fetched for the delta; vpk's own lists) is left out.
#
# Fails when the draft has an asset by a file's name with other contents (it was replaced, or planted before vpk ran),
# or lacks the version's full package or the feed. Needs gh, signed in through GH_TOKEN or GITHUB_TOKEN, and
# GITHUB_REPOSITORY and GITHUB_REF_NAME, as on GitHub Actions. Runs on Linux, Windows (Git Bash) and macOS, with its
# bash 3.2: no associative arrays.
set -euo pipefail

if [ $# -ne 4 ]; then
  echo "Usage: $0 <manifest> <dir> <channel> <version>" >&2
  exit 2
fi
manifest="$1"
dir="$2"
channel="$3"
version="$4"
feed="releases.$channel.json"
full="SemanticRouter-$version-$channel-full.nupkg"

sha256() {
  local digest
  if command -v sha256sum >/dev/null; then
    digest="$(sha256sum <"$1")"
  else
    digest="$(shasum -a 256 <"$1")"
  fi
  echo "${digest%% *}"
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Downloads one asset of the draft into $work and prints its SHA-256.
download() {
  rm -rf "$work/download" && mkdir "$work/download"
  gh release download "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY" --pattern "$1" --dir "$work/download" >&2
  if [ ! -f "$work/download/$1" ]; then
    echo "::error::Could not download $1 from the draft release $GITHUB_REF_NAME." >&2
    return 1
  fi
  sha256 "$work/download/$1"
}

# name <tab> digest, for every asset of the draft; the digest is GitHub's, "sha256:<hex>", or empty.
gh release view "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY" --json assets \
  --jq '.assets[] | [.name, (.digest // "")] | @tsv' | tr -d '\r' >"$work/assets"

# Whether the draft has an asset by that name; prints GitHub's digest of it in lower-case hex, or nothing.
on_draft() {
  awk -F '\t' -v name="$1" '$1 == name { sub(/^sha256:/, "", $2); print tolower($2); found = 1; exit } END { exit !found }' \
    "$work/assets"
}

mkdir -p "$(dirname "$manifest")"
failed=false
recorded=" "
for path in "$dir"/*; do
  name="${path##*/}"
  # The feed on the draft is vpk's upload, not the file of that name vpk pack left in <dir>: it is read below.
  if [ ! -f "$path" ] || [ "$name" = "$feed" ] || ! uploaded="$(on_draft "$name")"; then
    continue
  fi
  local_digest="$(sha256 "$path")"
  if [ -z "$uploaded" ]; then
    uploaded="$(download "$name")"
  fi
  if [ "$uploaded" != "$local_digest" ]; then
    echo "::error::$name on the draft release $GITHUB_REF_NAME is not the file this job uploaded (sha256 $uploaded, not $local_digest). Something else replaced or planted it: delete it from the draft and re-run this job."
    failed=true
    continue
  fi
  printf '%s  %s\n' "$local_digest" "$name" >>"$manifest"
  recorded="$recorded$name "
done

if ! on_draft "$feed" >/dev/null; then
  echo "::error::The draft release $GITHUB_REF_NAME has no $feed after vpk upload."
  failed=true
else
  uploaded="$(download "$feed")"
  printf '%s  %s\n' "$uploaded" "$feed" >>"$manifest"
  recorded="$recorded$feed "
fi
if [ "$failed" = false ] && [[ "$recorded" != *" $full "* ]]; then
  echo "::error::The draft release $GITHUB_REF_NAME has no $full from $dir after vpk upload."
  failed=true
fi

if [ "$failed" = true ]; then
  exit 1
fi
echo "Uploaded to $GITHUB_REF_NAME for $channel:$recorded"
