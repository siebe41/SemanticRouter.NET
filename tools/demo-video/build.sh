#!/usr/bin/env bash
# Encodes the frames record.js captured into a 1080p30 MP4 (H.264, plays everywhere) and a small GIF preview.
#   ./build.sh <frames-dir> <out.mp4>
set -euo pipefail
frames="${1:?frames directory}"
out="${2:?output .mp4}"

ffmpeg -y -loglevel error -f concat -safe 0 -i "$frames/frames.txt" \
  -vf "fps=30,scale=1920:1080:flags=lanczos,format=yuv420p" \
  -c:v libx264 -preset slow -crf 18 -movflags +faststart "$out"

# A 960-wide GIF for places that will not autoplay video (README, chat).
gif="${out%.*}.gif"
ffmpeg -y -loglevel error -i "$out" \
  -vf "fps=12,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=4" \
  "$gif"

ls -la "$out" "$gif"
