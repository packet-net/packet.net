package main

import (
	"errors"
	"fmt"
	"log"
	"net"
	"sync"
)

// netListen is the seam bridge client listeners bind through — net.Listen in
// production. Tests override it to bind hermetic, deterministic loopback
// listeners (so no test ever contends for a real fixed TCP port); the bridge's
// LOGICAL port stays Bridge.tcpPort regardless of the OS-assigned socket.
var netListen = net.Listen

// Bridge is one device's raw TCP↔serial pipe. The serial handle is opened once
// and held for the bridge's life (so the line-control verb works with or without
// a client, and PDN's baud sweep can re-clock a connected pipe); the TCP side
// serves one client at a time, re-accepting on disconnect. Bytes are pumped
// transparently — no framing, no parsing (CCDI and KISS are both just bytes).
//
// A bridge is torn down by Close (SIGTERM shutdown, or a hot-unplug removing the
// device): it stops the accept loop, drops any connected client, and releases the
// listener + serial handle. The lifecycle is identical for the startup-enumerated
// bridges and the ones the hot-plug rescan adds later.
type Bridge struct {
	dev     DiscoveredPort
	tcpPort int
	ln      net.Listener
	port    SerialPort

	done      chan struct{} // closed by Close → the accept loop unwinds
	closeOnce sync.Once

	mu         sync.Mutex // guards line (cached inventory params) + activeConn
	line       LineParams
	activeConn net.Conn // the currently-served client, so Close can disconnect it
}

// newBridge opens devPath at the given line params, binds a TCP listener on
// tcpPort (restricted to bindAddr when non-empty; empty = all interfaces), and
// returns the ready bridge. The caller starts serving with run and tears it down
// with Close.
func newBridge(dev DiscoveredPort, tcpPort int, bindAddr string, line LineParams, open SerialOpener) (*Bridge, error) {
	port, err := open(dev.DevPath, line)
	if err != nil {
		return nil, fmt.Errorf("open serial %s: %w", dev.DevPath, err)
	}
	addr := listenAddr(bindAddr, tcpPort)
	ln, err := netListen("tcp", addr)
	if err != nil {
		_ = port.Close()
		return nil, fmt.Errorf("listen %s: %w", addr, err)
	}
	return &Bridge{
		dev:     dev,
		tcpPort: tcpPort,
		ln:      ln,
		port:    port,
		done:    make(chan struct{}),
		line:    line.normalized(),
	}, nil
}

// run accepts clients one at a time until the bridge is Closed (shutdown or
// hot-unplug), serving each until it disconnects. A second client waits in the
// accept backlog until the first goes away.
func (b *Bridge) run() {
	for {
		conn, err := b.ln.Accept()
		if err != nil {
			// A closed listener (Close) surfaces as ErrClosed → a clean stop.
			if errors.Is(err, net.ErrClosed) {
				return
			}
			// Any other accept error after Close is still just shutdown.
			select {
			case <-b.done:
				return
			default:
			}
			log.Printf("bridge %s (:%d): accept: %v", b.dev.ID, b.tcpPort, err)
			return
		}
		log.Printf("bridge %s (:%d): client %s connected", b.dev.ID, b.tcpPort, conn.RemoteAddr())
		b.serve(conn)
		log.Printf("bridge %s (:%d): client disconnected", b.dev.ID, b.tcpPort)
		// Stop re-accepting once Closed (Close may have raced in after a client
		// disconnected on its own).
		select {
		case <-b.done:
			return
		default:
		}
	}
}

// serve runs the bidirectional pump for one client: client→serial in a
// goroutine, serial→client in the caller. It returns once either side ends —
// the client disconnecting, a serial fault, or Close disconnecting the client —
// having closed conn so both pumps unwind. The conn is registered so Close can
// disconnect an in-flight client on hot-unplug/shutdown.
func (b *Bridge) serve(conn net.Conn) {
	defer conn.Close()

	// Discard whatever the device emitted while NO client was attached (#586):
	// without this, stale buffered bytes burst into the fresh client and a new
	// CCDI/KISS session starts mid-garbage (both protocols resync, but it can
	// cost a first-transaction timeout). DrainInput flushes only what is
	// buffered at THIS instant — no pump is running yet (the previous client's
	// pumps fully unwind before re-accept), and bytes arriving after the flush
	// are picked up by the pump below, so nothing post-connect is lost.
	if err := b.port.DrainInput(); err != nil {
		log.Printf("bridge %s (:%d): drain stale serial input: %v", b.dev.ID, b.tcpPort, err)
	}

	b.mu.Lock()
	b.activeConn = conn
	b.mu.Unlock()
	defer func() {
		b.mu.Lock()
		b.activeConn = nil
		b.mu.Unlock()
	}()

	done := make(chan struct{})
	go func() {
		defer close(done)
		pumpConnToSerial(conn, b.port)
	}()
	pumpSerialToConn(b.port, conn, done)
	_ = conn.Close() // unblock pumpConnToSerial if we exited first (serial fault).
	<-done
}

// pumpConnToSerial copies client bytes to the serial device until the client
// closes or errors.
func pumpConnToSerial(conn net.Conn, port SerialPort) {
	buf := make([]byte, 4096)
	for {
		n, err := conn.Read(buf)
		if n > 0 {
			if _, werr := port.Write(buf[:n]); werr != nil {
				return
			}
		}
		if err != nil {
			return
		}
	}
}

// pumpSerialToConn copies serial bytes to the client until done is signalled
// (client gone) or a real serial error occurs. A read timeout surfaces as
// (0, nil) — the loop simply re-checks done and reads again.
func pumpSerialToConn(port SerialPort, conn net.Conn, done <-chan struct{}) {
	buf := make([]byte, 4096)
	for {
		select {
		case <-done:
			return
		default:
		}
		n, err := port.Read(buf)
		if n > 0 {
			if _, werr := conn.Write(buf[:n]); werr != nil {
				return
			}
		}
		if err != nil {
			return // real serial fault (timeouts are normalized to (0, nil)).
		}
	}
}

// SetLine reconfigures the serial line params and, on success, updates the
// cached params reported in inventory. Returns the effective (normalized) set.
func (b *Bridge) SetLine(l LineParams) (LineParams, error) {
	l = l.normalized()
	if err := l.validate(); err != nil {
		return LineParams{}, err
	}
	if err := b.port.SetLine(l); err != nil {
		return LineParams{}, err
	}
	b.mu.Lock()
	b.line = l
	b.mu.Unlock()
	return l, nil
}

// clientConnected reports whether a client is currently attached to the pipe
// (the /statusz observability surface, #583).
func (b *Bridge) clientConnected() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.activeConn != nil
}

// currentLine returns the cached line params (thread-safe read).
func (b *Bridge) currentLine() LineParams {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.line
}

// info snapshots the bridge as an inventory row.
func (b *Bridge) info() PortInfo {
	l := b.currentLine()
	return PortInfo{
		ID:       b.dev.ID,
		DevPath:  b.dev.DevPath,
		USBVid:   b.dev.USBVid,
		USBPid:   b.dev.USBPid,
		ByID:     b.dev.ByID,
		ByPath:   b.dev.ByPath,
		IDSource: b.dev.IDSource,
		IDStable: b.dev.IDStable,
		TCPPort:  b.tcpPort,
		Baud:     l.Baud,
		DataBits: l.DataBits,
		Parity:   l.Parity,
		StopBits: l.StopBits,
	}
}

// Close tears the bridge down and is idempotent: it stops the accept loop
// (closing the listener → Accept returns ErrClosed), disconnects any connected
// client, and drops the serial handle. Used for both SIGTERM shutdown and a
// hot-unplug removing the device. Closing the serial makes the serve pumps
// unwind even if the client is quiet.
func (b *Bridge) Close() {
	b.closeOnce.Do(func() {
		close(b.done)
		_ = b.ln.Close()
		b.mu.Lock()
		conn := b.activeConn
		b.mu.Unlock()
		if conn != nil {
			_ = conn.Close()
		}
		_ = b.port.Close()
	})
}
