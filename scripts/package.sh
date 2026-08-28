#!/usr/bin/env bash
# Builds every architecture and zips the .sdPlugin folder for OpenDeck's "Install from file" / GitHub releases.
#   ./scripts/package.sh            → dist/com.josbol.aiagentmonitor.sdPlugin-<version>.zip
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ID="com.josbol.aiagentmonitor.sdPlugin"
PLUGIN="$ROOT/plugin/$ID"
VERSION="$(python3 -c "import json;print(json.load(open('$PLUGIN/manifest.json'))['Version'])")"
RIDS="${RIDS:-linux-x64 linux-arm64}" "$ROOT/scripts/build.sh"

mkdir -p "$ROOT/dist"
OUT="$ROOT/dist/$ID-$VERSION.zip"
rm -f "$OUT"
( cd "$ROOT/plugin" && zip -qr "$OUT" "$ID" -x "$ID/bin/*/fonts/*.bak" )
echo "packaged: $OUT ($(du -sh "$OUT" | cut -f1))"
