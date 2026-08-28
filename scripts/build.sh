#!/usr/bin/env bash
# Builds the plugin binaries and assembles the .sdPlugin folder (plugin/com.josbol.aiagentmonitor.sdPlugin).
#   ./scripts/build.sh                 # linux-x64 (fast, for development)
#   RIDS="linux-x64 linux-arm64" ./scripts/build.sh   # every architecture listed in manifest.json (releases)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN="$ROOT/plugin/com.josbol.aiagentmonitor.sdPlugin"
RIDS="${RIDS:-linux-x64}"
CONFIG="${CONFIG:-Release}"

for RID in $RIDS; do
  OUT="$PLUGIN/bin/$RID"
  rm -rf "$OUT"
  dotnet publish "$ROOT/src/Opendeck.AiAgentMonitor/Opendeck.AiAgentMonitor.csproj" -c "$CONFIG" -r "$RID" -o "$OUT" --nologo -v quiet \
    -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
  chmod +x "$OUT/opendeck-aiagentmonitor"
  rm -f "$OUT"/*.pdb
  mkdir -p "$OUT/fonts" && cp "$PLUGIN/assets/fonts/"*.ttf "$OUT/fonts/"
  echo "built: $OUT/opendeck-aiagentmonitor ($(du -sh "$OUT" | cut -f1))"
done
