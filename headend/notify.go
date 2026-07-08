package main

// Hand-rolled sd_notify(3) support (#583), so the unit can run Type=notify +
// WatchdogSec without pulling in a dependency: READY=1 once the listeners are
// up, WATCHDOG=1 heartbeats at half the armed interval, STOPPING=1 on shutdown.
// Everything is guarded on $NOTIFY_SOCKET, which only a notify-expecting
// service manager sets — a direct run (dev box, container smoke, tests) takes
// the no-op path and behaves exactly as before.

import (
	"context"
	"log"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

// sdNotify sends one sd_notify state datagram (e.g. "READY=1") to the socket in
// $NOTIFY_SOCKET. Returns true only when a socket was present AND the write
// succeeded; false — silently for the common not-under-systemd case — otherwise.
func sdNotify(state string) bool {
	return sdNotifyTo(os.Getenv("NOTIFY_SOCKET"), state)
}

// sdNotifyTo is sdNotify against an explicit socket path (the testable seam).
// A leading "@" names a Linux abstract-namespace socket, per sd_notify(3).
func sdNotifyTo(socketPath, state string) bool {
	if socketPath == "" || state == "" {
		return false
	}
	name := socketPath
	if name[0] == '@' {
		name = "\x00" + name[1:]
	}
	conn, err := net.DialUnix("unixgram", nil, &net.UnixAddr{Name: name, Net: "unixgram"})
	if err != nil {
		log.Printf("sd_notify: dial %q: %v", socketPath, err)
		return false
	}
	defer conn.Close()
	if _, err := conn.Write([]byte(state)); err != nil {
		log.Printf("sd_notify: write %q: %v", state, err)
		return false
	}
	return true
}

// watchdogInterval reads the watchdog armed for THIS process from the
// environment, per sd_watchdog_enabled(3): WATCHDOG_USEC is the period in
// microseconds, and WATCHDOG_PID (when set) must match our pid — systemd sets
// it so a forked child doesn't feed the parent's watchdog. Returns 0 when no
// watchdog is armed (unset/invalid USEC, or a PID naming another process).
func watchdogInterval(pid int, env func(string) (string, bool)) time.Duration {
	v, ok := env("WATCHDOG_USEC")
	if !ok {
		return 0
	}
	usec, err := strconv.ParseInt(strings.TrimSpace(v), 10, 64)
	if err != nil || usec <= 0 {
		return 0
	}
	if p, ok := env("WATCHDOG_PID"); ok {
		n, err := strconv.Atoi(strings.TrimSpace(p))
		if err != nil || n != pid {
			return 0
		}
	}
	return time.Duration(usec) * time.Microsecond
}

// startWatchdog begins the WATCHDOG=1 heartbeat at HALF the armed interval (the
// sd_watchdog_enabled(3) recommendation), stopping when ctx is cancelled.
// Returns false — and starts nothing — when no watchdog is armed (interval 0).
func startWatchdog(ctx context.Context, interval time.Duration, notify func(string) bool) bool {
	if interval <= 0 {
		return false
	}
	tick := interval / 2
	if tick <= 0 {
		tick = interval
	}
	go func() {
		t := time.NewTicker(tick)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
				notify("WATCHDOG=1")
			}
		}
	}()
	return true
}
