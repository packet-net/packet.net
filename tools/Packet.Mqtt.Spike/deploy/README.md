# Deploying the collector on gb7rdg-node

One-time setup to run `Packet.Mqtt.Spike collect` as a systemd service on
the LinBPQ host, persisting MQTT traffic to daily-rotated SQLite files.

Assumes the host is `gb7rdg-node` reachable over SSH as root, running a
modern Debian/Ubuntu, with the broker on `127.0.0.1:1883` and topic
`PACKETNODE/#`.

## 1. Publish a self-contained binary

Run on a build machine that has the .NET 10 SDK installed. Pick the
runtime ID (`-r linux-arm64` for a Pi 5; `-r linux-x64` for amd64):

```sh
dotnet publish tools/Packet.Mqtt.Spike \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -o ./publish/packet-mqtt-collector
```

Result: `./publish/packet-mqtt-collector/Packet.Mqtt.Spike` - a standalone
binary with the .NET runtime + MQTTnet + SQLite baked in. Roughly 70-90 MB.

## 2. Copy onto the host

```sh
# From the build machine:
rsync -av ./publish/packet-mqtt-collector/ root@gb7rdg-node:/opt/packet-mqtt-collector/
rsync -av tools/Packet.Mqtt.Spike/deploy/packet-mqtt-collector.service \
  root@gb7rdg-node:/etc/systemd/system/packet-mqtt-collector.service
```

## 3. Create the service user + data dir

```sh
# Run on the host:
useradd --system --no-create-home --shell /usr/sbin/nologin packet-mqtt
mkdir -p /var/lib/packet-mqtt-collector
chown packet-mqtt:packet-mqtt /var/lib/packet-mqtt-collector
chmod 0750 /var/lib/packet-mqtt-collector
```

## 4. Enable + start

```sh
systemctl daemon-reload
systemctl enable --now packet-mqtt-collector
systemctl status packet-mqtt-collector
journalctl -u packet-mqtt-collector -f         # live tail
```

The unit logs to journal (filter by `SyslogIdentifier=packet-mqtt-collector`).
Daily SQLite files land in `/var/lib/packet-mqtt-collector/` named
`gb7rdg-YYYY-MM-DD.sqlite`, rotated automatically at UTC midnight.

## 5. Inspecting the corpus

```sh
sqlite3 /var/lib/packet-mqtt-collector/gb7rdg-$(date -u +%Y-%m-%d).sqlite

# Total messages today, by format / direction:
SELECT format, direction, COUNT(*)
FROM messages
GROUP BY format, direction;

# Per-port breakdown:
SELECT port, COUNT(*) FROM messages GROUP BY port ORDER BY port;

# Run history:
SELECT * FROM run_meta;

# Recent kiss/rcvd payloads (hex):
SELECT datetime(ts_utc_us / 1000000, 'unixepoch') AS ts, port, hex(payload)
FROM messages
WHERE format = 'kiss' AND direction = 'rcvd'
ORDER BY ts_utc_us DESC LIMIT 20;
```

## 6. Updating

Re-publish, rsync the binary over, `systemctl restart packet-mqtt-collector`.
The service's `Restart=on-failure` + the WAL-mode SQLite means a restart is
safe - no message loss for messages already written, brief gap for
in-flight ones.

## 7. Pulling the corpus back for analysis

```sh
# From the analysis workstation:
rsync -av root@gb7rdg-node:/var/lib/packet-mqtt-collector/*.sqlite ./corpus/
```

Then run any analysis script that opens the SQLite files. The collector
itself doesn't parse the AX.25 frames - that's deliberately offline so
parser changes can be re-run against a stable corpus.

## Notes

- **WAL files**: each `.sqlite` has companion `-shm` and `-wal` files while
  the writer is active. On the host they're owned by `packet-mqtt`. When
  copying for analysis, copy them too so SQLite has the full state - or
  run `PRAGMA wal_checkpoint(TRUNCATE);` to fold them into the main file
  first (the service does this implicitly on each rotation).
- **Quiet broker windows**: real RF traffic is sparse on most ports. A
  60-s smoke test may see 0-10 messages. A 24-h capture should produce
  several thousand. Don't read too much into short-window counts.
- **Schema**: see `../SqliteSink.cs` for the canonical schema. It's
  intentionally simple (one table + run_meta). Denormalised topic
  components are populated on ingest so queries don't have to re-parse.
