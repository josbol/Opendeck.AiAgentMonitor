#!/usr/bin/env python3
"""Observe agy's documented status-line feed; preserve any previous status script.

No permission hooks, API requests, credentials, prompts or transcript copies.
The status command may be launched via sh, so find the owning agy through /proc.
"""
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def is_agy(pid, comm):
    """agy's self-updater renames the running binary to agy.<nanos>.old (and may unlink
    it), so the exe link stops ending in 'agy'; comm keeps the exec'd name either way."""
    if comm == "agy":
        return True
    try:
        name = Path(os.readlink(f"/proc/{pid}/exe")).name.removesuffix(" (deleted)")
    except OSError:
        return False  # a process we cannot inspect is never the owning agy
    return name == "agy" or re.fullmatch(r"agy\.\d+\.old", name) is not None


def owner():
    pid = os.getppid()
    for _ in range(32):
        if pid <= 1:
            break
        try:
            stat = Path(f"/proc/{pid}/stat").read_text()
        except OSError:
            break  # an ancestor vanished or is another user's; the walk cannot continue
        fields = stat[stat.rfind(")") + 2:].split()
        if is_agy(pid, stat[stat.find("(") + 1:stat.rfind(")")]):
            return pid, fields[19]
        pid = int(fields[1])
    return None


def number(value):
    return value if isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value) else None


def observe(data, identity, state_dir):
    session = data.get("conversation_id") or data.get("session_id")
    if not identity or not isinstance(session, str) or not session:
        return
    pid, ticks = identity
    boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
    key = hashlib.sha256(f"{pid}:{ticks}:{boot}".encode()).hexdigest()
    state_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
    target = state_dir / (key + ".json")
    now = datetime.datetime.now(datetime.timezone.utc).isoformat()
    try:
        old = json.loads(target.read_text())
    except (OSError, ValueError):
        old = {}
    state = data.get("agent_state")
    waiting = data.get("tool_confirmation_pending") is True
    context = data.get("context_window") or {}
    model = data.get("model") or {}
    quotas = {}
    for name, value in (data.get("quota") or {}).items():
        if not isinstance(value, dict):
            continue
        fraction = number(value.get("remaining_fraction"))
        if fraction is not None:
            quotas[name] = {"remaining_fraction": fraction, "reset_time": value.get("reset_time")}
    same_session = old.get("session_id") == session
    record = {
        "session_id": session, "pid": pid, "start_ticks": ticks, "boot_id": boot,
        "cwd": data.get("cwd") or (data.get("workspace") or {}).get("current_dir", ""),
        "agent_state": state, "tool_confirmation_pending": waiting,
        "model": model.get("display_name") or model.get("id"),
        "context_pct": number(context.get("used_percentage")),
        "context_tokens": number((context.get("current_usage") or {}).get("input_tokens")),
        "task_count": number(data.get("task_count")),
        "quota": quotas, "plan_tier": data.get("plan_tier"),
        "observed_at": now,
        "started_at": old.get("started_at", now) if same_session else now,
        "state_since": old.get("state_since", now) if same_session and old.get("agent_state") == state and old.get("tool_confirmation_pending") == waiting else now,
    }
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", dir=state_dir, delete=False) as output:
            temporary = output.name
            json.dump(record, output, allow_nan=False)
            output.write("\n")
        os.replace(temporary, target)
    finally:
        if temporary and os.path.exists(temporary):
            os.unlink(temporary)


def previous_status(raw, backup):
    try:
        original = json.loads(backup.read_text()).get("statusLine") or {}
        if original.get("enabled") is False:
            return
        command = original.get("command")
        if command and "antigravity-status.py" not in command:
            # Same shell and stdin contract as agy; preserve the user's display, never persist its payload.
            result = subprocess.run(command, shell=True, input=raw, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=2)
            sys.stdout.write(result.stdout)
    except Exception:
        pass


def main():
    raw = sys.stdin.read()
    try:
        observe(json.loads(raw), owner(), Path(sys.argv[1]))
    except Exception:
        pass  # status monitoring must never interrupt the CLI or echo its private payload
    if len(sys.argv) > 2:
        previous_status(raw, Path(sys.argv[2]))


if __name__ == "__main__":
    main()
