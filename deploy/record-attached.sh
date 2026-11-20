#!/usr/bin/env bash
# What an installer job of the release attaches to the draft release, for the checksums job, which attests only that
# (docs/releases.md#the-order-of-a-release, #404):
#
#   deploy/record-attached.sh <vpk output dir> <channel> <record dir>
#
# `vpk upload github` attaches, byte for byte, the files `vpk pack` listed in <output dir>/assets.<channel>.json, and
# then a releases.<channel>.json that it writes at upload time, with an entry for each of those files that is a full or
# delta package (Velopack's GitReleaseUploadCommandRunner). The releases.<channel>.json that `vpk pack` left in the
# output directory has the same entries, plus one for each earlier package found there (the previous release's, which
# package-installer.sh downloads for the delta). So this writes, into <record dir>:
#
# - <channel>.sha256: the SHA-256 of each file in assets.<channel>.json, as sha256sum prints it;
# - releases.<channel>.json: the feed vpk uploads, that is, the local feed with only the entries for those files.
#
# The checksums job compares the draft with these records: a file on it that no job of the run attached, or one whose
# checksum or feed entries differ, stops the release before anything is attested. Run it after `vpk pack` and before
# `vpk upload`, so that a record that cannot be made stops the job before it attaches anything. Needs jq, and sha256sum
# or shasum.
set -euo pipefail

dir="$1"
channel="$2"
record="$3"
assets="$dir/assets.$channel.json"
feed="$dir/releases.$channel.json"

for file in "$assets" "$feed"; do
  if [ ! -f "$file" ]; then
    echo "::error::$file is missing: vpk pack writes it, and vpk upload reads it."
    exit 1
  fi
done

if command -v sha256sum >/dev/null 2>&1; then
  sum=(sha256sum -b)
else
  sum=(shasum -a 256 -b)
fi

mkdir -p "$record"
# vpk uploads each file under its file name (Path.GetFileName). jq on Windows can end its lines with \r.
names="$(jq -r '.[].RelativeFileName' "$assets" | tr -d '\r')"
if [ -z "$names" ]; then
  echo "::error::$assets lists no files to attach."
  exit 1
fi
: > "$record/$channel.sha256"
while IFS= read -r name; do
  digest="$("${sum[@]}" -- "$dir/$name")"
  digest="${digest#\\}" && digest="${digest%% *}"
  printf '%s  %s\n' "$digest" "${name##*/}" >> "$record/$channel.sha256"
done <<<"$names"

jq --argjson names "$(jq '[.[].RelativeFileName | split("/") | last]' "$assets")" \
  '.Assets |= map(select(.FileName as $file | any($names[]; . == $file)))' "$feed" > "$record/releases.$channel.json"

# Every full and delta package must have its entry, or the record is not the feed vpk uploads.
packages="$(jq '[.[] | select(.Type == "Full" or .Type == "Delta")] | length' "$assets" | tr -d '\r')"
entries="$(jq '.Assets | length' "$record/releases.$channel.json" | tr -d '\r')"
if [ "$packages" != "$entries" ] || [ "$packages" = 0 ]; then
  echo "::error::$assets lists $packages full or delta package(s), but $feed has an entry for $entries of them."
  exit 1
fi

echo "Attached to the draft on channel $channel:"
cat "$record/$channel.sha256"
