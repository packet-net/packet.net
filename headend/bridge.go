package main

import (
	"errors"
	"fmt"
	"log"
	"net"
	"sync"
)

// Bridge is one device's raw TCP↔serial pipe. The serial handle is opened once
// and held for the daemon's life (so the line-control verb works with or without
// a client, and PDN's baud sweep can re-clock a connected pipe); the TCP side
// serves one client at a time, re-accepting on disconnect. Bytes are pumped
// transparently — no framing, no parsing (CCDI and KISS are both just bytes).
type Bridge struct {
	dev     DiscoveredPort
	tcpPort int
	ln      net.Listener
	port    SerialPort

	mu   sync.Mutex // guards line (the cached params reported in inventory)
	line LineParams
}

// newBridge opens devPath at the given line params, binds a TCP listener on
// tcpPort, and returns the ready bridge. The caller starts serving with run.
func newBridge(dev DiscoveredPort, tcpPort int, line LineParams, open SerialOpener) (*Bridge, error) {
	port, err := open(dev.DevPath, line)
	if err != nil {
		return nil, fmt.Errorf("open serial %s: %w", dev.DevPath, err)
	}
	ln, err := net.Listen("tcp", fmt.Sprintf(":%d", tcpPort))
	if err != nil {
		_ = port.Close()
		return nil, fmt.Errorf("listen :%d: %w", tcpPort, err)
	}
	return &Bridge{dev: dev, tcpPort: tcpPort, ln: ln, port: port, line: line.normalized()}, nil
}

// run accepts clients one at a time until the listener is closed (shutdown),
// serving each until it disconnects. A second client waits in the accept backlog
// until the first goes away.
func (b *Bridge) run() {
	for {
		conn, err := b.ln.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return // listener closed → shutdown.
			}
			log.Printf("bridge %s (:%d): accept: %v", b.dev.ID, b.tcpPort, err)
			return
		}
		log.Printf("bridge %s (:%d): client %s connected", b.dev.ID, b.tcpPort, conn.RemoteAddr())
		b.serve(conn)
		log.Printf("bridge %s (:%d): client disconnected", b.dev.ID, b.tcpPort)
	}
}

// serve runs the bidirectional pump for one client: client→serial in a
// goroutine, serial→client in the caller. It returns once either side ends,
// having closed conn so both pumps unwind.
func (b *Bridge) serve(conn net.Conn) {
	defer conn.Close()
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
		TCPPort:  b.tcpPort,
		Baud:     l.Baud,
		DataBits: l.DataBits,
		Parity:   l.Parity,
		StopBits: l.StopBits,
	}
}

// close tears the bridge down: stop accepting, drop the serial handle.
func (b *Bridge) close() {
	_ = b.ln.Close()
	_ = b.port.Close()
}
