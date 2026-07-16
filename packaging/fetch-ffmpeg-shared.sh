#!/usr/bin/env bash
# Fetch the ffmpeg SHARED build required by the FFME video preview (T-019).
#
# FFME.Windows 7.0.361-beta.1 binds to FFmpeg.AutoGen 7.0.0 (ffmpeg 7.x ABI:
# avcodec-61 / avformat-61 / avutil-59 / ...). FFME does NOT ship the native libs.
# This downloads a matching ffmpeg 7.1 SHARED win64 build from BtbN and lays the
# DLLs + ffmpeg.exe / ffprobe.exe FLAT into the repo-local ffmpeg-shared/ folder.
#
# ffmpeg-shared/ is gitignored (root .gitignore). Run this once per dev/CI box that
# needs the FFME preview. App startup points Library.FFmpegDirectory here in dev.
#
# Source: https://github.com/BtbN/FFmpeg-Builds/releases (latest, n7.1 gpl shared)
set -euo pipefail

URL="${1:-https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${2:-$SCRIPT_DIR/../ffmpeg-shared}"

echo "Fetching ffmpeg SHARED build (ffmpeg 7.x, for FFME preview)..."
echo "  URL : $URL"
echo "  Dest: $DEST"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

curl -L "$URL" -o "$TMP/ffmpeg-shared.zip"
unzip -q -o "$TMP/ffmpeg-shared.zip" -d "$TMP/extract"

BIN="$(find "$TMP/extract" -type d -name bin | head -n1)"
[ -n "$BIN" ] || { echo "ERROR: bin/ not found in archive" >&2; exit 1; }

mkdir -p "$DEST"
cp "$BIN"/*.dll "$DEST"/
for exe in ffmpeg.exe ffprobe.exe; do
  [ -f "$BIN/$exe" ] && cp "$BIN/$exe" "$DEST"/
done

ls "$DEST"/avcodec-*.dll >/dev/null 2>&1 || { echo "ERROR: avcodec-*.dll missing after copy" >&2; exit 1; }

echo ""
echo "Done. ffmpeg-shared/ now contains:"
ls -1 "$DEST"
echo ""
echo "These files are gitignored and MUST NOT be committed."
