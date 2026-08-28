#!/usr/bin/env python3
"""Creates the dedicated "AI Agents" OpenDeck profile for the Ulanzi D200X (and optionally adds an
Attention key to the main profile).

  scripts/install-profile.py                 # write profiles/<device>/AI Agents.json (backs up an existing one)
  scripts/install-profile.py --main-key 4    # also put an "Attention → Monitor" key on slot 4 of the main profile
  scripts/install-profile.py --device ulanzi-d200x --main Default --name "AI Agents"

OpenDeck caches loaded profiles in memory: the new profile is picked up when it is first selected,
but a change to the main profile only shows after OpenDeck is restarted.
"""
import argparse, json, os, shutil, sys, time

PLUGIN = "com.josbol.aiagentmonitor.sdPlugin"
PREFIX = "com.josbol.aiagentmonitor."

def config_dir():
    for d in (os.path.expanduser("~/.config/opendeck"), os.path.expanduser("~/.var/app/me.amankhanna.opendeck/config/opendeck")):
        if os.path.isdir(d):
            return d
    sys.exit("OpenDeck config directory not found")

def state(image, show=False):
    return {"alignment": "middle", "background_colour": "#000000", "colour": "#FFFFFF", "family": "Liberation Sans",
            "image": image, "image_scale": 100, "name": "", "show": show, "size": 16, "stroke_colour": "#000000",
            "stroke_size": 3, "style": "Regular", "text": "", "underline": False}

def load_manifest(cfg):
    with open(os.path.join(cfg, "plugins", PLUGIN, "manifest.json")) as f:
        return json.load(f)

def action_def(manifest, short):
    uuid = PREFIX + short
    a = next(x for x in manifest["Actions"] if x["UUID"] == uuid)
    icon = f"plugins/{PLUGIN}/{a['Icon']}@2x.png"
    enc = a.get("Encoder")
    encoder = None
    if enc:
        td = enc.get("TriggerDescription", {})
        encoder = {"background": enc.get("background", ""), "icon": enc.get("Icon", ""), "layout": enc.get("layout", ""),
                   "stack_color": enc.get("StackColor", ""),
                   "trigger_description": {"long_touch": td.get("LongTouch", ""), "push": td.get("Push", ""), "rotate": td.get("Rotate", ""), "touch": td.get("Touch", "")}}
    return {
        "controllers": a.get("Controllers", ["Keypad"]),
        "disable_automatic_states": a.get("DisableAutomaticStates", False),
        "encoder": encoder,
        "icon": icon,
        "name": a["Name"],
        "plugin": PLUGIN,
        "property_inspector": f"plugins/{PLUGIN}/{a['PropertyInspectorPath']}" if a.get("PropertyInspectorPath") else "",
        "states": [state(f"plugins/{PLUGIN}/{s['Image']}@2x.png", s.get("ShowTitle", True)) for s in a["States"]],
        "supported_in_multi_actions": a.get("SupportedInMultiActions", False),
        "tooltip": a.get("Tooltip", ""),
        "uuid": uuid,
        "visible_in_action_list": a.get("VisibleInActionsList", True),
    }

def instance(manifest, short, controller, position, settings=None):
    a = action_def(manifest, short)
    return {"action": a, "children": None, "context": f"{controller}.{position}.0", "current_state": 0,
            "settings": settings or {}, "states": [dict(s) for s in a["states"]]}

def retarget(inst, controller, position):
    """Copy an instance from another profile onto a new slot."""
    if inst is None:
        return None
    inst = json.loads(json.dumps(inst))
    inst["context"] = f"{controller}.{position}.0"
    return inst

def build_profile(manifest, main_profile, name):
    keys = [None] * 17   # 5x3 grid (0-14) + 2 touchpoints (15, 16)
    sliders = [None] * 3
    keys[0] = instance(manifest, "quota", "Keypad", 0, {"provider": "claude"})
    keys[1] = instance(manifest, "quota", "Keypad", 1, {"provider": "codex"})
    keys[2] = instance(manifest, "overview", "Keypad", 2)
    keys[3] = instance(manifest, "selected", "Keypad", 3)
    keys[4] = instance(manifest, "attention", "Keypad", 4, {"mode": "back"})
    for slot, pos in enumerate(range(5, 11), start=1):        # 5..10 → agent slots 1..6
        keys[pos] = instance(manifest, "agent", "Keypad", pos, {"slot": slot, "provider": "auto"})
    keys[11] = instance(manifest, "approve", "Keypad", 11)
    keys[12] = instance(manifest, "deny", "Keypad", 12)
    keys[13] = instance(manifest, "overview", "Keypad", 13)     # the wide screen (shown when the D200X "Wide screen" mode is "Action icon")
    keys[14] = retarget((main_profile or {}).get("keys", [None] * 17)[14] if main_profile else None, "Keypad", 14)  # D200X wide-screen settings action, if present
    keys[15] = instance(manifest, "approve", "Keypad", 15)      # side button 1: approve (no display)
    keys[16] = instance(manifest, "deny", "Keypad", 16)         # side button 2: deny (no display)
    sliders[0] = instance(manifest, "dial", "Encoder", 0)
    if main_profile:
        ms = main_profile.get("sliders", [])
        # keep the user's volume dials reachable on the monitoring layout
        sliders[1] = retarget(ms[1] if len(ms) > 1 else None, "Encoder", 1)
        sliders[2] = retarget(ms[0] if len(ms) > 0 else None, "Encoder", 2)
    return {"id": name, "keys": keys, "sliders": sliders, "infobars": []}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="ulanzi-d200x")
    ap.add_argument("--main", default="Default", help="main profile id (source of the wide-screen settings key and volume dials)")
    ap.add_argument("--name", default="AI Agents", help="name of the monitoring profile to create")
    ap.add_argument("--main-key", type=int, default=None, help="also place an Attention → Monitor key on this slot of the main profile (needs an OpenDeck restart)")
    args = ap.parse_args()

    cfg = config_dir()
    manifest = load_manifest(cfg)
    pdir = os.path.join(cfg, "profiles", args.device)
    os.makedirs(pdir, exist_ok=True)
    main_path = os.path.join(pdir, f"{args.main}.json")
    main_profile = json.load(open(main_path)) if os.path.exists(main_path) else None

    out = os.path.join(pdir, f"{args.name}.json")
    if os.path.exists(out):
        shutil.copy(out, out + f".bak-{int(time.time())}")
    with open(out, "w") as f:
        json.dump(build_profile(manifest, main_profile, args.name), f, indent=2)
    print(f"wrote {out}")

    if args.main_key is not None and main_profile is not None:
        k = args.main_key
        if main_profile["keys"][k] is not None:
            sys.exit(f"slot {k} of {args.main} is occupied by {main_profile['keys'][k]['action']['uuid']}; pick a free one")
        shutil.copy(main_path, main_path + f".bak-{int(time.time())}")
        main_profile["keys"][k] = instance(manifest, "attention", "Keypad", k, {"mode": "monitor"})
        with open(main_path, "w") as f:
            json.dump(main_profile, f, indent=2)
        print(f"added Attention → Monitor to slot {k} of {main_path} (restart OpenDeck to load it)")

if __name__ == "__main__":
    main()
