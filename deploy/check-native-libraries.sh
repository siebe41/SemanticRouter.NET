#!/usr/bin/env bash
# Checks that a desktop publish holds the native libraries the app needs on its platform, so a release cannot ship a
# build where SQLite, the UI or the in-process embeddings fail to load:
#
#   deploy/check-native-libraries.sh <rid> <publish-dir>
#
# publish-desktop.sh runs it after every publish. ONNX Runtime (the in-process embeddings) is required everywhere except
# osx-x64: it publishes no build for Intel Macs, and there the router uses an embedding server instead (docs/desktop.md).
set -euo pipefail

rid="$1"
dir="$2"

case "$rid" in
  win-*)
    required=(e_sqlite3.dll libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll onnxruntime.dll)
    ;;
  linux-*)
    required=(libe_sqlite3.so libSkiaSharp.so libHarfBuzzSharp.so libonnxruntime.so)
    ;;
  osx-x64)
    required=(libe_sqlite3.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib libAvaloniaNative.dylib)
    known_missing=libonnxruntime.dylib
    ;;
  osx-*)
    required=(libe_sqlite3.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib libAvaloniaNative.dylib libonnxruntime.dylib)
    ;;
  *)
    echo "::error::check-native-libraries.sh does not know the native libraries of $rid; add them." >&2
    exit 1
    ;;
esac

missing=()
for library in "${required[@]}"; do
  [ -f "$dir/$library" ] || missing+=("$library")
done

if [ "${#missing[@]}" -gt 0 ]; then
  echo "::error::The $rid publish in $dir is missing ${missing[*]}." >&2
  exit 1
fi

if [ -n "${known_missing:-}" ]; then
  if [ -f "$dir/$known_missing" ]; then
    echo "$rid now ships $known_missing: require it in deploy/check-native-libraries.sh and update docs/desktop.md."
  else
    echo "$rid: $known_missing is not shipped (no ONNX Runtime build for Intel Macs; known, see docs/desktop.md)."
  fi
fi

echo "$rid: ${required[*]} present."
