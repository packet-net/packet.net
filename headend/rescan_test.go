package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"sync"
	"testing"
	"time"
)

// --- fakes / helpers -------------------------------------------------------

// mkDev builds a DiscoveredPort with a stable by-id-style identity.
func mkDev(id, devPath string) DiscoveredPort {
	return DiscoveredPort{ID: id, DevPath: devPath, IDSource: idSourceByID, IDStable: true}
}

// scriptedEnumerator returns a DIFFERENT device set on each successive
// Enumerate() call (the last step repeats), so a test can simulate a device
// appearing / disappearing between polls. It counts calls and can signal its
// first call, for the rescan-loop wiring tests.
type scriptedEnumerator struct {
	mu        sync.Mutex
	calls     int
	steps     [][]DiscoveredPort
	firstCall chan struct{}
	once      sync.Once
}

func (s *scriptedEnumerator) Enumerate() []DiscoveredPort {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.firstCall != nil {
		s.once.Do(func() { close(s.firstCall) })
	}
	i := s.calls
	s.calls++
	if len(s.steps) == 0 {
		return nil
	}
	if i >= len(s.steps) {
		i = len(s.steps) - 1
	}
	return s.steps[i]
}

func (s *scriptedEnumerator) callCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.calls
}

// countingOpener returns a SerialOpener backed by fresh fakeSerials. fail maps a
// devPath to the number of leading opens that should FAIL (busy) before it then
// succeeds — for the transient-failure retry test.
func countingOpener(fail map[string]int) SerialOpener {
	var mu sync.Mutex
	return func(devPath string, l LineParams) (SerialPort, error) {
		mu.Lock()
		defer mu.Unlock()
		if fail[devPath] > 0 {
			fail[devPath]--
			return nil, fmt.Errorf("fake open %s: device busy", devPath)
		}
		return newFakeSerial(l), nil
	}
}

// useLoopbackListeners points the bridge listener seam at a hermetic loopback
// binder (ignores the logical addr, binds 127.0.0.1:0) for the test, so rescan
// tests never contend for a real fixed TCP port while the bridge's LOGICAL
// Bridge.tcpPort assertions stay deterministic.
func useLoopbackListeners(t *testing.T) {
	t.Helper()
	prev := netListen
	netListen = func(_, _ string) (net.Listener, error) { return net.Listen("tcp", "127.0.0.1:0") }
	t.Cleanup(func() { netListen = prev })
}

// assertInventoryIDs asserts GET /inventory reflects exactly the given ids.
func assertInventoryIDs(t *testing.T, reg *Registry, wantIDs ...string) {
	t.Helper()
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, httptest.NewRequest("GET", "/inventory", nil))
	if rr.Code != http.StatusOK {
		t.Fatalf("/inventory status = %d, want 200", rr.Code)
	}
	var inv InventoryResponse
	if err := json.Unmarshal(rr.Body.Bytes(), &inv); err != nil {
		t.Fatalf("decode inventory: %v", err)
	}
	got := map[string]bool{}
	for _, p := range inv.Ports {
		got[p.ID] = true
	}
	if len(inv.Ports) != len(wantIDs) {
		t.Fatalf("/inventory has %d ports %v, want %d %v", len(inv.Ports), got, len(wantIDs), wantIDs)
	}
	for _, id := range wantIDs {
		if !got[id] {
			t.Errorf("/inventory missing id %q (got %v)", id, got)
		}
	}
}

func hotplugConfig() Config {
	return Config{BaseTCPPort: 7301, Allow: []string{"*"}}
}

// --- diffDevices (pure) ----------------------------------------------------

func TestDiffDevices(t *testing.T) {
	bridgeFor := func(id, dp string) *Bridge { return &Bridge{dev: mkDev(id, dp)} }
	ids := func(bs []*Bridge) []string {
		out := make([]string, len(bs))
		for i, b := range bs {
			out[i] = b.dev.ID
		}
		return out
	}

	t.Run("add new, remove gone, keep existing", func(t *testing.T) {
		current := []*Bridge{bridgeFor("a", "/dev/ttyUSB0"), bridgeFor("b", "/dev/ttyUSB1")}
		enum := []DiscoveredPort{mkDev("a", "/dev/ttyUSB0"), mkDev("c", "/dev/ttyUSB2")}
		added, removed := diffDevices(enum, current)
		if len(added) != 1 || added[0].ID != "c" {
			t.Errorf("added = %v, want [c]", added)
		}
		if len(removed) != 1 || removed[0].dev.ID != "b" {
			t.Errorf("removed = %v, want [b]", ids(removed))
		}
	})

	t.Run("same devPath different id is not churned (by-path to by-id upgrade)", func(t *testing.T) {
		// Poll 1 saw the device via by-path (id = the by-path name); poll 2 sees the
		// SAME kernel path via by-id (id changed) — it must stay ONE bridge, untouched.
		current := []*Bridge{bridgeFor("by-path-x", "/dev/ttyUSB0")}
		enum := []DiscoveredPort{mkDev("usb-by-id-x", "/dev/ttyUSB0")}
		added, removed := diffDevices(enum, current)
		if len(added) != 0 || len(removed) != 0 {
			t.Errorf("id shift on same devPath churned: added=%v removed=%v", added, ids(removed))
		}
	})

	t.Run("empty enumeration removes all", func(t *testing.T) {
		current := []*Bridge{bridgeFor("a", "/dev/ttyUSB0")}
		added, removed := diffDevices(nil, current)
		if len(added) != 0 || len(removed) != 1 {
			t.Errorf("added=%v removed=%v, want [] and [a]", added, ids(removed))
		}
	})
}

// --- reconcile: a device appears between polls -----------------------------

func TestReconcile_DeviceAppears_AddsBridge(t *testing.T) {
	useLoopbackListeners(t)
	cfg := hotplugConfig()
	d0, d1 := mkDev("dev0", "/dev/ttyUSB0"), mkDev("dev1", "/dev/ttyUSB1")
	enum := &scriptedEnumerator{steps: [][]DiscoveredPort{
		{d0},     // poll 1: only dev0 present
		{d0, d1}, // poll 2: dev1 hot-plugged in
	}}
	reg := newRegistry("pi", nil)
	warned := map[string]bool{}
	open := countingOpener(nil)
	line := defaultLine(9600)

	reg.reconcile(cfg, enum, open, line, warned) // adds dev0
	assertInventoryIDs(t, reg, "dev0")

	reg.reconcile(cfg, enum, open, line, warned) // dev1 appears → new bridge
	assertInventoryIDs(t, reg, "dev0", "dev1")

	b1, ok := reg.lookup("dev1")
	if !ok {
		t.Fatal("dev1 not bridged after hot-plug")
	}
	if b1.tcpPort != 7302 {
		t.Errorf("dev1 tcpPort = %d, want 7302 (next free ≥ base after dev0=7301)", b1.tcpPort)
	}
}

// --- reconcile: a device disappears; its bridge is Closed + port freed ------

func TestReconcile_DeviceDisappears_RemovesBridgeAndFreesPort(t *testing.T) {
	useLoopbackListeners(t)
	cfg := hotplugConfig()
	d0 := mkDev("dev0", "/dev/ttyUSB0")
	d1 := mkDev("dev1", "/dev/ttyUSB1")
	d2 := mkDev("dev2", "/dev/ttyUSB2")
	enum := &scriptedEnumerator{steps: [][]DiscoveredPort{
		{d0, d1}, // both present
		{d0},     // dev1 unplugged
		{d0, d2}, // dev2 plugged — should reuse dev1's freed port (7302)
	}}
	reg := newRegistry("pi", nil)
	warned := map[string]bool{}
	open := countingOpener(nil)
	line := defaultLine(9600)

	reg.reconcile(cfg, enum, open, line, warned) // add dev0(7301), dev1(7302)
	b1, _ := reg.lookup("dev1")
	if b1 == nil || b1.tcpPort != 7302 {
		t.Fatalf("dev1 setup: bridge=%v want port 7302", b1)
	}
	fake1 := b1.port.(*fakeSerial)

	reg.reconcile(cfg, enum, open, line, warned) // dev1 gone → Close + remove
	if _, ok := reg.lookup("dev1"); ok {
		t.Error("dev1 still bridged after unplug")
	}
	assertInventoryIDs(t, reg, "dev0")
	select {
	case <-fake1.closed: // Close closed the serial handle
	default:
		t.Error("dev1 serial handle not closed on removal")
	}

	reg.reconcile(cfg, enum, open, line, warned) // dev2 → reuses the freed 7302
	b2, ok := reg.lookup("dev2")
	if !ok {
		t.Fatal("dev2 not bridged")
	}
	if b2.tcpPort != 7302 {
		t.Errorf("dev2 tcpPort = %d, want 7302 (dev1's freed port reused, lowest free ≥ base)", b2.tcpPort)
	}
}

// --- reconcile: an existing bridge + its connected client stay untouched ----

func TestReconcile_ExistingBridgeAndClientUntouched(t *testing.T) {
	useLoopbackListeners(t)
	cfg := hotplugConfig()
	d0 := mkDev("dev0", "/dev/ttyUSB0")
	enum := &scriptedEnumerator{steps: [][]DiscoveredPort{
		{d0}, // poll 1: add dev0
		{d0}, // poll 2: unchanged — must not disturb the bridge or its client
	}}
	reg := newRegistry("pi", nil)
	warned := map[string]bool{}
	open := countingOpener(nil)
	line := defaultLine(9600)

	reg.reconcile(cfg, enum, open, line, warned)
	b0, ok := reg.lookup("dev0")
	if !ok {
		t.Fatal("dev0 not bridged")
	}
	fake0 := b0.port.(*fakeSerial)

	// Connect a client and prove bytes flow through the pipe.
	conn, err := net.Dial("tcp", b0.ln.Addr().String())
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	if _, err := conn.Write([]byte{0xC0, 0x11, 0xC0}); err != nil {
		t.Fatalf("write: %v", err)
	}
	fake0.deviceRead(t, 3, 2*time.Second)

	// A poll with the SAME device set is a no-op on the delta.
	reg.reconcile(cfg, enum, open, line, warned)

	if b0again, ok := reg.lookup("dev0"); !ok || b0again != b0 {
		t.Fatal("existing bridge replaced across an unchanged poll")
	}
	select {
	case <-fake0.closed:
		t.Fatal("existing bridge's serial handle was closed by an unchanged poll")
	default:
	}
	// The still-connected client keeps working across the poll.
	if _, err := conn.Write([]byte{0x22, 0xC0}); err != nil {
		t.Fatalf("write after poll: %v", err)
	}
	fake0.deviceRead(t, 2, 2*time.Second)
	if n := len(reg.snapshot()); n != 1 {
		t.Errorf("bridge count = %d across an unchanged poll, want 1 (unchanged)", n)
	}
}

// --- reconcile: a new device whose open fails is skipped, retried, warned once

func TestReconcile_OpenFails_SkippedRetriedWarnedOnce(t *testing.T) {
	useLoopbackListeners(t)
	var buf bytes.Buffer
	log.SetOutput(&buf)
	t.Cleanup(func() { log.SetOutput(os.Stderr) })

	cfg := hotplugConfig()
	d1 := mkDev("dev1", "/dev/ttyUSB1")
	enum := &scriptedEnumerator{steps: [][]DiscoveredPort{{d1}, {d1}, {d1}}}
	reg := newRegistry("pi", nil)
	warned := map[string]bool{}
	open := countingOpener(map[string]int{"/dev/ttyUSB1": 2}) // fail twice, then succeed
	line := defaultLine(9600)

	reg.reconcile(cfg, enum, open, line, warned) // open fails → deferred + warned
	if _, ok := reg.lookup("dev1"); ok {
		t.Fatal("dev1 bridged despite a failed open")
	}
	if !warned["dev1"] {
		t.Fatal("dev1 not recorded in the warned set after its first failed open")
	}

	reg.reconcile(cfg, enum, open, line, warned) // fails again → NOT re-logged (retry, quiet)
	if _, ok := reg.lookup("dev1"); ok {
		t.Fatal("dev1 bridged on the second failing poll")
	}

	reg.reconcile(cfg, enum, open, line, warned) // open recovers → bridged, warned cleared
	if _, ok := reg.lookup("dev1"); !ok {
		t.Fatal("dev1 not bridged after its open recovered")
	}
	if warned["dev1"] {
		t.Error("dev1 still in the warned set after a successful add")
	}

	if n := strings.Count(buf.String(), "bridge deferred dev1"); n != 1 {
		t.Errorf("deferred-open warning logged %d times, want exactly 1\nlog:\n%s", n, buf.String())
	}
}

// --- startRescan gate: interval 0 disables the poll (startup-only) -----------

func TestStartRescan_ZeroIntervalDisabled(t *testing.T) {
	cfg := hotplugConfig()
	cfg.RescanInterval = 0
	enum := &scriptedEnumerator{steps: [][]DiscoveredPort{{mkDev("dev0", "/dev/ttyUSB0")}}}
	reg := newRegistry("pi", nil)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	if startRescan(ctx, reg, cfg, enum, countingOpener(nil), defaultLine(9600)) {
		t.Fatal("startRescan returned true for interval 0, want false (rescan disabled)")
	}
	// Give any erroneously-started loop time to poll, then confirm none did.
	time.Sleep(50 * time.Millisecond)
	if n := enum.callCount(); n != 0 {
		t.Errorf("Enumerate called %d times with rescan disabled, want 0 (no rescan goroutine)", n)
	}
	if n := len(reg.snapshot()); n != 0 {
		t.Errorf("registry gained %d bridges with rescan disabled, want 0", n)
	}
}

// --- startRescan gate: a positive interval runs the poll loop ---------------

func TestStartRescan_PositiveIntervalPolls(t *testing.T) {
	useLoopbackListeners(t)
	first := make(chan struct{})
	cfg := hotplugConfig()
	cfg.RescanInterval = Duration(5 * time.Millisecond)
	enum := &scriptedEnumerator{
		steps:     [][]DiscoveredPort{{mkDev("dev0", "/dev/ttyUSB0")}},
		firstCall: first,
	}
	reg := newRegistry("pi", nil)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	if !startRescan(ctx, reg, cfg, enum, countingOpener(nil), defaultLine(9600)) {
		t.Fatal("startRescan returned false for a positive interval, want true")
	}
	select {
	case <-first:
	case <-time.After(2 * time.Second):
		t.Fatal("rescan loop never polled within 2s")
	}

	// The poll bridged the enumerated device — the hot-plug wiring is live.
	deadline := time.After(2 * time.Second)
	for {
		if _, ok := reg.lookup("dev0"); ok {
			break
		}
		select {
		case <-deadline:
			t.Fatal("dev0 never bridged by the rescan loop")
		case <-time.After(5 * time.Millisecond):
		}
	}
	cancel() // loop returns on ctx cancel (SIGTERM path)
}
