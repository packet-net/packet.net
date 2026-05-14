#!/usr/bin/env python3
"""
NinoTNC mode 12 (300 AFSK AX.25) — catastrophic-RX-lockup repro.

Setup:
    * Two NinoTNC N9600A boards (firmware 3.44 observed).
    * Audio TX↔RX cross-wired between them (line-level cable, no RF).
    * Both USB-CDC ACM to one Linux/Windows host.
    * MODE DIPs on both: 1111 ("Set from KISS").
    * TXDELAY pots on both: minimum (so KISS TXDELAY is honoured).

Observation:
    With mode 12 (300 AFSK AX.25) selected via KISS SETHW, certain
    TXDELAY values catastrophically wedge the *receive* path: every
    subsequent frame is dropped (CRC presumably failing in the demod
    or the AX.25 framer never re-syncing). The failing TXDELAY value
    is not consistent across runs:

        Overnight run 1: TXDELAY=100 (1000 ms)
            * A->B 99/100 (frame 0 only lost, otherwise clean)
            * B->A   4/100 (frames 0, 5..99 all lost; contiguous run)

        Overnight run 2 (~2 h later, same hardware, same script):
            * TXDELAY=50  (500 ms)  -> A->B   0/100, B->A   0/100
            * TXDELAY=100 (1000 ms) -> A->B  99/100, B->A 100/100
            * TXDELAY=20  (200 ms)  -> A->B  99/100, B->A 100/100

    Modes 13 (300 AFSKPLL IL2P) and 14 (300 AFSKPLL IL2P+CRC) — same
    300-baud air time, but PLL-based AFSK demodulator — *do not*
    exhibit this behaviour at TXDELAY=500 or 1000 ms over N=50 trials
    in either direction. Points at mode 12's plain-AFSK demodulator
    rather than 300-baud air time or AX.25 framing.

    Mode 12 is deprecated for new deployments; this repro is filed
    for awareness rather than fix-blocker.

Usage:
    python3 ninotnc_mode12_repro.py <portA> <portB> [--n 100] [--txdelay 50]

    e.g. python3 ninotnc_mode12_repro.py /dev/ttyACM0 /dev/ttyACM1
         python3 ninotnc_mode12_repro.py COM6 COM8 --txdelay 100

The script tries TXDELAY values {20, 50, 100} sequentially with N
frames in each direction and prints the success / failure pattern
for each. Expectation if the bug repros: at least one (TXDELAY,
direction) cell drops every frame after the first few.

Dependencies: pyserial (`pip install pyserial`).
"""

import argparse
import sys
import time
import threading
import queue
from typing import List, Optional

try:
    import serial  # type: ignore
except ImportError:  # pragma: no cover
    print("This script needs `pyserial`. Install with: pip install pyserial",
          file=sys.stderr)
    sys.exit(2)

# ---------------------------- KISS framing ---------------------------- #

FEND = 0xC0
FESC = 0xDB
TFEND = 0xDC
TFESC = 0xDD

CMD_DATA = 0x00
CMD_TXDELAY = 0x01
CMD_SETHW = 0x06


def kiss_encode(port: int, command: int, payload: bytes) -> bytes:
    """Wrap a payload in a single KISS frame, including SLIP escaping."""
    cmd_byte = ((port & 0x0F) << 4) | (command & 0x0F)
    out = bytearray([FEND])
    for b in [cmd_byte, *payload]:
        if b == FEND:
            out += bytes([FESC, TFEND])
        elif b == FESC:
            out += bytes([FESC, TFESC])
        else:
            out.append(b)
    out.append(FEND)
    return bytes(out)


class KissDecoder:
    """Stateful unescape + frame-end split. Returns one decoded frame
    (the bytes between two FENDs, post-unescape) at a time."""

    def __init__(self) -> None:
        self._buf = bytearray()
        self._esc = False

    def push(self, chunk: bytes) -> List[bytes]:
        frames: List[bytes] = []
        for b in chunk:
            if self._esc:
                self._esc = False
                if b == TFEND:
                    self._buf.append(FEND)
                elif b == TFESC:
                    self._buf.append(FESC)
                # else: malformed; drop silently.
                continue
            if b == FEND:
                if self._buf:
                    frames.append(bytes(self._buf))
                    self._buf.clear()
                continue
            if b == FESC:
                self._esc = True
                continue
            self._buf.append(b)
        return frames


# ---------------------------- AX.25 helpers ---------------------------- #

def ax25_encode_callsign(call: str, ssid: int, last: bool) -> bytes:
    call = call.upper().ljust(6)[:6]
    out = bytearray(((ord(c) & 0x7F) << 1) for c in call)
    ssid_byte = 0x60 | ((ssid & 0x0F) << 1) | (0x01 if last else 0x00)
    out.append(ssid_byte)
    return bytes(out)


def ax25_ui_frame(src: str, src_ssid: int, dst: str, dst_ssid: int, info: bytes) -> bytes:
    """Build a UI frame in the form KISS expects (no FCS, no flags)."""
    body = (
        ax25_encode_callsign(dst, dst_ssid, False) +
        ax25_encode_callsign(src, src_ssid, True) +
        bytes([0x03, 0xF0]) +  # UI control, PID = no L3
        info
    )
    return body


def ax25_extract_info(frame: bytes) -> Optional[bytes]:
    """Recover the INFO field of a UI frame; None if it doesn't look like one."""
    if len(frame) < 16:
        return None
    if frame[0] != CMD_DATA:  # KISS command byte expected to be 0
        return None
    body = frame[1:]
    # Skip dest (7) + src (7) + control (1) + PID (1) = 16 bytes header.
    return body[16:]


# ---------------------------- TNC wrangling ---------------------------- #

class Tnc:
    """One NinoTNC. Background thread does serial reads → frames into queue."""

    def __init__(self, path: str, baud: int = 57600) -> None:
        self.path = path
        self.port = serial.Serial(path, baud, timeout=0.1)
        self.port.dtr = True
        self.port.rts = True
        self.frames: "queue.Queue[bytes]" = queue.Queue()
        self._decoder = KissDecoder()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._reader, daemon=True)
        self._thread.start()

    def _reader(self) -> None:
        while not self._stop.is_set():
            try:
                buf = self.port.read(4096)
            except (serial.SerialException, OSError):
                return
            if buf:
                for f in self._decoder.push(buf):
                    self.frames.put(f)

    def write(self, raw: bytes) -> None:
        self.port.write(raw)
        self.port.flush()

    def set_mode(self, mode: int, persist: bool = False) -> None:
        """KISS SETHW. Adds +16 when persist is False (don't burn flash)."""
        payload = bytes([mode + (0 if persist else 16)])
        self.write(kiss_encode(0, CMD_SETHW, payload))

    def set_txdelay(self, units_10ms: int) -> None:
        self.write(kiss_encode(0, CMD_TXDELAY, bytes([units_10ms])))

    def send_data(self, ax25_frame: bytes) -> None:
        self.write(kiss_encode(0, CMD_DATA, ax25_frame))

    def drain_inbox(self) -> None:
        try:
            while True:
                self.frames.get_nowait()
        except queue.Empty:
            return

    def wait_for_info(self, expected_info: bytes, timeout: float) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                frame = self.frames.get(timeout=max(0.01, deadline - time.time()))
            except queue.Empty:
                return False
            info = ax25_extract_info(frame)
            if info is not None and info == expected_info:
                return True
        return False

    def close(self) -> None:
        self._stop.set()
        try:
            self.port.close()
        except Exception:
            pass


# ---------------------------- the actual probe ---------------------------- #

def run_direction(tx: Tnc, rx: Tnc, n: int, label: str, payload_bytes: int) -> tuple[int, List[int]]:
    ok = 0
    failures: List[int] = []
    for i in range(n):
        info_prefix = f"{label}-{i:03d}-".encode("ascii")
        info = info_prefix + bytes((ord('A') + (j % 26)) for j in range(max(0, payload_bytes - len(info_prefix))))
        info = info[:payload_bytes]
        frame = ax25_ui_frame("AA", 1, "BB", 2, info)

        rx.drain_inbox()
        tx.send_data(frame)

        if rx.wait_for_info(info, timeout=15.0):
            ok += 1
        else:
            failures.append(i)
        if (i + 1) % 20 == 0:
            print(f"    {label}: {i+1}/{n} ok={ok} fails={len(failures)}")
    return ok, failures


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("port_a")
    ap.add_argument("port_b")
    ap.add_argument("--n", type=int, default=100, help="frames per direction per TXDELAY (default 100)")
    ap.add_argument("--payload", type=int, default=50, help="AX.25 INFO bytes (default 50)")
    ap.add_argument("--txdelays", type=str, default="20,50,100",
                    help="comma-separated TXDELAY values in 10-ms units (default 20,50,100)")
    ap.add_argument("--mode", type=int, default=12, help="NinoTNC mode (default 12)")
    args = ap.parse_args()

    txdelays = [int(x) for x in args.txdelays.split(",") if x.strip()]

    print(f"# NinoTNC mode-{args.mode} catastrophic-RX-lockup repro")
    print(f"# ports: A={args.port_a}, B={args.port_b}")
    print(f"# N per direction per TXDELAY: {args.n}, payload {args.payload} B")
    print()

    for txd in txdelays:
        print(f"=== TXDELAY={txd} ({txd * 10} ms) ===")
        a = Tnc(args.port_a)
        b = Tnc(args.port_b)
        try:
            a.set_mode(args.mode)
            b.set_mode(args.mode)
            time.sleep(0.7)
            a.set_txdelay(txd)
            b.set_txdelay(txd)
            time.sleep(0.2)
            a.drain_inbox()
            b.drain_inbox()

            ab_ok, ab_fail = run_direction(a, b, args.n, "A2B", args.payload)
            ba_ok, ba_fail = run_direction(b, a, args.n, "B2A", args.payload)
        finally:
            a.close()
            b.close()

        # ASCII arrows because Windows consoles using cp1252 trip on
        # the Unicode arrow characters that look nicer everywhere else.
        print(f"  A->B: {ab_ok}/{args.n} (failures: {summarize(ab_fail)})")
        print(f"  B->A: {ba_ok}/{args.n} (failures: {summarize(ba_fail)})")
        print()

    return 0


def summarize(failures: List[int]) -> str:
    if not failures:
        return "none"
    if len(failures) == 1 and failures[0] == 0:
        return "first only"
    if len(failures) == 1:
        return f"index {failures[0]}"
    # Try to recognise "frame 0 dropped, then contiguous run from some N".
    first = failures[0] == 0
    run_start = failures[1] if first else failures[0]
    expected = run_start
    is_run = True
    for f in failures[1 if first else 0:]:
        if f != expected:
            is_run = False
            break
        expected += 1
    if is_run:
        prefix = "0 + " if first else ""
        return f"{prefix}contiguous {run_start}..{failures[-1]} ({len(failures) - (1 if first else 0)} frames)"
    return f"scattered ({len(failures)}): {failures}"


if __name__ == "__main__":
    sys.exit(main())
