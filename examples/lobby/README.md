# LOBBY - the long-running-socket worked example

LOBBY is a live multi-user **chat lobby** for stations connected to a packet.net (pdn) node. Connect to it and you see who else is here (`WHO`) and you can talk to all of them in real time (`SAY`): a line one station types lands *live* in every other connected station's session.

It exists as the worked example for the **next rung up** from [WALL](../wall/) on pdn's app-extensibility platform. WALL is the *floor*: the node spawns one fresh `wall.py` process per connect, so users are isolated processes that can only share state through a file. LOBBY is the *socket rung*: it is **one long-running daemon** that the node opens a new connection to for each user, so every user's session is handled inside the **same process**. That is the whole point of this rung - it can hold **shared in-memory state across all connected users** behind a lock. User B shows up in user A's `WHO`, and a line A sends with `SAY` is broadcast straight into B's session. The spawn-per-connect floor structurally cannot do that: separate processes share no memory, so there is no live "who else is here" and no cross-user broadcast.

Like WALL, LOBBY is a standalone **Python 3 program (stdlib only)** that knows **nothing** about the node's .NET internals. The only thing connecting them is the wire below. That language/process boundary is the demonstration: a pdn app can be written in any language, in its own process, fully arm's-length from the node - the node is just a broker that bridges a session to it.

## The wire it speaks - `pdn-app/1` over a Unix-domain socket

The floor (WALL) carries `pdn-app/1` over **stdio** (the node spawns the app and pipes the session). The socket rung carries the **same** `pdn-app/1` contract over a **Unix-domain stream socket** instead. The role is inverted: **the node is the client, LOBBY is the server**. LOBBY binds a `socket.AF_UNIX` / `SOCK_STREAM` path, `listen()`s, and `accept()`s in a loop; for **each** user that launches the app, the node opens a **new connection** to that socket. Each accepted connection is one bridged user session, handled on its own thread.

For each accepted connection:

- **Connect header (node → app, first).** A sequence of `Key: Value` lines (UTF-8, each `\n`-terminated), ended by **one blank line**. Keys: `pdn-app` (wire version `1`), `id` (the launched app id), `callsign` (the connecting station, opaque text e.g. `M0LTE-7`), `transport` (`ax25`|`netrom`|`telnet`), `port` (arrival port id or `-`), `sysop` (`0`/`1`), `args` (tokens typed after the launch verb). **Unknown keys must be ignored** - new keys appear over time, and ignoring them is how an app stays forward-compatible.
- **Session traffic (after the blank line).** Each line the user sends arrives as one `\n`-terminated UTF-8 line. The app replies with `\n` line endings **only**; the node translates `\n` to the transport's newline, so the app must **never emit `\r`**. The socket is buffered, so the app **flushes after every write** (an unflushed prompt looks like a hang). Crucially, the app may also write to a connection **unsolicited** - that is how a broadcast reaches a user who isn't currently typing; the node forwards whatever the app writes, whenever it writes it. Diagnostics go to **stderr** (the node log), never to a user.
- **Lifecycle.** When the user disconnects, the node closes the connection and the app's `recv` returns empty (**EOF**) - that means "this user is gone": drop them from the shared state and announce their departure. A quit command ends the session the same way.

That's the entire contract. See the module docstring at the top of [`lobby.py`](lobby.py) for the same summary alongside the code.

## Commands

All commands are case-insensitive and accept the abbreviations shown:

- `WHO` / `W` - list the callsigns currently in the lobby (read from shared state). This proves shared state: user B appears in user A's `WHO`.
- `SAY <text>` / `.<text>` - broadcast `<callsign>: <text>` to **every** connected user (including you, so you see your own line in the transcript). This is the killer demo - a message one station types appears in another station's live session. Text is sanitised: control chars (incl. would-be newlines and terminal escapes) are stripped and the length is bounded (~200 chars), so a hostile line can't injure another user's terminal.
- `HELP` / `?` - the command list.
- `BYE` / `B` / `QUIT` / `Q` - short goodbye, announce departure, close.
- empty line - just reprompts.
- anything else - a friendly "unknown, type ? for help".

On connect, LOBBY greets the new station (`Welcome to the LOBBY, <callsign>. N user(s) here.` plus a one-line command hint and who's already here) and announces `* <callsign> joined` to everyone else. On disconnect (EOF or quit) it announces `* <callsign> left`.

## Concurrency and robustness

- **One thread per accepted connection.** `accept()` runs on the main thread; each connection is served on its own daemon thread.
- **All shared state under one lock.** A single `threading.Lock` guards the registry of `connection → callsign`. Every read (`WHO`, the roster) and every write (join, leave, broadcast) takes it.
- **A vanished user never crashes anyone else.** A broadcast write that fails (the peer disappeared) is caught and the dead entry reaped under the lock - one user leaving can't abort another user's send, the handling thread, or the server.
- **A malformed/hostile line never crashes the thread or the server.** Bad input is caught per-line; the session and the daemon keep running as connections come and go.
- **Clean shutdown on SIGINT/SIGTERM** - the listener is closed and the socket file unlinked.

## Run it as a long-running daemon

LOBBY is a **long-running service that the owner runs**, not something the node spawns. The node only *connects* to it - pdn does **not** manage its lifecycle (start it / keep it up / restart it is the owner's job, e.g. a systemd unit or any supervisor). The socket path is resolved as: **`argv[1]`** if given, otherwise the **`LOBBY_SOCKET`** environment variable, otherwise `/tmp/lobby.sock`. On startup LOBBY unlinks a stale socket file at that path (a leftover from a previous run), binds, and `chmod`s the socket to `0660` so the node - running as the same `packetnet` service user/group - can connect.

```sh
# explicit path (recommended: a dir the packetnet user owns)
python3 lobby.py /run/packetnet/lobby.sock

# or via the env var
LOBBY_SOCKET=/run/packetnet/lobby.sock python3 lobby.py
```

A minimal systemd unit the owner would install (the node does not do this for you):

```ini
[Unit]
Description=LOBBY chat app for packet.net
After=network.target

[Service]
User=packetnet
Group=packetnet
ExecStart=/usr/bin/python3 /usr/share/packetnet/apps/lobby/lobby.py /run/packetnet/lobby.sock
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

### Try it standalone (no node)

Start the daemon, then connect two "users" by hand with `socat` (each blob is a connect header, terminated by a blank line, then session lines):

```sh
python3 lobby.py /tmp/lobby.sock &

# user A — joins, lists, and says hello
printf 'pdn-app: 1\nid: lobby\ncallsign: M0LTE-7\ntransport: ax25\nport: -\nsysop: 0\nargs: \n\nWHO\nSAY hello everyone\n' | socat - UNIX-CONNECT:/tmp/lobby.sock
```

Open a second terminal and connect as `G0ABC-1` the same way while the first is still attached, and you'll see A's `SAY` arrive in B's session live. (`test_lobby.py` automates exactly this two-connection scenario.)

## Tests

[`test_lobby.py`](test_lobby.py) is a stdlib `unittest` suite that starts `lobby.py` as a subprocess on a temp socket path, waits (bounded) for the socket to appear, then opens **two** `AF_UNIX` client connections (two users the node bridged) with different callsigns. It asserts the rung's whole point: user A's `WHO` lists **both** callsigns (shared state visible across connections); a `SAY` from A is received on **user B's** socket attributed to A (broadcast across connections); closing B's connection (EOF) removes B so A sees `* G0ABC-1 left` and a B-less `WHO`; and a malformed/hostile line doesn't crash the server (a third user can still connect afterwards). All timeouts are short and bounded so it can't hang CI. Run either:

```sh
python3 -m unittest examples/lobby/test_lobby.py
# or, from this directory:
python3 test_lobby.py
```

## Register it on a node

Add LOBBY to the node's `applications:` config block with `kind: socket`. The node matches the `match` verb/alias (here `LOBBY`, so a user connects with `C LOBBY`) and, for each launch, opens a new connection to `socketPath` and bridges the session to it. The owner runs the daemon separately (above) so the socket exists before a user launches it.

```yaml
applications:
  - id: lobby
    match: LOBBY
    enabled: true
    kind: socket
    socketPath: /run/packetnet/lobby.sock
    capabilities: [ session ]
```

Contrast with WALL's entry (`kind: process`, with a `command`/`args` the node spawns per connect): there the node *owns* the process lifecycle; here the node only *connects* to a daemon the owner runs. That difference - spawn-per-connect process vs. connect-to-a-long-running-socket - is exactly the floor-vs-rung distinction, and it is what lets LOBBY keep shared in-memory state that WALL cannot.
