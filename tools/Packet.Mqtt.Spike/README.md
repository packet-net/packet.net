# Packet.Mqtt.Spike

SP-001 - LinBPQ MQTT frame-feed ingestion spike. See `docs/plan.md` §5.X.

Tom's LinBPQ node publishes every AX.25 frame sent/received across its 4 RF
ports to an MQTT broker on his LAN. We subscribe, parse, and feed the corpus
into our AX.25 parser to surface real-world edge cases that synthetic tests
don't generate.

## Modes

### `probe` - exploratory subscription

```sh
dotnet run --project tools/Packet.Mqtt.Spike -- probe \
  --broker 10.45.0.70:1883 \
  --topic 'PACKETNODE/#' \
  --seconds 30
```

Subscribes for the configured window, dumps each received message (topic +
first-64-byte hex preview) to stdout and `artifacts/mqtt-probe/<ts>/`:

- `messages.jsonl` - one JSON line per message with topic, length, hex preview, ASCII preview, ts
- `summary.md` - final topic-frequency table + payload-size distribution

Used to learn the wire format before promoting the spike to structured
parsing. Without args defaults to `10.45.0.70:1883` and `PACKETNODE/#`.

### `collect` - long-running daemon, daily SQLite rotation

```sh
dotnet run --project tools/Packet.Mqtt.Spike -- collect \
  --broker 10.45.0.70:1883 \
  --topic 'PACKETNODE/#' \
  --out-dir /var/lib/packet-mqtt-collector \
  --filename-prefix gb7rdg
```

Streams every received MQTT message to per-day SQLite files
(`<prefix>-YYYY-MM-DD.sqlite`) and runs indefinitely. Re-connects on
broker disconnect with exponential backoff. Graceful shutdown on
SIGTERM/SIGINT - drains the in-flight channel and checkpoints WAL.

Schema (denormalised topic components on ingest, no AX.25 parsing -
that's offline):

```sql
CREATE TABLE messages (
  id           INTEGER PRIMARY KEY,
  ts_utc_us    INTEGER NOT NULL,
  topic        TEXT    NOT NULL,
  format       TEXT,             -- 'kiss', 'ax25/trace/bpqformat'
  node         TEXT,             -- e.g. 'GB7RDG'
  direction    TEXT,             -- 'rcvd' or 'sent'
  port         INTEGER,
  payload      BLOB    NOT NULL,
  payload_len  INTEGER NOT NULL
);

CREATE TABLE run_meta (
  run_id          INTEGER PRIMARY KEY AUTOINCREMENT,
  started_at_us   INTEGER,
  ended_at_us     INTEGER,       -- last-seen-ts via trigger
  client_id       TEXT,
  message_count   INTEGER NOT NULL DEFAULT 0,
  reconnect_count INTEGER NOT NULL DEFAULT 0
);
```

A trigger increments `run_meta.message_count` + updates `ended_at_us` on
every insert, so the meta is always accurate without a separate
heartbeat path.

See [`deploy/README.md`](deploy/README.md) for deploying this as a
systemd service on the LinBPQ host.

### `monitor` - structured ingestion (stub)

Not yet implemented. Would parse each captured payload through
`Ax25Frame.TryParse` + the round-trip discipline (mirroring the AprsIs
spike). Lands once we have enough corpus to drive it against. For now,
analysis happens offline against the SQLite files written by `collect`.

## Args reference

| Flag | Default | Meaning |
|---|---|---|
| `--broker <host[:port]>` | `10.45.0.70:1883` | broker endpoint |
| `--topic <topic>` | `PACKETNODE/#` | subscription filter |
| `--seconds <N>` | `30` | probe window |
| `--out-dir <dir>` | `artifacts/mqtt-<mode>/<ts>/` | artifact directory |

## Scope

v0.1 spike. Read-only consumer. No state, no persistence beyond artifacts.
Will graduate to `src/Packet.Replay.Mqtt/` once the format is understood
and we want a real ingestion pipeline.
