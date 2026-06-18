# Config in the database (`SqliteConfigProvider`)

As of node v0.17 (#473) the node's **live configuration lives in `pdn.db`**, not in a watched `/etc/packetnet/packetnet.yaml` file. This is Phase 4 Slice-2's deferred `SqliteConfigProvider`, landed behind the unchanged `IConfigProvider` / `IWritableConfigProvider` / `NodeConfig` seam, so the reconcile path and every consumer are byte-for-byte the same — only *where config is stored* changed.

## Why

The old `FileConfigProvider` rewrote its own YAML conffile through the management API (config-write, port CRUD, first-run `/setup`). Because that file was a dpkg **conffile**, every upgrade compared md5s, saw the node's runtime mutations, and prompted the operator to keep or replace it — a recurring upgrade snag. Moving config into `pdn.db` (in the writable `StateDirectory`, which dpkg never tracks) removes the conffile entirely, so the prompt vanishes *structurally*.

## How it is stored

A single versioned JSON-blob row — **not** shredded into structured tables. `NodeConfig` is a deep, polymorphic, frequently-evolving record tree (a `kind`-discriminated transport union, many additive sub-records); structured tables would duplicate the whole shape in DDL and demand a migration on every field add. One blob round-trips the *exact* model the provider produces, so it is provably zero behaviour change.

```sql
CREATE TABLE IF NOT EXISTS node_config (
    id          INTEGER PRIMARY KEY CHECK (id = 1),   -- singleton row
    schema_ver  INTEGER NOT NULL,                      -- NodeConfig.SchemaVersion
    format      TEXT NOT NULL,                         -- 'json'
    payload     TEXT NOT NULL,                         -- the serialised NodeConfig
    updated_utc TEXT NOT NULL);
```

The blob is **JSON**, serialised with the *same* `System.Text.Json` options the management API uses (`NodeConfigJson` — camelCase web defaults + the `TransportConfigJsonConverter`). So the structured `PUT /config` body and the on-disk DB bytes are byte-identical: one canonical serialisation, no second dialect to drift. JSON (not YAML) deliberately keeps YAML's comments/formatting out of the load-bearing bytes; the store compares config *values*, not text — which also removed the file watcher's `lastText` echo-suppression machinery.

The store (`SqliteConfigStore`) follows the resilient discipline of every other `pdn.db` store: `CREATE TABLE IF NOT EXISTS` (meta-less — it does not fight the routing store over `PRAGMA user_version`), WAL mode, a fresh pooled connection per call, every op `try/catch(SqliteException)`. A store fault never takes the node down.

## First-boot migration / seed

On the first 0.17 boot the `node_config` row is absent. The provider resolves a source in priority order and imports it:

1. **The `--config` YAML** (the lab's `/etc/packetnet/packetnet.yaml`) — read, `NodeConfigYaml.Parse`, validate, save into the row. This carries a hand-tuned config across the upgrade with **zero operator action**.
2. **`PACKETNET_CONFIG_SEED`** — an explicit seed-file path for a headless image to seed a full config without touching `/etc`.
3. **`/usr/share/packetnet/packetnet.yaml.example`** — the packaged bootstrap template.
4. **The in-code `NodeConfigTemplate`** — the ultimate fallback so the node *always* boots idle on `N0CALL` even with no files at all.

A present-but-**invalid** source **throws** (a node never boots on broken config — same invariant as the file provider). The import is **idempotent structurally**: it runs *only* when the row is absent, so a postinst re-run, a restart, or a downgrade-then-upgrade cannot double-import. Once the row exists, every boot loads from the DB and every API write goes to the DB; the YAML is never read again.

After a successful import the provider writes an informational marker `/var/lib/packetnet/.config-migrated` (source path + timestamp). The marker is **not** the idempotency authority (the row is) — it is purely so an operator can see the import happened and that the old YAML is safe to delete. The source YAML is **never auto-deleted** (rollback floor).

## Editing config now

The hot **hand-edit-the-file** flow is gone — a hand-edit of the vestigial YAML no longer hot-applies. Edit config via:

- **The web UI / control panel** (the primary path).
- **`PUT /api/v1/config`** (structured JSON) or **`PUT /api/v1/config/raw`** (raw YAML) — both persist to the DB and raise the same `OnChange` the file watcher used to, driving the same reconcile.
- **`pdn config export [--out <path>]`** — boot just the provider, write `NodeConfigYaml.Serialize(Current)` to stdout/file. Inspect, diff, back up.
- **`pdn config import <path>`** — boot just the provider, parse + validate + `TryApply` a YAML file (the explicit apply that replaces the old hot file-watch).

`GET /api/v1/config/raw` still serialises `Current` to YAML, so the human-facing YAML view is preserved. Together `export` + `import` give the full export → `$EDITOR` → import workflow on top of DB storage.

## Persist-before-advance

`TryApply` persists the candidate to the DB **before** advancing `Current`. A DB write failure therefore does **not** advance `Current` — the node never runs on un-persisted config; the edit surfaces as a failed apply (a `(store)` error) instead. This is stricter than the file provider and correct: with `pdn.db` shared under WAL, a wedged DB now blocks config writes too, but the degrade-safe store keeps the node *booting* on its in-memory current.

## Packaging

- `packaging/conffiles` is empty — no conffile is shipped, so dpkg never runs its conffile md5 comparison and never prompts on upgrade.
- `build-deb.sh` no longer stages the YAML to `/etc`; it stages the pristine template to `/usr/share/packetnet/packetnet.yaml.example` (the step-3 seed source).
- `packetnet.service` drops `/etc/packetnet` from `ReadWritePaths` (the node no longer rewrites `/etc`); the `--config` arg stays — it is read-only, the legacy-YAML import anchor + the documented inspect/export anchor.
- `postinst` keeps the chown/chmod-if-exists on a pre-existing `/etc` YAML for one release cycle (so the importer can read it); it does **not** create the dir or file.
- `postrm` purge explicitly removes `/etc/packetnet/packetnet.yaml*` (no longer dpkg-managed) and `rm -rf /var/lib/packetnet` (which now also drops the config).

## Rollback

A 0.17 → 0.16 downgrade finds the original `/etc` YAML still in place (we never deleted it) and `FileConfigProvider` boots on it. Edits made under 0.17 that live only in the DB are lost on downgrade — the expected, acceptable semantics for a forward migration, not a regression.
