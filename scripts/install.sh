#!/usr/bin/env bash
# Installs the plugin into OpenDeck's plugin directory.
#   ./scripts/install.sh          copy
#   ./scripts/install.sh --link   symlink (needs OpenDeck developer mode; best for development)
#   ./scripts/install.sh --uninstall
# Afterwards: restart OpenDeck, or `opendeck --reload-plugin com.josbol.aiagentmonitor.sdPlugin`.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ID="com.josbol.aiagentmonitor.sdPlugin"
SRC="$ROOT/plugin/$ID"
if [ -d "$HOME/.var/app/me.amankhanna.opendeck/config/opendeck" ] && ! [ -d "$HOME/.config/opendeck" ]; then
  DEST_DIR="$HOME/.var/app/me.amankhanna.opendeck/config/opendeck/plugins"
else
  DEST_DIR="$HOME/.config/opendeck/plugins"
fi
DEST="$DEST_DIR/$ID"
mkdir -p "$DEST_DIR"
case "${1:-}" in
  --uninstall) rm -rf "$DEST"; echo "removed $DEST" ;;
  --link) rm -rf "$DEST"; ln -s "$SRC" "$DEST"; echo "linked $DEST -> $SRC" ;;
  *) rm -rf "$DEST"; cp -r "$SRC" "$DEST"; echo "copied to $DEST" ;;
esac
if command -v opendeck >/dev/null 2>&1 && pgrep -x opendeck >/dev/null 2>&1; then
  opendeck --reload-plugin "$ID" >/dev/null 2>&1 && echo "asked OpenDeck to reload the plugin" || true
fi
