package main

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// notifyListener binds a unixgram socket standing in for systemd's
// $NOTIFY_SOCKET and returns its path plus a reader for one datagram.
func notifyListener(t *testing.T) (string, func(timeout time.Duration) string) {
	t.Helper()
	// Keep the path short: unix socket paths cap at ~108 bytes and t.TempDir()
	// can exceed that on deep working trees.
	dir, err := os.MkdirTemp("", "sdn")
	if err != nil {
		t.Fatalf("mkdtemp: %v", err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(dir) })
	path := filepath.Join(dir, "notify.sock")
	conn, err := net.ListenUnixgram("unixgram", &net.UnixAddr{Name: path, Net: "unixgram"})
	if err != nil {
		t.Fatalf("listen unixgram: %v", err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	return path, func(timeout time.Duration) string {
		_ = conn.SetReadDeadline(time.Now().Add(timeout))
		buf := make([]byte, 256)
		n, err := conn.Read(buf)
		if err != nil {
			t.Fatalf("read notify datagram: %v", err)
		}
		return string(buf[:n])
	}
}

// TestSdNotifyTo_WritesDatagram proves the hand-rolled writer delivers the
// exact sd_notify state string as one datagram on the notify socket.
func TestSdNotifyTo_WritesDatagram(t *testing.T) {
	path, read := notifyListener(t)
	if !sdNotifyTo(path, "READY=1") {
		t.Fatal("sdNotifyTo returned false against a live socket")
	}
	if got := read(2 * time.Second); got != "READY=1" {
		t.Errorf("datagram = %q, want READY=1", got)
	}
}

// TestSdNotify_UsesNotifySocketEnv proves the env-guarded entry point: with
// NOTIFY_SOCKET set it delivers, and the boolean reports success.
func TestSdNotify_UsesNotifySocketEnv(t *testing.T) {
	path, read := notifyListener(t)
	t.Setenv("NOTIFY_SOCKET", path)
	if !sdNotify("WATCHDOG=1") {
		t.Fatal("sdNotify returned false with NOTIFY_SOCKET set")
	}
	if got := read(2 * time.Second); got != "WATCHDOG=1" {
		t.Errorf("datagram = %q, want WATCHDOG=1", got)
	}
}

// TestSdNotifyTo_NoSocketIsNoop: the direct-run path — no socket, no error, no
// send, false returned (so callers can gate their "notified" log line).
func TestSdNotifyTo_NoSocketIsNoop(t *testing.T) {
	if sdNotifyTo("", "READY=1") {
		t.Error("sdNotifyTo(\"\") = true, want false (not under systemd)")
	}
	if sdNotifyTo("/nonexistent/notify.sock", "READY=1") {
		t.Error("sdNotifyTo(dead socket) = true, want false")
	}
}

func TestWatchdogInterval(t *testing.T) {
	pid := 4242
	env := func(m map[string]string) func(string) (string, bool) {
		return func(k string) (string, bool) { v, ok := m[k]; return v, ok }
	}
	cases := []struct {
		name string
		vars map[string]string
		want time.Duration
	}{
		{"unset", map[string]string{}, 0},
		{"armed 30s", map[string]string{"WATCHDOG_USEC": "30000000"}, 30 * time.Second},
		{"armed with matching pid", map[string]string{"WATCHDOG_USEC": "5000000", "WATCHDOG_PID": "4242"}, 5 * time.Second},
		{"pid for another process", map[string]string{"WATCHDOG_USEC": "5000000", "WATCHDOG_PID": "1"}, 0},
		{"junk usec", map[string]string{"WATCHDOG_USEC": "soon"}, 0},
		{"zero usec", map[string]string{"WATCHDOG_USEC": "0"}, 0},
	}
	for _, tc := range cases {
		if got := watchdogInterval(pid, env(tc.vars)); got != tc.want {
			t.Errorf("%s: watchdogInterval = %s, want %s", tc.name, got, tc.want)
		}
	}
}

// TestStartWatchdog_Heartbeats proves the ticker sends repeated WATCHDOG=1 at
// half the armed interval and stops on context cancel.
func TestStartWatchdog_Heartbeats(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	beats := make(chan string, 32)
	if !startWatchdog(ctx, 40*time.Millisecond, func(s string) bool {
		beats <- s
		return true
	}) {
		t.Fatal("startWatchdog = false for a positive interval")
	}

	for i := 0; i < 2; i++ {
		select {
		case s := <-beats:
			if s != "WATCHDOG=1" {
				t.Fatalf("heartbeat %d = %q, want WATCHDOG=1", i, s)
			}
		case <-time.After(2 * time.Second):
			t.Fatalf("no heartbeat %d within 2s (tick should be ~20ms)", i)
		}
	}

	cancel()
	// Drain anything in flight, then confirm silence.
	time.Sleep(60 * time.Millisecond)
	for len(beats) > 0 {
		<-beats
	}
	select {
	case s := <-beats:
		t.Errorf("heartbeat %q after cancel", s)
	case <-time.After(120 * time.Millisecond):
	}
}

// TestStartWatchdog_NotArmed: a zero interval must start nothing.
func TestStartWatchdog_NotArmed(t *testing.T) {
	if startWatchdog(context.Background(), 0, func(string) bool {
		t.Error("notify called with no watchdog armed")
		return true
	}) {
		t.Error("startWatchdog = true for interval 0")
	}
}
