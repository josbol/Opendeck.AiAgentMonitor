#!/usr/bin/env bash
# Copilot CLI permissionRequest hook → AI Agent Monitor (OpenDeck). Forwards the hook JSON from stdin to the
# plugin and prints whatever decision it returns ({"behavior":"allow"|"deny"}). If the plugin is not running,
# prints nothing and exits 0 so Copilot continues with its normal permission flow.
PORT="${AIAGENTMONITOR_PORT:-43117}"
HOLD="${AIAGENTMONITOR_HOLD:-40}"
curl -sS --max-time "$HOLD" -X POST -H 'Content-Type: application/json' --data-binary @- \
  "http://127.0.0.1:${PORT}/hooks/copilot" 2>/dev/null || true
exit 0
