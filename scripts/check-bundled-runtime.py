#!/usr/bin/env python3
"""Fails unless every package bundles the latest .NET 10 runtime patch.

The node is self-contained, so each package carries the runtime of whichever SDK built it, and
an operating system update never patches it. A self-hosted runner with a stale SDK ships old
security fixes and nothing notices. This reads the runtime version out of the node in each
package and compares it with the latest 10.0 runtime Microsoft lists. The single-file release
binary embeds its deps.json; a loose publish (the container image tree) has packetnet.deps.json.

Usage: scripts/check-bundled-runtime.py <package.deb|package.tar.gz|directory>...
"""
import json
import re
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
PROGRAMS = ("packetnet", "packetnet.deps.json")
RUNTIME = re.compile(rb"runtimepack\.Microsoft\.(?:NETCore|AspNetCore)\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def programs_in(root):
    return [(str(p.relative_to(root)), p.read_bytes())
            for p in Path(root).rglob("*")
            if p.name in PROGRAMS and p.is_file() and not p.is_symlink()
            # a loose publish's apphost embeds nothing; its deps.json says which runtime it uses
            and not (p.name == "packetnet" and p.with_name("packetnet.deps.json").exists())]


def program_bytes(package):
    if package.is_dir():
        return programs_in(package)
    with tempfile.TemporaryDirectory() as tmp:
        if package.suffix == ".deb":
            subprocess.run(["dpkg-deb", "-x", str(package), tmp], check=True)
        else:
            with tarfile.open(package) as t:
                t.extractall(tmp, filter="data")
        return programs_in(tmp)


def main(packages):
    if not packages:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for package in map(Path, packages):
        programs = program_bytes(package)
        if not programs:
            print(f"::error::{package.name}: no program found to check")
            bad = True
        for name, data in programs:
            found = sorted({m.decode() for m in RUNTIME.findall(data)})
            print(f"{package.name}: {name} bundles {', '.join(found) or 'nothing recognisable'}")
            if found != [want]:
                print(f"::error::{package.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                      f"not {want}. Update the .NET 10 SDK on the runner that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
