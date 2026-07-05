package main

import (
	"fmt"
	"strings"
	"sync"
	"time"

	"go.bug.st/serial"
)

// LineParams is the serial line configuration reported and set over the HTTP
// control plane. The data plane stays a pure binary TCP pipe (deliberately NOT
// RFC2217 — its 0xFF escaping collides with binary CCDI/KISS); line params ride
// out-of-band here. Parity is "none" | "even" | "odd"; stopBits is 1 | 2.
type LineParams struct {
	Baud     int    `json:"baud"`
	DataBits int    `json:"dataBits"`
	Parity   string `json:"parity"`
	StopBits int    `json:"stopBits"`
}

// defaultLine is 8N1 at the configured baud — the universal serial default.
func defaultLine(baud int) LineParams {
	return LineParams{Baud: baud, DataBits: 8, Parity: "none", StopBits: 1}
}

// normalized fills in zero fields with 8N1 defaults and lowercases parity, so a
// partial {baud} request produces a complete, valid set.
func (l LineParams) normalized() LineParams {
	if l.DataBits == 0 {
		l.DataBits = 8
	}
	if l.StopBits == 0 {
		l.StopBits = 1
	}
	if l.Parity == "" {
		l.Parity = "none"
	}
	l.Parity = strings.ToLower(l.Parity)
	return l
}

// validate rejects line params the serial layer can't honour, independent of
// the backend (so the API rejects them uniformly, real hardware or not).
func (l LineParams) validate() error {
	l = l.normalized()
	if l.Baud <= 0 {
		return fmt.Errorf("baud %d must be positive", l.Baud)
	}
	if l.DataBits < 5 || l.DataBits > 8 {
		return fmt.Errorf("dataBits %d out of range 5..8", l.DataBits)
	}
	switch l.Parity {
	case "none", "even", "odd":
	default:
		return fmt.Errorf("unsupported parity %q (want none|even|odd)", l.Parity)
	}
	switch l.StopBits {
	case 1, 2:
	default:
		return fmt.Errorf("unsupported stopBits %d (want 1 or 2)", l.StopBits)
	}
	return nil
}

// serialReadTimeout bounds each blocking serial read so the bridge's read pump
// wakes periodically to notice a departed client / shutdown, instead of blocking
// forever on a quiet channel. On timeout a SerialPort.Read returns (0, nil).
const serialReadTimeout = 200 * time.Millisecond

// SerialPort is the narrow byte seam the bridge pumps over. Read has
// go.bug.st/serial timeout semantics: it blocks up to serialReadTimeout and
// returns (0, nil) on timeout — never a 0-length hot-spin, never a forever
// block — so the pump can re-check its done signal between reads. Fakeable so
// tests need no real hardware.
type SerialPort interface {
	Read(p []byte) (int, error)
	Write(p []byte) (int, error)
	// SetLine reconfigures the live line params (PDN's baud sweep + rare
	// re-clock) without dropping the open handle.
	SetLine(l LineParams) error
	Close() error
}

// SerialOpener opens devPath at the given line params. Injected so the bridge is
// testable with a fake serial layer; production uses openRealSerial.
type SerialOpener func(devPath string, l LineParams) (SerialPort, error)

// realSerial wraps a go.bug.st/serial port. Read/Write pass straight through
// (go.bug.st is safe for concurrent read+write); only SetLine is mutex-guarded,
// so a reconfigure never deadlocks against a blocked read.
type realSerial struct {
	port serial.Port
	mu   sync.Mutex
}

func openRealSerial(devPath string, l LineParams) (SerialPort, error) {
	mode, err := toMode(l)
	if err != nil {
		return nil, err
	}
	p, err := serial.Open(devPath, mode)
	if err != nil {
		return nil, err
	}
	if err := p.SetReadTimeout(serialReadTimeout); err != nil {
		_ = p.Close()
		return nil, err
	}
	return &realSerial{port: p}, nil
}

func (r *realSerial) Read(p []byte) (int, error)  { return r.port.Read(p) }
func (r *realSerial) Write(p []byte) (int, error) { return r.port.Write(p) }

func (r *realSerial) SetLine(l LineParams) error {
	mode, err := toMode(l)
	if err != nil {
		return err
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.port.SetMode(mode)
}

func (r *realSerial) Close() error { return r.port.Close() }

// toMode translates our transport-neutral LineParams into a go.bug.st mode.
func toMode(l LineParams) (*serial.Mode, error) {
	l = l.normalized()
	m := &serial.Mode{BaudRate: l.Baud, DataBits: l.DataBits}
	switch l.Parity {
	case "none":
		m.Parity = serial.NoParity
	case "even":
		m.Parity = serial.EvenParity
	case "odd":
		m.Parity = serial.OddParity
	default:
		return nil, fmt.Errorf("unsupported parity %q (want none|even|odd)", l.Parity)
	}
	switch l.StopBits {
	case 1:
		m.StopBits = serial.OneStopBit
	case 2:
		m.StopBits = serial.TwoStopBits
	default:
		return nil, fmt.Errorf("unsupported stopBits %d (want 1 or 2)", l.StopBits)
	}
	return m, nil
}
