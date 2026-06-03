#!/usr/bin/env python3
"""Minimal AGW accept/echo helper for the Dire Wolf interop peer.

This is the glue that makes Dire Wolf *answer* an inbound connect. Dire Wolf's
connected-mode engine (ax25_link.c) only services an incoming SABME/SABM if a
client app has registered the destination callsign on that radio channel
(dl_register_callsign). This helper is that client app:

  * 'X' (register callsign)  — register our called callsign so inbound
                               connects to it are routed to us. Dire Wolf
                               then answers the SABME with UA on its own
                               (set_version_2_2 → modulo 128 for an extended
                               connect; no modulo knob is needed here).
  * 'C' (connection received)— logged; the connect is already accepted by the
                               time we see this (Dire Wolf sent the UA).
  * 'D' (connected data)     — echoed straight back so a payload round-trips
                               over the connected-mode link.
  * 'd' (disconnected)       — logged.

WHY NOT Packet.Agw's AgwClient?  AgwClient is connect-*initiator*-only:
OpenSessionAsync sends 'C' and waits for the ack; there is no path to
passively accept an inbound 'C' or to run an echo responder, and pulling a
.NET runtime into this image just for that would be heavyweight. The AGW wire
format is a trivial fixed 36-byte header, so a tiny purpose-built helper is
the lighter, self-contained choice. (Packet.Agw.AgwFrame/AgwFrameStream are
exercised on the *client* side by the C# interop tests that drive this peer.)

The AGW frame header is 36 bytes, little-endian data length, callsigns as
10-byte NUL-padded ASCII — see Packet.Agw.AgwFrame for the canonical layout.
"""

import os
import socket
import struct
import sys
import time

AGW_HOST = os.environ.get("AGW_HOST", "127.0.0.1")
AGW_PORT = int(os.environ.get("AGW_PORT", "8000"))
# Callsign(s) to register — comma-separated. These are the callsigns our
# Ax25Session will connect TO. Registering several lets one helper back
# multiple interop test classes using distinct called callsigns.
REGISTER_CALLS = [
    c.strip()
    for c in os.environ.get("AGW_REGISTER", "N0RESP").split(",")
    if c.strip()
]
RADIO_PORT = int(os.environ.get("AGW_RADIO_PORT", "0"))

HEADER_SIZE = 36


def log(msg):
    print(f"[agw-echo] {msg}", flush=True)


def build(kind, port=0, pid=0, frm="", to="", data=b""):
    """Serialise one AGW frame (36-byte header + body)."""
    h = bytearray(HEADER_SIZE)
    h[0] = port & 0xFF
    h[4] = ord(kind)
    h[6] = pid & 0xFF
    fb = frm.encode("ascii")[:10]
    tb = to.encode("ascii")[:10]
    h[8 : 8 + len(fb)] = fb
    h[18 : 18 + len(tb)] = tb
    struct.pack_into("<I", h, 28, len(data))
    return bytes(h) + data


def parse_call(field):
    end = field.find(b"\0")
    if end < 0:
        end = len(field)
    return field[:end].decode("ascii", errors="replace").strip()


def run_once():
    s = socket.create_connection((AGW_HOST, AGW_PORT), timeout=10)
    s.settimeout(1.0)
    log(f"connected to AGW {AGW_HOST}:{AGW_PORT}")

    for call in REGISTER_CALLS:
        s.sendall(build("X", port=RADIO_PORT, frm=call))
        log(f"registered callsign {call!r} on radio port {RADIO_PORT}")

    buf = b""
    while True:
        try:
            chunk = s.recv(8192)
        except socket.timeout:
            continue
        if not chunk:
            raise ConnectionError("AGW server closed the connection")
        buf += chunk

        while len(buf) >= HEADER_SIZE:
            data_len = struct.unpack_from("<I", buf, 28)[0]
            if len(buf) < HEADER_SIZE + data_len:
                break
            port = buf[0]
            kind = chr(buf[4])
            pid = buf[6]
            frm = parse_call(buf[8:18])
            to = parse_call(buf[18:28])
            body = buf[HEADER_SIZE : HEADER_SIZE + data_len]
            buf = buf[HEADER_SIZE + data_len :]

            if kind == "X":
                ok = body[:1] == b"\x01"
                log(f"registration ack for {frm!r}: {'ok' if ok else 'FAILED'}")
            elif kind == "C":
                log(f"connection received: {frm!r} -> {to!r} (port {port})")
            elif kind == "D":
                # Echo connected data straight back to the originator. The
                # server reports From=remote, To=us; we swap them on the way
                # out. PID 0xF0 (no layer 3) matches text data.
                log(f"echoing {len(body)} bytes for {to!r} -> {frm!r}")
                s.sendall(
                    build("D", port=port, pid=0xF0, frm=to, to=frm, data=body)
                )
            elif kind == "d":
                log(f"disconnected: {frm!r} -> {to!r}")
            # Other server-initiated kinds (monitor frames etc.) are ignored.


def main():
    log(
        f"starting; register={REGISTER_CALLS} port={RADIO_PORT} "
        f"target={AGW_HOST}:{AGW_PORT}"
    )
    while True:
        try:
            run_once()
        except (ConnectionError, OSError) as ex:
            log(f"connection lost ({ex}); retrying in 2s")
            time.sleep(2)
        except KeyboardInterrupt:
            log("interrupted; exiting")
            return 0


if __name__ == "__main__":
    sys.exit(main())
