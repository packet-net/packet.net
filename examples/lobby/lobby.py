#!/usr/bin/env python3
"""LOBBY — the worked example for the packet.net (pdn) node's "long-running
socket" app rung: a live multi-user chat lobby for connected stations.

Where WALL (``examples/wall/``) is the *floor* — the node spawns one fresh
process per connect, so users are isolated processes that can only share state
through a file — LOBBY is the *next rung up*: one long-running daemon that the
node opens a NEW connection to for each user. Because every user's session is
handled inside the SAME process, LOBBY can hold **shared in-memory state across
all connected users** behind a lock. That is the whole point of this rung:
user B shows up in user A's ``WHO``, and a line user A types with ``SAY`` lands
*live* in user B's session. The spawn-per-connect floor cannot do that — there
is no shared memory between separate processes.

This program knows NOTHING about the node's internals. The ONLY contract is the
``pdn-app/1`` wire, here carried over a **Unix-domain stream socket** instead of
stdio. The node is a CLIENT; we are the SERVER:

  (a) Connect header  — for each user, the node opens a fresh connection and
      writes ``Key: Value`` lines (UTF-8, ``\\n`` terminated), ended by ONE
      blank line. Unknown keys MUST be ignored (forward-compat). Keys we read:
      ``callsign`` (the connecting station, e.g. ``M0LTE-7``); also present:
      ``pdn-app`` (=``1``), ``id``, ``transport``, ``port``, ``sysop``, ``args``.
  (b) Session traffic — after the blank line: each user line arrives as one
      ``\\n``-terminated UTF-8 line; we reply with ``\\n`` line endings ONLY
      (never ``\\r`` — the node maps ``\\n`` to the transport's newline) and
      flush after every write (a socket buffer looks like a hang otherwise). We
      may ALSO write to a connection **unsolicited** — that is how a broadcast
      reaches a user who isn't currently typing.
  (c) Lifecycle      — when the user disconnects, the node closes the
      connection: our ``recv`` returns empty (EOF). EOF means "this user is
      gone": drop them from the shared registry and announce their departure.
      A quit command ends the session the same way.

Concurrency model: ``accept()`` in the main thread; one daemon thread per
accepted connection. ALL access to the shared registry is under a single
``threading.Lock``. A broadcast write that fails (the peer vanished) is caught
and the dead entry reaped — one user leaving never crashes another's send, the
handling thread, or the server.

Stdlib only; runs on a stock Debian python3 (3.9+) — ``socket``, ``threading``,
``os``, ``sys``, ``signal``, ``re``.
"""

import os
import re
import sys
import socket
import signal
import threading

WIRE_VERSION = "1"          # the pdn-app wire version we speak
DEFAULT_SOCKET = "/tmp/lobby.sock"
SOCKET_MODE = 0o660         # owner+group rw: the packetnet service user (node) connects
MAX_SAY_LEN = 200           # bound a single broadcast line's length
LISTEN_BACKLOG = 16         # pending-connection queue depth
PROMPT = "lobby> "

# A user line is a single logical line, but a hostile peer can still smuggle
# control bytes (an embedded ESC sequence, a NUL, a stray CR) into the text.
# Strip every C0/C7F control char — including any would-be newline — so a SAY
# can never inject a line break or a terminal escape into another user's session.
_CTRL = re.compile(r"[\x00-\x1f\x7f]")

# ---------------------------------------------------------------------------
# Shared in-memory state — the heart of this rung.
#
# `_clients` maps a connection's file object -> its callsign. Holding the file
# object lets us write a broadcast straight into any user's session. EVERY read
# or write of `_clients` happens under `_lock`; nothing else may touch it.
# ---------------------------------------------------------------------------
_clients = {}                       # wfile -> callsign
_lock = threading.Lock()


def log(msg):
    """Diagnostics to STDERR — the node log, never a user. Always flushed."""
    sys.stderr.write("lobby: " + msg + "\n")
    sys.stderr.flush()


def sanitise(text):
    """Make arbitrary user text safe to relay as exactly one line.

    Strips control chars (incl. would-be newlines), trims, and bounds length.
    """
    text = _CTRL.sub("", text).strip()
    return text[:MAX_SAY_LEN]


def socket_path():
    """Resolve the listen path: argv[1] if given, else LOBBY_SOCKET, else default."""
    if len(sys.argv) > 1 and sys.argv[1].strip():
        return sys.argv[1]
    return os.environ.get("LOBBY_SOCKET") or DEFAULT_SOCKET


def send(wfile, line=""):
    """Write one line to a single user's session and flush.

    Returns True on success, False if the peer is gone (a broken pipe / closed
    socket). Callers broadcasting to many users use the False return to reap the
    dead connection rather than letting one vanished user abort the whole send.
    """
    try:
        wfile.write(line + "\n")
        wfile.flush()
        return True
    except (BrokenPipeError, OSError, ValueError):
        # ValueError: write on an already-closed file object.
        return False


def read_header(rfile):
    """Read the connect header up to the first blank line; return a dict.

    Lines are ``Key: Value``; keys are lower-cased. Unknown keys are kept but
    callers only read the ones they know (the forward-compat rule). Returns
    whatever was gathered on EOF (the caller treats a header with no callsign as
    an anonymous user).
    """
    header = {}
    while True:
        raw = rfile.readline()
        if raw == "":
            break  # EOF before the blank line — give back what we have
        line = raw.rstrip("\n")
        if line == "":
            break  # blank line ends the header
        if ":" in line:
            key, _, value = line.partition(":")
            header[key.strip().lower()] = value.strip()
        # colon-less lines are ignored defensively
    return header


def roster_locked():
    """Snapshot the current callsigns. MUST be called while holding `_lock`."""
    return sorted(_clients.values())


def broadcast(text, exclude=None):
    """Write `text` to every connected user except `exclude` (a wfile).

    Runs under the lock so the roster can't change mid-iteration, and reaps any
    connection whose write fails (that user vanished mid-send). A single dead
    peer never aborts the broadcast or crashes the sender — its owning thread's
    EOF path will also notice and announce the departure, so the reap here is
    just a safety net that keeps the roster honest.
    """
    with _lock:
        reaped = []
        for wfile in list(_clients):
            if wfile is exclude:
                continue
            if not send(wfile, text):
                reaped.append(_clients.pop(wfile, None))
        for gone in reaped:
            if gone:
                log("reaped dead connection for {} during broadcast".format(gone))


def print_help(wfile):
    send(wfile, "commands (case-insensitive):")
    send(wfile, "  WHO / W            who is in the lobby right now")
    send(wfile, "  SAY <text> / .<text>   say <text> to everyone here")
    send(wfile, "  HELP / ?           this help")
    send(wfile, "  BYE / B / QUIT / Q leave the lobby")


def handle_command(line, callsign, wfile):
    """Handle one user command line. Returns True to keep going, False to quit.

    Reads/writes the shared registry under the lock; broadcasts go out via
    `broadcast`, which is itself lock-guarded.
    """
    stripped = line.strip()
    if not stripped:
        return True  # empty line — just reprompt

    # ".<text>" is a shorthand for SAY <text>.
    if stripped.startswith("."):
        return _do_say(stripped[1:], callsign, wfile)

    word, _, rest = stripped.partition(" ")
    cmd = word.lower()

    if cmd in ("who", "w"):
        with _lock:
            names = roster_locked()
        send(wfile, "in the lobby ({}): {}".format(len(names), ", ".join(names)))
    elif cmd in ("say", "s"):
        return _do_say(rest, callsign, wfile)
    elif cmd in ("help", "?", "h"):
        print_help(wfile)
    elif cmd in ("bye", "b", "quit", "q", "exit"):
        send(wfile, "73 — leaving the lobby. Bye!")
        return False
    else:
        send(wfile, 'unknown command "{}" — type ? for help'.format(word))
    return True


def _do_say(text, callsign, wfile):
    """SAY/. handler: sanitise, then broadcast '<callsign>: <text>' to all."""
    text = sanitise(text)
    if not text:
        send(wfile, "nothing to say — usage: SAY <text>")
        return True
    # Broadcast to everyone INCLUDING the speaker, so the sender sees their own
    # line land in the transcript exactly as others do (no special-casing).
    broadcast("{}: {}".format(callsign, text))
    return True


def serve_client(conn):
    """Handle one accepted connection = one user session bridged by the node.

    Registers the user, greets them, announces the join, runs the command loop,
    and on EOF/quit deregisters and announces the departure. Wrapped so no
    malformed input or peer disappearance can crash the server.
    """
    callsign = None
    wfile = None
    try:
        # Line-buffered text wrappers over the raw socket: readline() gives us
        # one user line; the write side we flush explicitly via send().
        conn.settimeout(None)
        rfile = conn.makefile("r", encoding="utf-8", errors="replace", newline="\n")
        wfile = conn.makefile("w", encoding="utf-8", errors="replace", newline="\n")

        header = read_header(rfile)
        if header.get("pdn-app") not in (None, WIRE_VERSION):
            # We only speak v1; the line rules are stable, so proceed anyway.
            log("unexpected wire version {!r}; proceeding as v1".format(header.get("pdn-app")))
        callsign = header.get("callsign") or "anon"

        # Register under the lock, then greet + announce.
        with _lock:
            _clients[wfile] = callsign
            here = len(_clients)
            others = [c for w, c in _clients.items() if w is not wfile]
        send(wfile, "Welcome to the LOBBY, {}. {} user(s) here.".format(callsign, here))
        send(wfile, "Type ? for help, WHO to see who's here, SAY <text> to chat, BYE to leave.")
        if others:
            send(wfile, "already here: {}".format(", ".join(sorted(others))))
        # Tell everyone else this station arrived (not the joiner themselves).
        broadcast("* {} joined".format(callsign), exclude=wfile)
        log("{} joined ({} now in lobby)".format(callsign, here))

        # Command loop. A blank readline() is EOF — the node closed the
        # connection because the user is gone.
        while True:
            send(wfile, PROMPT.rstrip("\n"))  # prompt on its own line
            try:
                line = rfile.readline()
            except (OSError, ValueError):
                line = ""  # socket died under us — treat as EOF
            if line == "":
                break  # EOF: the user disconnected
            try:
                if not handle_command(line.rstrip("\n"), callsign, wfile):
                    break  # a quit command
            except Exception as e:  # one bad line must not kill the session
                log("error handling line from {}: {}".format(callsign, e))
                send(wfile, "sorry — that command hiccuped; try ? for help")
    except Exception as e:  # last-ditch: a session error never takes down serve()
        log("session error ({}): {}".format(callsign or "?", e))
    finally:
        # Deregister and announce departure — exactly once, whatever ended us.
        left = None
        if wfile is not None:
            with _lock:
                left = _clients.pop(wfile, None)
                remaining = len(_clients)
        if left:
            broadcast("* {} left".format(left))
            log("{} left ({} remain in lobby)".format(left, remaining))
        # Close our file wrappers + socket explicitly so nothing is left for the
        # interpreter to finalize on a chatty daemon thread at shutdown.
        for f in (wfile,):
            try:
                if f is not None:
                    f.close()
            except OSError:
                pass
        try:
            conn.close()
        except OSError:
            pass


def make_server(path):
    """Bind + listen on the Unix-domain socket at `path`.

    Unlinks any stale socket file first (a leftover from a previous run would
    make bind() fail with EADDRINUSE), then chmods it so the node — running as
    the same ``packetnet`` service user/group — can connect.

    Readiness is published atomically: a Unix socket becomes *visible on the
    filesystem at ``bind()``* but only *accepts connections after ``listen()``*.
    A client that connects in that window gets ECONNREFUSED. Anything that treats
    "the socket file exists" as "the daemon is ready" (the node's per-connect
    dial, a systemd readiness probe, the integration test) therefore races the
    bind→listen gap — which widens to whole milliseconds when the box is CPU-
    saturated and this process is preempted between the two calls. So we bind +
    chmod + listen on a *temporary* sibling path and then ``os.rename`` it onto
    the final path: rename is atomic, so the final path appears already-listening
    and the window is gone.
    """
    # Remove a stale socket file, if any. Only unlink an actual socket, never a
    # regular file the user pointed us at by mistake.
    try:
        if os.path.exists(path):
            import stat
            if stat.S_ISSOCK(os.stat(path).st_mode):
                os.unlink(path)
            else:
                raise SystemExit("lobby: refusing to unlink non-socket path: {}".format(path))
    except FileNotFoundError:
        pass

    # Bind + listen on a temp sibling path, then atomically rename it onto the
    # final path so `path` only ever appears once the listener is accepting. The
    # pid suffix keeps the staging name unique; clear any same-pid leftover from a
    # crash mid-rename so the bind below can't fail with EADDRINUSE.
    staging = "{}.staging.{}".format(path, os.getpid())
    try:
        os.unlink(staging)
    except OSError:
        pass

    srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    srv.bind(staging)
    try:
        os.chmod(staging, SOCKET_MODE)
    except OSError as e:
        log("could not chmod socket {}: {}".format(staging, e))
    srv.listen(LISTEN_BACKLOG)
    # Atomic publish: the final path springs into existence already-listening.
    os.rename(staging, path)
    return srv


def main():
    path = socket_path()
    srv = make_server(path)
    log("listening on {} (pid {})".format(path, os.getpid()))

    # Clean shutdown on SIGINT/SIGTERM: close the listener and unlink the socket
    # file. We exit via os._exit rather than sys.exit: the meaningful cleanup is
    # already done, and os._exit skips interpreter finalizers, so a per-session
    # daemon thread caught mid-write can't emit a spurious finalizer traceback.
    def shutdown(_signum, _frame):
        log("shutting down")
        try:
            srv.close()
        except OSError:
            pass
        try:
            os.unlink(path)
        except OSError:
            pass
        os._exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    # Accept loop: one daemon thread per connection. Daemon threads so a signal
    # exit doesn't wait on chatty sessions.
    while True:
        try:
            conn, _ = srv.accept()
        except OSError:
            break  # listener closed by the signal handler — exit the loop
        t = threading.Thread(target=serve_client, args=(conn,), daemon=True)
        t.start()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
