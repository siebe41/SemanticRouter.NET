#!/usr/bin/env bash
# Downloads the site's videos into public/videos: the MP4s, poster frames (.poster.jpg) and captions (.srt or .vtt)
# attached to the GitHub release named in videos.json (site/README.md says how to add one). Captions in .srt become
# WebVTT, which is what browsers show. Needs gh, signed in (the Pages workflow passes its GITHUB_TOKEN).
set -euo pipefail
cd "$(dirname "$0")"

repository="${GITHUB_REPOSITORY:-siebe41/SemanticRouter.NET}"
release="$(jq -r .release videos.json)"
mkdir -p public/videos

if ! error="$(gh api "repos/$repository/releases/tags/$release" --silent 2>&1)"; then
  if [[ "$error" == *"HTTP 404"* ]]; then
    echo "::notice::There is no release tagged $release yet, so the site has no videos (site/README.md)."
    exit 0
  fi
  echo "$error" >&2
  exit 1
fi

gh release download "$release" --repo "$repository" --dir public/videos --clobber \
  --pattern '*.mp4' --pattern '*.jpg' --pattern '*.srt' --pattern '*.vtt'

for srt in public/videos/*.srt; do
  [ -e "$srt" ] || continue
  vtt="${srt%.srt}.vtt"
  [ -e "$vtt" ] && continue
  {
    echo WEBVTT
    echo
    sed -e 's/\r$//' -e 's/^\([0-9:]*\),\([0-9]*\) --> \([0-9:]*\),\([0-9]*\)/\1.\2 --> \3.\4/' "$srt"
  } >"$vtt"
done
ls -l public/videos
