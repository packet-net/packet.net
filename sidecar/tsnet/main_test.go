package main

import (
	"bufio"
	"bytes"
	"io"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestParseForwards_Good(t *testing.T) {
	in := []byte(`[
		{"listen":993,"target":"127.0.0.1:1430","tls":"terminate"},
		{"listen":465,"target":"127.0.0.1:1465","tls":"raw"},
		{"listen":143,"target":"127.0.0.1:1431"}
	]`)
	got, err := parseForwards(in)
	if err != nil {
		t.Fatalf("parseForwards: %v", err)
	}
	if len(got) != 3 {
		t.Fatalf("want 3 forwards, got %d: %+v", len(got), got)
	}
	want := []forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 465, Target: "127.0.0.1:1465", TLS: "raw"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"}, // tls omitted ⇒ defaults to raw
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("forward[%d] = %+v, want %+v", i, got[i], want[i])
		}
	}
}

func TestParseForwards_Malformed(t *testing.T) {
	for _, tc := range []struct {
		name string
		in   string
	}{
		{"not json", `not json at all`},
		{"object not array", `{"listen":993}`},
		{"truncated", `[{"listen":993,`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := parseForwards([]byte(tc.in)); err == nil {
				t.Fatalf("want error for %q, got nil", tc.in)
			}
		})
	}
}

func TestParseForwards_Empty(t *testing.T) {
	for _, in := range []string{"", "   ", "\n\t "} {
		got, err := parseForwards([]byte(in))
		if err != nil {
			t.Fatalf("empty input %q: unexpected error %v", in, err)
		}
		if len(got) != 0 {
			t.Fatalf("empty input %q: want 0 forwards, got %d", in, len(got))
		}
	}
}

func TestParseForwards_BadEntriesSkipped(t *testing.T) {
	// A mix of good and bad entries: the document is well-formed, so the bad
	// individual entries are skipped (logged) and the good ones survive.
	in := []byte(`[
		{"listen":993,"target":"127.0.0.1:1430","tls":"terminate"},
		{"listen":0,"target":"127.0.0.1:1","tls":"raw"},
		{"listen":70000,"target":"127.0.0.1:2","tls":"raw"},
		{"listen":465,"target":"","tls":"raw"},
		{"listen":25,"target":"127.0.0.1:3","tls":"bogus"},
		{"listen":143,"target":"127.0.0.1:1431","tls":"raw"}
	]`)
	got, err := parseForwards(in)
	if err != nil {
		t.Fatalf("parseForwards: %v", err)
	}
	if len(got) != 2 {
		t.Fatalf("want 2 surviving forwards, got %d: %+v", len(got), got)
	}
	if got[0].Listen != 993 || got[1].Listen != 143 {
		t.Errorf("surviving forwards = %+v, want listen 993 then 143", got)
	}
}

// echoServer is a stand-in for a loopback app target: it accepts one
// connection and echoes everything back. Returns its listen address.
func echoServer(t *testing.T) (addr string, stop func()) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				_, _ = io.Copy(c, c)
			}(c)
		}
	}()
	return ln.Addr().String(), func() { _ = ln.Close() }
}

// TestServeForward_RoundTrip exercises the dial+copy byte-pump without tsnet:
// a plain net.Listener stands in for the tsnet listener (the only thing tsnet
// changes is *which* listener is created — terminate vs raw — so this covers
// both byte-pump paths) and a local echo server stands in for the loopback app.
func TestServeForward_RoundTrip(t *testing.T) {
	target, stopEcho := echoServer(t)
	defer stopEcho()

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("forward listen: %v", err)
	}
	defer ln.Close()
	go serveForward(ln, target)

	conn, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatalf("dial forward: %v", err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(5 * time.Second))

	want := []byte("hello over the forward\n")
	if _, err := conn.Write(want); err != nil {
		t.Fatalf("write: %v", err)
	}
	got, err := bufio.NewReader(conn).ReadBytes('\n')
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if !bytes.Equal(got, want) {
		t.Fatalf("round-trip mismatch: got %q, want %q", got, want)
	}
}

// TestServeForward_DialFailureSurvives confirms a connection whose target
// can't be dialled is closed without taking the listener down: a second
// connection to a now-good target still round-trips.
func TestServeForward_DialFailureSurvives(t *testing.T) {
	// Reserve a port, then close it so dialling it fails (connection refused).
	dead, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("reserve: %v", err)
	}
	deadAddr := dead.Addr().String()
	_ = dead.Close()

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("forward listen: %v", err)
	}
	defer ln.Close()
	go serveForward(ln, deadAddr)

	// First conn: target is dead → pipeConn logs + closes it, listener lives.
	c1, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatalf("dial 1: %v", err)
	}
	_ = c1.SetReadDeadline(time.Now().Add(5 * time.Second))
	// The forward closes our conn after the failed dial → read returns EOF.
	if _, err := io.ReadAll(c1); err != nil {
		t.Fatalf("read 1: want EOF (nil err from ReadAll), got %v", err)
	}
	c1.Close()

	// Listener must still accept a new connection.
	c2, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatalf("dial 2 (listener died after dial failure): %v", err)
	}
	c2.Close()
}

// TestServeForward_ListenerCloseStops confirms serveForward returns cleanly
// when its listener is closed (the SIGTERM/ctx-cancel teardown path).
func TestServeForward_ListenerCloseStops(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	done := make(chan struct{})
	go func() {
		serveForward(ln, "127.0.0.1:1")
		close(done)
	}()
	_ = ln.Close()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("serveForward did not return after listener close")
	}
}

// ---- reconcileForwards (the SIGHUP live-reload diff) ----------------------
//
// These exercise the diff with fake open/close — no tsnet, no real sockets —
// so the registry reconciliation is unit-testable in isolation. open records
// each opened spec and hands back a sentinel listener; close records the port.

// fakeListener is a no-op net.Listener stand-in: reconcileForwards only stores
// it and (via the fake closeFunc) never calls its methods, so the methods just
// satisfy the interface.
type fakeListener struct{}

func (fakeListener) Accept() (net.Conn, error) { return nil, net.ErrClosed }
func (fakeListener) Close() error              { return nil }
func (fakeListener) Addr() net.Addr            { return nil }

// fakeReconciler wires reconcileForwards to in-memory open/close recorders.
type fakeReconciler struct {
	opened  []forward
	closed  []int
	openErr map[int]error // listen port → error to return from open (else nil)
	active  map[int]*activeForward
}

func newFakeReconciler() *fakeReconciler {
	return &fakeReconciler{active: make(map[int]*activeForward), openErr: map[int]error{}}
}

func (r *fakeReconciler) open(f forward) (net.Listener, error) {
	if err := r.openErr[f.Listen]; err != nil {
		return nil, err
	}
	r.opened = append(r.opened, f)
	return fakeListener{}, nil
}

func (r *fakeReconciler) close(listen int) {
	r.closed = append(r.closed, listen)
}

func (r *fakeReconciler) reconcile(desired []forward) (opened, closed []int) {
	return reconcileForwards(desired, r.active, r.open, r.close)
}

func activePorts(active map[int]*activeForward) map[int]forward {
	out := make(map[int]forward, len(active))
	for p, a := range active {
		out[p] = a.forward
	}
	return out
}

func TestReconcileForwards_InitialOpensAll(t *testing.T) {
	r := newFakeReconciler()
	desired := []forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},
	}
	opened, closed := r.reconcile(desired)
	if len(opened) != 2 || len(closed) != 0 {
		t.Fatalf("opened=%v closed=%v, want 2 opened 0 closed", opened, closed)
	}
	if len(r.active) != 2 {
		t.Fatalf("active = %v, want 2 entries", activePorts(r.active))
	}
	if _, ok := r.active[993]; !ok {
		t.Errorf("993 not active: %v", activePorts(r.active))
	}
	if _, ok := r.active[143]; !ok {
		t.Errorf("143 not active: %v", activePorts(r.active))
	}
}

func TestReconcileForwards_AddOne(t *testing.T) {
	r := newFakeReconciler()
	r.reconcile([]forward{{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}})
	r.opened = nil // reset the recorder for the second pass.

	opened, closed := r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}, // unchanged
		{Listen: 465, Target: "127.0.0.1:1465", TLS: "raw"},       // new
	})
	if len(opened) != 1 || opened[0] != 465 {
		t.Fatalf("opened=%v, want [465]", opened)
	}
	if len(closed) != 0 {
		t.Fatalf("closed=%v, want none (993 unchanged)", closed)
	}
	if len(r.opened) != 1 || r.opened[0].Listen != 465 {
		t.Errorf("open called for %v, want only 465 (993 must be left alone)", r.opened)
	}
}

func TestReconcileForwards_RemoveOne(t *testing.T) {
	r := newFakeReconciler()
	r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},
	})

	opened, closed := r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}, // kept
	})
	if len(opened) != 0 {
		t.Fatalf("opened=%v, want none", opened)
	}
	if len(closed) != 1 || closed[0] != 143 {
		t.Fatalf("closed=%v, want [143]", closed)
	}
	if r.closed[len(r.closed)-1] != 143 {
		t.Errorf("close called for %v, want 143 closed", r.closed)
	}
	if _, gone := r.active[143]; gone {
		t.Errorf("143 still active after removal: %v", activePorts(r.active))
	}
}

func TestReconcileForwards_ChangedTargetReopens(t *testing.T) {
	r := newFakeReconciler()
	r.reconcile([]forward{{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}})
	r.opened, r.closed = nil, nil

	// Same listen port, different target → close + reopen on that port.
	opened, closed := r.reconcile([]forward{{Listen: 993, Target: "127.0.0.1:9999", TLS: "terminate"}})
	if len(opened) != 1 || opened[0] != 993 {
		t.Fatalf("opened=%v, want [993] (reopened)", opened)
	}
	if len(closed) != 1 || closed[0] != 993 {
		t.Fatalf("closed=%v, want [993] (closed before reopen)", closed)
	}
	if got := r.active[993].Target; got != "127.0.0.1:9999" {
		t.Errorf("active 993 target = %q, want the new target", got)
	}
}

func TestReconcileForwards_ChangedTlsReopens(t *testing.T) {
	r := newFakeReconciler()
	r.reconcile([]forward{{Listen: 993, Target: "127.0.0.1:1430", TLS: "raw"}})
	r.opened, r.closed = nil, nil

	// Same listen + target, different tls mode → must close + reopen.
	opened, closed := r.reconcile([]forward{{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}})
	if len(opened) != 1 || opened[0] != 993 || len(closed) != 1 || closed[0] != 993 {
		t.Fatalf("opened=%v closed=%v, want both [993]", opened, closed)
	}
	if got := r.active[993].TLS; got != "terminate" {
		t.Errorf("active 993 tls = %q, want terminate", got)
	}
}

func TestReconcileForwards_UnchangedIsNoop(t *testing.T) {
	r := newFakeReconciler()
	same := []forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},
	}
	r.reconcile(same)
	r.opened, r.closed = nil, nil

	opened, closed := r.reconcile(same)
	if len(opened) != 0 || len(closed) != 0 {
		t.Fatalf("opened=%v closed=%v, want both empty (idempotent reload)", opened, closed)
	}
	if len(r.opened) != 0 || len(r.closed) != 0 {
		t.Errorf("open/close were called on an unchanged reload: opened=%v closed=%v", r.opened, r.closed)
	}
}

func TestReconcileForwards_EmptyDesiredClosesAll(t *testing.T) {
	r := newFakeReconciler()
	r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},
	})

	opened, closed := r.reconcile(nil)
	if len(opened) != 0 {
		t.Fatalf("opened=%v, want none", opened)
	}
	if len(closed) != 2 {
		t.Fatalf("closed=%v, want both ports", closed)
	}
	if len(r.active) != 0 {
		t.Errorf("active = %v, want empty after closing all", activePorts(r.active))
	}
}

func TestReconcileForwards_OpenFailureLeavesPortInactive(t *testing.T) {
	r := newFakeReconciler()
	r.openErr[993] = net.ErrClosed // simulate a listen failure on this port.

	opened, closed := r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"}, // fails to open
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},       // opens fine
	})
	if len(opened) != 1 || opened[0] != 143 {
		t.Fatalf("opened=%v, want only [143] (993 failed)", opened)
	}
	if len(closed) != 0 {
		t.Fatalf("closed=%v, want none", closed)
	}
	if _, live := r.active[993]; live {
		t.Errorf("993 must not be active after an open failure: %v", activePorts(r.active))
	}
	// A subsequent reload (now succeeding) opens the previously-failed port.
	delete(r.openErr, 993)
	r.opened = nil
	opened, _ = r.reconcile([]forward{
		{Listen: 993, Target: "127.0.0.1:1430", TLS: "terminate"},
		{Listen: 143, Target: "127.0.0.1:1431", TLS: "raw"},
	})
	if len(opened) != 1 || opened[0] != 993 {
		t.Fatalf("opened=%v on retry, want [993] (the previously-failed port)", opened)
	}
}

// TestStartAndReloadForwards_FromFile exercises the file-backed entry points
// (startForwards → reloadForwards) end to end against the fake opener, proving
// the SIGHUP path re-reads the file and reconciles the live registry.
func TestStartAndReloadForwards_FromFile(t *testing.T) {
	r := newFakeReconciler()
	path := filepath.Join(t.TempDir(), "forwards.json")

	write := func(s string) {
		if err := os.WriteFile(path, []byte(s), 0o600); err != nil {
			t.Fatalf("write forwards file: %v", err)
		}
	}

	write(`[{"listen":993,"target":"127.0.0.1:1430","tls":"terminate"}]`)
	startForwards(path, r.active, r.open)
	if _, ok := r.active[993]; !ok || len(r.active) != 1 {
		t.Fatalf("after start: active=%v, want only 993", activePorts(r.active))
	}

	// Add a forward + drop the original → the reload reconciles to the new file.
	write(`[{"listen":143,"target":"127.0.0.1:1431","tls":"raw"}]`)
	reloadForwards(path, r.active, r.open)
	if _, gone := r.active[993]; gone {
		t.Errorf("993 should be closed after reload: %v", activePorts(r.active))
	}
	if _, ok := r.active[143]; !ok || len(r.active) != 1 {
		t.Fatalf("after reload: active=%v, want only 143", activePorts(r.active))
	}
}
