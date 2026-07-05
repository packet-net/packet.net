package main

import (
	"io"
	"net"
	"sync"
	"testing"
	"time"
)

// fakeReadTimeout mirrors serialReadTimeout for the fake, but shorter so tests
// re-check their done signal snappily.
const fakeReadTimeout = 30 * time.Millisecond

// fakeSerial is an in-memory SerialPort. inbound carries bytes the "device"
// emits (serial→client); outbound captures bytes written to the device
// (client→serial) for the test to observe. Read honours the go.bug.st timeout
// contract: (0, nil) on a quiet window, never a forever block.
type fakeSerial struct {
	inbound  chan byte
	outbound chan byte
	closed   chan struct{}
	once     sync.Once

	mu       sync.Mutex
	line     LineParams
	setCalls int
}

func newFakeSerial(l LineParams) *fakeSerial {
	return &fakeSerial{
		inbound:  make(chan byte, 8192),
		outbound: make(chan byte, 8192),
		closed:   make(chan struct{}),
		line:     l.normalized(),
	}
}

// inject queues bytes for the device to emit toward the client.
func (f *fakeSerial) inject(b []byte) {
	for _, x := range b {
		select {
		case f.inbound <- x:
		case <-f.closed:
			return
		}
	}
}

// deviceRead blocks until it has read n bytes the bridge wrote to the device, or
// the deadline passes.
func (f *fakeSerial) deviceRead(t *testing.T, n int, deadline time.Duration) []byte {
	t.Helper()
	out := make([]byte, 0, n)
	timer := time.After(deadline)
	for len(out) < n {
		select {
		case b := <-f.outbound:
			out = append(out, b)
		case <-timer:
			t.Fatalf("deviceRead: got %q (%d bytes), want %d before deadline", out, len(out), n)
		}
	}
	return out
}

func (f *fakeSerial) Read(p []byte) (int, error) {
	select {
	case b, ok := <-f.inbound:
		if !ok {
			return 0, io.EOF
		}
		p[0] = b
		n := 1
		for n < len(p) {
			select {
			case b2 := <-f.inbound:
				p[n] = b2
				n++
			default:
				return n, nil
			}
		}
		return n, nil
	case <-time.After(fakeReadTimeout):
		return 0, nil // timeout → (0, nil), matches the SerialPort contract.
	case <-f.closed:
		return 0, io.EOF
	}
}

func (f *fakeSerial) Write(p []byte) (int, error) {
	for _, b := range p {
		select {
		case f.outbound <- b:
		case <-f.closed:
			return 0, io.ErrClosedPipe
		}
	}
	return len(p), nil
}

func (f *fakeSerial) SetLine(l LineParams) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.line = l.normalized()
	f.setCalls++
	return nil
}

func (f *fakeSerial) Close() error {
	f.once.Do(func() { close(f.closed) })
	return nil
}

// newTestBridge opens a bridge on an OS-chosen port backed by a fresh fake
// serial, and starts its accept loop. It returns the bridge, the fake, and the
// dial address.
func newTestBridge(t *testing.T) (*Bridge, *fakeSerial, string) {
	t.Helper()
	fake := newFakeSerial(defaultLine(9600))
	dev := DiscoveredPort{ID: "test0", DevPath: "/dev/fake0"}
	b, err := newBridge(dev, 0, "", defaultLine(9600), func(string, LineParams) (SerialPort, error) {
		return fake, nil
	})
	if err != nil {
		t.Fatalf("newBridge: %v", err)
	}
	go b.run()
	return b, fake, b.ln.Addr().String()
}

func TestBridge_ClientToSerial(t *testing.T) {
	b, fake, addr := newTestBridge(t)
	defer b.close()

	conn, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	// KISS/CCDI are just bytes, including 0xC0/0xFF — the bridge must be fully
	// transparent (no framing, no escaping).
	want := []byte{0xC0, 0x00, 0x11, 0x22, 0xFF, 0xC0}
	if _, err := conn.Write(want); err != nil {
		t.Fatalf("write: %v", err)
	}
	got := fake.deviceRead(t, len(want), 2*time.Second)
	if string(got) != string(want) {
		t.Errorf("device received %v, want %v", got, want)
	}
}

func TestBridge_SerialToClient(t *testing.T) {
	b, fake, addr := newTestBridge(t)
	defer b.close()

	conn, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))

	want := []byte{0xFF, 0x01, 0xC0, 0x7E, 0x00}
	fake.inject(want)
	got := make([]byte, len(want))
	if _, err := io.ReadFull(conn, got); err != nil {
		t.Fatalf("read: %v", err)
	}
	if string(got) != string(want) {
		t.Errorf("client received %v, want %v", got, want)
	}
}

// TestBridge_OneClientReconnects proves the accept loop serves one client at a
// time and cleanly re-accepts after a disconnect.
func TestBridge_OneClientReconnects(t *testing.T) {
	b, fake, addr := newTestBridge(t)
	defer b.close()

	for i := 0; i < 3; i++ {
		conn, err := net.Dial("tcp", addr)
		if err != nil {
			t.Fatalf("dial %d: %v", i, err)
		}
		msg := []byte{byte(i), 0xC0}
		if _, err := conn.Write(msg); err != nil {
			t.Fatalf("write %d: %v", i, err)
		}
		got := fake.deviceRead(t, len(msg), 2*time.Second)
		if got[0] != byte(i) {
			t.Errorf("round %d: device got %v, want first byte %d", i, got, i)
		}
		conn.Close()
		// Give the accept loop a moment to re-accept before the next dial.
		time.Sleep(20 * time.Millisecond)
	}
}

// TestNewBridge_BindsToBindAddr proves a non-empty bindAddr threads all the way
// through to net.Listen: the listener binds to that address (loopback here) and a
// client can still dial it. An empty bindAddr (all interfaces) is exercised by
// every other bridge test via newTestBridge.
func TestNewBridge_BindsToBindAddr(t *testing.T) {
	fake := newFakeSerial(defaultLine(9600))
	dev := DiscoveredPort{ID: "test0", DevPath: "/dev/fake0"}
	b, err := newBridge(dev, 0, "127.0.0.1", defaultLine(9600),
		func(string, LineParams) (SerialPort, error) { return fake, nil })
	if err != nil {
		t.Fatalf("newBridge: %v", err)
	}
	defer b.close()
	go b.run()

	addr := b.ln.Addr().(*net.TCPAddr)
	if !addr.IP.IsLoopback() {
		t.Errorf("listener bound to %s, want a loopback address (bindAddr=127.0.0.1)", addr)
	}

	// It is still dial-able on that address.
	conn, err := net.Dial("tcp", addr.String())
	if err != nil {
		t.Fatalf("dial %s: %v", addr, err)
	}
	defer conn.Close()
	want := []byte{0xC0, 0x42, 0xC0}
	if _, err := conn.Write(want); err != nil {
		t.Fatalf("write: %v", err)
	}
	if got := fake.deviceRead(t, len(want), 2*time.Second); string(got) != string(want) {
		t.Errorf("device received %v, want %v", got, want)
	}
}

func TestBridge_SetLineUpdatesCacheAndDevice(t *testing.T) {
	fake := newFakeSerial(defaultLine(9600))
	b, err := newBridge(DiscoveredPort{ID: "t", DevPath: "/dev/fake"}, 0, "", defaultLine(9600),
		func(string, LineParams) (SerialPort, error) { return fake, nil })
	if err != nil {
		t.Fatalf("newBridge: %v", err)
	}
	defer b.close()

	got, err := b.SetLine(LineParams{Baud: 28800})
	if err != nil {
		t.Fatalf("SetLine: %v", err)
	}
	// Partial request (baud only) → 8N1 defaults filled in.
	if got.Baud != 28800 || got.DataBits != 8 || got.Parity != "none" || got.StopBits != 1 {
		t.Errorf("effective params = %+v, want 28800 8N1", got)
	}
	if b.currentLine().Baud != 28800 {
		t.Errorf("cached baud = %d, want 28800", b.currentLine().Baud)
	}
	if fake.setCalls != 1 || fake.line.Baud != 28800 {
		t.Errorf("device SetLine not applied: calls=%d line=%+v", fake.setCalls, fake.line)
	}
}
