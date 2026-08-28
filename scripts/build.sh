#!/usr/bin/env bash
# Builds the plugin binary and assembles the .sdPlugin folder (plugin/com.josbol.aiagentmonitor.sdPlugin).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN="$ROOT/plugin/com.josbol.aiagentmonitor.sdPlugin"
RID="${RID:-linux-x64}"
CONFIG="${CONFIG:-Release}"

dotnet publish "$ROOT/src/Opendeck.AiAgentMonitor/Opendeck.AiAgentMonitor.csproj" -c "$CONFIG" -r "$RID" -o "$PLUGIN/bin" --nologo -v quiet
chmod +x "$PLUGIN/bin/opendeck-aiagentmonitor"
mkdir -p "$PLUGIN/bin/fonts"
cp "$PLUGIN/assets/fonts/"*.ttf "$PLUGIN/bin/fonts/"
# the single-file publish leaves nothing else we need; drop debug leftovers
rm -f "$PLUGIN/bin/"*.pdb
echo "built: $PLUGIN/bin/opendeck-aiagentmonitor ($(du -sh "$PLUGIN/bin" | cut -f1))"
