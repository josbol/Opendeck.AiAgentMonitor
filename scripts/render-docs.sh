#!/usr/bin/env bash
# Renders the sample keys into docs/keys.png for the README.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$ROOT/plugin/com.josbol.aiagentmonitor.sdPlugin/bin/linux-x64/opendeck-aiagentmonitor"
TMP="$(mktemp -d)"
"$BIN" --render "$TMP" >/dev/null
mkdir -p "$ROOT/docs"
python3 - "$TMP" "$ROOT/docs/keys.png" <<'PY'
import sys, os
from PIL import Image
src, out = sys.argv[1], sys.argv[2]
order = ["sample-working", "sample-waiting", "sample-idle", "sample-agent-approval", "sample-quota", "quota-codex",
         "sample-overview", "sample-attention", "sample-approve", "sample-deny", "sample-selected-approval", "empty-slot"]
files = [os.path.join(src, n + ".png") for n in order if os.path.exists(os.path.join(src, n + ".png"))]
cols = 6; rows = (len(files) + cols - 1) // cols; cell = 150
sheet = Image.new("RGB", (cols * cell, rows * cell), (30, 32, 38))
for i, f in enumerate(files):
    sheet.paste(Image.open(f).convert("RGB"), ((i % cols) * cell + 3, (i // cols) * cell + 3))
sheet.save(out, optimize=True); print("wrote", out, sheet.size)
PY
rm -rf "$TMP"
