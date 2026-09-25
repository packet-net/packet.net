#!/usr/bin/env python3
"""Regenerates src/Packet.Aprs/Data/aprs-deviceid.json from the APRS device identification
database (https://github.com/aprsorg/aprs-deviceid, CC BY-SA 2.0, maintained by OH7LZB).

Usage: scripts/update-aprs-deviceid.py <path to a clone of aprsorg/aprs-deviceid>

Contact addresses are dropped; everything else the library uses is kept as-is.
"""
import json, subprocess, sys, pathlib, yaml

src = pathlib.Path(sys.argv[1])
db = yaml.safe_load((src / "tocalls.yaml").read_text(encoding="utf-8"))
commit = subprocess.run(["git", "-C", str(src), "log", "-1", "--format=%H %cs"],
                        capture_output=True, text=True, check=True).stdout.strip()

def keep(entry, fields):
    return {k: entry[k] for k in fields if k in entry}

out = {
    "source": "https://github.com/aprsorg/aprs-deviceid",
    "licence": "CC BY-SA 2.0",
    "commit": commit,
    "classes": [keep(c, ["class", "shown", "description"]) for c in db["classes"]],
    "mice": [keep(m, ["suffix", "vendor", "model", "class", "features"]) for m in db["mice"]],
    "micelegacy": [keep(m, ["prefix", "suffix", "vendor", "model", "class", "features"]) for m in db["micelegacy"]],
    "tocalls": [keep(t, ["tocall", "vendor", "model", "class", "os", "features"]) for t in db["tocalls"]],
}
dest = pathlib.Path(__file__).resolve().parent.parent / "src/Packet.Aprs/Data/aprs-deviceid.json"
dest.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {dest} ({len(out['tocalls'])} tocalls, {len(out['mice'])} mice, {len(out['micelegacy'])} legacy) from {commit}")
