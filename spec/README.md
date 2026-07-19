# RHPv2 wire grammar (CDDL)

This directory contains the machine-readable, language-neutral definition of the
RHPv2 (PWP-0222 / PWP-0245) JSON-over-TCP wire format, expressed in
[CDDL](https://datatracker.ietf.org/doc/html/rfc8610) (RFC 8610).

## Files

| File | Purpose |
|---|---|
| `rhp2.cddl` | The wire grammar — every message shape pdn's RhpServer emits |
| `vectors/rhp2-messages.json` | Conformance vectors — golden JSON payloads, one per wire shape |

## Usage

### Validate a JSON payload against the grammar

```sh
# Requires: cargo install cddl
echo '{"type":"auth","id":1,"user":"op","pass":"x"}' | cddl validate --cddl spec/rhp2.cddl --stdin
```

### Validate the full vectors corpus

```sh
python3 -c "
import json, subprocess, sys
for i, v in enumerate(json.load(open('spec/vectors/rhp2-messages.json'))):
    r = subprocess.run(['cddl','validate','--cddl','spec/rhp2.cddl','--stdin'],
                       input=json.dumps(v), capture_output=True, text=True)
    if r.returncode != 0:
        print(f'FAIL [{i}]: {r.stderr}'); sys.exit(1)
print('All vectors pass')
"
```

### In CI (pdn)

The `CddlWireConformanceTests` class in `tests/Packet.Rhp2.Tests/` serializes
every message type the codec emits and validates each against `rhp2.cddl`. A
code change that alters the wire shape fails the build.

## Cross-implementation conformance

Any RHPv2 implementation (server or client) can validate its wire output against
this grammar:

1. Capture the JSON payloads your implementation emits (one per line, or as an array).
2. Validate each against `rhp2.cddl` using the `cddl` CLI.
3. Replay `vectors/rhp2-messages.json` through your parser — every vector must
   parse without error and produce the expected semantics.

Divergence from the grammar means either:
- Your implementation has drifted from the wire contract, or
- The grammar needs a new row (a deliberate, documented deviation — see
  `docs/rhp2-server.md` § Named deviations).

Either way, it goes in the grammar and the tables — never silently into code.

## Scope

The grammar covers the DAPPS-proven subset (R-1 through R-7): `ax25` family,
`stream` / `dgram` / `custom` modes, the full request/reply/push message
surface. Deferred families (`netrom`, `inet`, `unix`) and modes (`raw`,
`trace`, `seqpkt`, `semiraw`) are not yet pinned.

## Provenance

Transcribed from `Packet.Rhp2` (the pdn codec), pinned against live XRouter
(image label 505c) and RHPTEST (v505d). Wire-fidelity deltas and named
deviations are annotated inline in the grammar.
