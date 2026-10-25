#!/usr/bin/env python3
"""Fails if any package bundles a .NET 10 runtime older than the latest patch.

The node is self-contained, so each package carries the runtime of whichever SDK built it, and
an operating system update never patches it. A self-hosted runner with a stale SDK ships old
security fixes and nothing notices. This reads the runtime version out of the node in each
package and compares it with the latest 10.0 runtime Microsoft lists. A newer one passes: SDK workloads can
run ahead of the release index (the WebAssembly workload did on 2026-10-25, 10.0.14 against a listed 10.0.13),
and the point is to catch a stale runner, not one ahead of the index. The single-file release
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


def version_key(version):
    return tuple(int(part) for part in version.split("."))


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
            if len(found) != 1:
                print(f"::error::{package.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                      f"not one recognisable version. Check how {name} was built.")
                bad = True
            elif version_key(found[0]) < version_key(want):
                print(f"::error::{package.name} bundles .NET runtime {found[0]}, older than {want}. "
                      "Update the .NET 10 SDK on the runner that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
