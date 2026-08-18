# Packet.AprsIs.Spike

SP-001b - APRS-IS UI-frame corpus ingestion (see `docs/plan.md` §5.X).

Connects to APRS-IS as a read-only listener, parses TNC2-format frames,
attempts to reconstruct each as `Ax25Frame.Ui(...)`, round-trips through
our parser, and reports parser robustness against real-world traffic.

The point isn't to be an APRS gateway - it's to drive a steady stream of
real-world data through our AX.25 plumbing and surface edge cases that
synthetic tests don't cover.

## Modes

### `oneshot` - original short-window pipeline

```sh
dotnet run --project tools/Packet.AprsIs.Spike -- oneshot --max-frames 1000
```

Connect, read up to N frames, run each through TNC2 parse →
`Ax25Frame.Ui` reconstruct → encode-then-decode round-trip. Persist
failures to `artifacts/aprs-is-spike/<ts>/failures.jsonl` and a
`stats.md` summary.

### `collect` - long-running daemon, daily SQLite rotation

```sh
dotnet run --project tools/Packet.AprsIs.Spike -- collect \
  --out-dir /var/lib/packet-aprs-collector \
  --filename-prefix aprs-is
```

Streams every TNC2 line into per-day SQLite files
(`<prefix>-YYYY-MM-DD.sqlite`). Reconnects on TCP drop with exponential
backoff; graceful shutdown on SIGTERM/SIGINT. Inserts batched in
transactions (commit every 100 lines or 500 ms) to handle firehose
volume - a 30 s smoke caught ~90 lines/sec (~22 MB/min, ~1.3 GB/day at
filter `t/poimqstuc`).

Schema (mirrors the MQTT collector):

```sql
CREATE TABLE lines (
  id           INTEGER PRIMARY KEY,
  ts_utc_us    INTEGER NOT NULL,
  source       TEXT,           -- TNC2 parse result
  destination  TEXT,
  digi_path    TEXT,           -- comma-joined VIA list with * markers
  digi_count   INTEGER,
  info_len     INTEGER,
  info         BLOB,
  raw_line     TEXT NOT NULL   -- full TNC2 line as received
);
```

An `AFTER INSERT` trigger keeps `run_meta.line_count` + `ended_at_us`
exact. The collector doesn't parse the AX.25 envelope - that's offline
against the corpus by design, so parser changes can be re-run.

### `direwolf` - pipe corpus through direwolf as a reference decoder

```sh
dotnet run --project tools/Packet.AprsIs.Spike -- direwolf \
  --data-dir /home/tf/aprs-is-data
```

For every `lines` row, pipes the TNC2 line (with APRS-IS q-construct
stripped per direwolf's recommended `,qA.*:` regex) through
`/usr/bin/decode_aprs` and writes the structured output back into a
sibling `direwolf_decoded` table in the same SQLite file:

```sql
CREATE TABLE direwolf_decoded (
  line_id      INTEGER PRIMARY KEY,
  decoded_type TEXT,           -- e.g., 'Position with time, Repeater, SharkRF openSPOT4'
  latitude     REAL,           -- decimal degrees
  longitude    REAL,
  altitude_m   REAL,
  comment      TEXT,
  has_error    INTEGER NOT NULL DEFAULT 0,
  error_first  TEXT,
  raw_output   TEXT NOT NULL,  -- direwolf's full per-frame output
  FOREIGN KEY (line_id) REFERENCES lines(id)
);
```

This gives the corpus a **reference interpretation** for every line -
ground truth from the most authoritative public APRS decoder. When we
eventually build our own decoders, every diff against
`direwolf_decoded` is a candidate bug (in us, in direwolf, or in the
upstream sender).

`--reprocess` re-runs over rows that already have direwolf output;
default is resume-style (skip rows already processed). `--limit N`
caps; `--batch-size N` controls the chunk-per-subprocess.

### Common args

| Flag | Default | Meaning |
|---|---|---|
| `--server <host:port>` | `rotate.aprs2.net:14580` | APRS-IS server |
| `--callsign <CALL>` | `N0CALL` | login callsign (read-only via passcode -1) |
| `--filter <filter>` | `t/poimqstuc` | server-side filter (all APRS message types) |
| `--out-dir <dir>` | (mode-dependent) | artifact / data directory |
| `--filename-prefix <prefix>` | `aprs-is` | (collect) per-day SQLite file prefix |
| `--max-frames <N>` | `1000` | (oneshot) stop after N frames (0 = unlimited) |
| `--quiet` | off | (oneshot) suppress per-frame stdout |

## Known finding: APRS letter SSIDs

A first 30-frame smoke run produced **8 reconstruct failures, all from D-Star
gateways** using APRS letter SSIDs (`-B`, `-D`, `-T`, `-H`) - e.g.:

```
F5ZMN-SH>APSF40,TCPIP*,qAC,T2HUN:>Hardware: ESP8266 + BME280…
IZ6BDZ-B>APDG02,TCPIP*,qAC,IZ6BDZ-BS:!5000.00ND…  (D-Star B port)
DN9AMB-D>APDG03,TCPIP*,qAC,DN9AMB-DS:!5056.88N…   (D-Star D port)
```

These are **APRS conventions** that aren't valid AX.25 source addresses -
the spec only allows numeric SSIDs 0-15. APRS-IS leaks them through. Our
`Callsign.TryParse` correctly rejects them; the spike records each failure
to `failures.jsonl` for visibility.

This is the kind of real-world quirk the spike is designed to surface - the
existing synthetic tests don't generate letter SSIDs. Possible follow-ups:

- A separate "AprsCallsign" type that accepts letter SSIDs, for the APRS
  monitor view in the web UI (which needs to display these verbatim).
- Mapping rules from APRS letter-SSID to AX.25 numeric-SSID at the gateway
  layer (probably `B → 11`, `C → 12`, `D → 13` per common D-Star practice).

## Scope

v0.1 spike - UI frames only (the only frame type APRS-IS carries). No
persistence beyond `artifacts/`. No live ingestion to a regression corpus
(that's SP-003's job). Will be retired or graduated to `src/Packet.Replay.AprsIs/`
once the patterns are clear.
