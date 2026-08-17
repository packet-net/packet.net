#!/usr/bin/env python3
"""A throwaway TCP sink for the live smoke: the far end of the node's kiss-tcp ports.

The smoke boots a real pdn node with real KISS-over-TCP ports. Those ports dial out,
so something has to be listening or the port never comes up and the Ports screen shows
a permanently-faulted card. This is that something: it accepts every connection and
drains whatever arrives, forever, and NEVER replies.

Never replying is deliberate. It gives the smoke a port that is genuinely up (the
socket is connected, the supervisor is happy) while every AX.25 dial over it fails by
timeout - which is exactly the "connect to a station that isn't there" path the
Sessions step asserts on. A sink that spoke AX.25 back would need a whole peer stack
and would prove nothing extra.

Usage: tcp-sink.py PORT [PORT ...]   (binds 127.0.0.1 on each; the smoke kills it by PID)
"""

import socket
import sys
import threading


def drain(conn):
    """Read and discard until the peer goes away. No writes, ever."""
    with conn:
        try:
            while conn.recv(65536):
                pass
        except OSError:
            pass


def serve(port):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", port))
    srv.listen(16)
    print("sink listening on 127.0.0.1:%d" % port, flush=True)
    while True:
        try:
            conn, _ = srv.accept()
        except OSError:
            return
        threading.Thread(target=drain, args=(conn,), daemon=True).start()


def main(argv):
    ports = [int(a) for a in argv[1:]]
    if not ports:
        print("usage: tcp-sink.py PORT [PORT ...]", file=sys.stderr)
        return 2
    threads = [threading.Thread(target=serve, args=(p,), daemon=True) for p in ports]
    for t in threads:
        t.start()
    # Park the main thread. The smoke kills this process by PID when it is done.
    for t in threads:
        t.join()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except KeyboardInterrupt:
        sys.exit(0)
