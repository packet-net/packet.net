# pdn local-session app wire - `pdn-app/1`

**Status:** v1, agreed 2026-06-10. The "floor" of the local-session seam in [`app-extensibility.md`](app-extensibility.md): an external process, spawned per connect, with the connected user's session piped over stdio. This is the contract an app author writes against - deliberately tiny and language-agnostic. A useful app is ~30 lines in any language that can read stdin and write stdout.

## The deal

When a connected user invokes a registered application (by typing its `match` verb at the node prompt), pdn:

1. **Spawns** the app's configured `command` (+ `args`), redirecting the child's stdin, stdout and stderr.
2. **Writes a connect header** to the child's **stdin**.
3. **Bridges the session** to the child's stdin/stdout as line-oriented UTF-8 text until the user disconnects or the app exits.
4. **Tears the child down** when the user goes away; **returns the user to the node prompt** when the app exits first.

The app needs to know *nothing* about AX.25, NET/ROM, telnet, callsign framing, or line-ending conventions. It reads lines, it writes lines.

## 1. The connect header

Before any session traffic, pdn writes a header to the child's stdin: a sequence of `Key: Value` lines (UTF-8, each terminated by a single `\n`), ended by **one blank line** (`\n`). Read lines until you hit the blank line; that's the whole header.

```
pdn-app: 1
id: wall
callsign: M0LTE-7
transport: ax25
port: gb7rdg
sysop: 0
args: last 5

```

| Key | Meaning |
|---|---|
| `pdn-app` | Wire version. `1` today. Always the first line. |
| `id` | The registered application id that was launched. |
| `callsign` | The connecting station - an AX.25 callsign (with SSID) for radio transports; a remote endpoint string for telnet. Already canonicalised; treat as opaque text. |
| `transport` | `ax25`, `netrom`, or `telnet`. |
| `port` | The arrival port id when known; `-` (or absent) for telnet / network-arrived sessions. |
| `sysop` | `1` if the launching session was sysop-elevated, else `0`. (Reserved; always `0` in v1.) |
| `args` | The tokens the user typed after the `match` verb, space-joined. May be empty. |

**Forward-compatibility rule:** an app **MUST ignore header keys it does not recognise**. New keys will be added; ignoring unknowns is how a v1 app keeps working against a later node.

## 2. Session traffic

After the blank line, the streams carry the user's session as line-oriented UTF-8 text:

- **pdn → app (the child's stdin):** each line the user sent, as UTF-8 text terminated by a **single `\n`**. pdn has already stripped the transport's CR / CR-LF and re-terminated, so one user input line is exactly one `\n`-terminated line on stdin. Read it with an ordinary line read.
- **app → pdn (the child's stdout):** write UTF-8 text using `\n` line endings. pdn translates each `\n` to the connecting transport's newline (a bare `CR` for AX.25 / NET/ROM, `CR-LF` for telnet) before sending it to the user. **Never emit `\r` yourself** - let pdn do the translation, and the same app works over every transport.
- **the child's stderr:** captured into the node log for diagnostics. Never shown to the user. Log freely here.

Flush stdout after writing a prompt or a reply - a block-buffered app will appear to hang to the user. (In Python, run unbuffered or call `sys.stdout.flush()`; most languages flush on newline when stdout is a pipe only if line-buffered, which pipes are usually *not*.)

## 3. Lifecycle and teardown

- **User disconnects:** pdn closes the child's stdin (the child sees **EOF** on stdin). A well-behaved app exits on stdin EOF. pdn then waits a short grace period and, if the child is still alive, terminates its process tree. So: **treat stdin EOF as "the user is gone - exit."**
- **App exits first:** when the app writes its goodbye and returns (exit code ignored in v1), pdn returns the user to the node prompt. This is the normal "the user finished with the app" path.
- **App fails to start** (bad `command`, missing interpreter): pdn tells the user the application is unavailable and returns them to the prompt - the node never crashes because an app is misconfigured.

## 4. What the node guarantees (and does not)

Guarantees: one child process per connect (no shared state across users at the floor - that's the "next rung", a long-running local socket); a clean UTF-8 line-oriented duplex stream; newline translation; stderr capture; teardown on disconnect.

Does **not** (in v1): sandbox the child (the node *owner* chose to run it - see the trust model in [`app-extensibility.md`](app-extensibility.md)); pass node secrets (the child inherits a minimal environment); deliver raw bytes (the floor is text/line-oriented - an app needing a raw byte stream or shared cross-user state uses the long-running-socket rung, a later slice); expose a web UI (that's the human-plane gateway, a later slice).

## 5. Registering an app

A `pdn-app/1` app is one entry in the node's `applications:` config list (hot-reloaded like `ports`/`beacons` - the next connect picks up the change, since each connect spawns fresh):

```yaml
applications:
  - id: wall
    match: WALL                 # the console verb that launches it (case-insensitive, exact match)
    enabled: true
    kind: process
    command: /usr/bin/python3
    args: [ /usr/share/packetnet/apps/wall/wall.py ]
    workingDirectory: /var/lib/packetnet/apps/wall
    capabilities: [ session ]    # declared capabilities; in v1 only "session" is meaningful
```

The user types `WALL` (or `WALL last 5`) at the node prompt; pdn spawns the app, writes the header (with `args: last 5`), and bridges the session. Built-in console verbs always win, so an app cannot shadow `BYE`/`CONNECT`/etc. - the validator rejects a `match` that collides with a built-in.

See [`examples/wall/`](../examples/wall/) for the worked example this contract is documented around.

## 6. The long-running-socket rung (`kind: socket`)

The stdio floor spawns a fresh process per connect, so each user is isolated - fine for a wall or a menu, but it can't let users see or message **each other**. The next rung keeps the *exact same `pdn-app/1` wire* but changes the transport: the app is a **long-running daemon** that listens on a **Unix-domain socket**, and the node opens a **new connection to that socket per connect**, bridging the session over it. Because the daemon holds every live connection, it can keep **shared in-memory state across users** and **push unsolicited output** (a broadcast appears in another user's session live).

```yaml
applications:
  - id: lobby
    match: LOBBY
    enabled: true
    kind: socket
    socketPath: /run/packetnet/lobby.sock   # the daemon's listening socket
    capabilities: [ session ]
```

For an app author the contract is identical to the floor - **per accepted connection**: read the connect header (§1), then exchange line-oriented UTF-8 (§2: read the user's `\n`-terminated lines, reply with `\n` line endings only, the node translates). The differences:

- **Your program is the server, the node is the client.** Bind the Unix socket, `listen`, `accept` in a loop, handle each accepted connection concurrently (one connection = one user session). The owner runs your daemon (e.g. a systemd unit) - **the node does not manage its lifecycle**, it only connects (so a daemon that isn't listening just reports the app unavailable to the user).
- **Bind loopback / local only** and set the socket mode so the node's service user can connect (the owner owns trust - the daemon is reachable only on the local machine).
- **EOF = that user left.** When the user disconnects, the node closes the connection; your `recv` for it returns empty. Drop them from your shared state (and tell the others, if you broadcast presence).
- **Shared state is yours to hold.** Guard it (a lock) - multiple connections run concurrently. A write to a vanished peer may fail; catch it and reap that entry rather than crashing the broadcast.

See [`examples/lobby/`](../examples/lobby/) for the worked example - a presence + broadcast "lobby" where `WHO` lists who's connected right now and `SAY` reaches every connected user, which the spawn-per-connect floor structurally cannot do.
