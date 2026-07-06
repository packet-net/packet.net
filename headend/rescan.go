package main

import (
	"context"
	"log"
	"time"
)

// deviceEnumerator is the seam the daemon enumerates serial devices through, so
// the startup scan AND the hot-plug rescan can be driven by a scripted fake in
// tests. The production Enumerator (enumerate.go) satisfies it directly.
type deviceEnumerator interface {
	Enumerate() []DiscoveredPort
}

// filterAllowed applies the allow/deny glob filter to an enumeration, dropping
// devices the operator excluded — the same gate buildBridges uses at startup, so
// the rescan never bridges a device the startup scan would have skipped.
func filterAllowed(cfg Config, devs []DiscoveredPort) []DiscoveredPort {
	out := make([]DiscoveredPort, 0, len(devs))
	for _, dev := range devs {
		if cfg.allowed(baseName(dev.DevPath), baseName(dev.ByID)) {
			out = append(out, dev)
		}
	}
	return out
}

// diffDevices computes the delta between a fresh (already allow/deny-filtered)
// enumeration and the currently-bridged set. A device is matched to a bridge by
// EITHER its stable id OR its resolved kernel path, so:
//   - added   = enumerated devices matched by neither key on any live bridge;
//   - removed = live bridges matched by neither key in the enumeration.
//
// Everything else — a device still present under the same id/path — is left out
// of both lists, so its bridge and any connected client are untouched. Pure, for
// unit testing.
func diffDevices(enumerated []DiscoveredPort, current []*Bridge) (added []DiscoveredPort, removed []*Bridge) {
	curByID := make(map[string]bool, len(current))
	curByDev := make(map[string]bool, len(current))
	for _, b := range current {
		curByID[b.dev.ID] = true
		if b.dev.DevPath != "" {
			curByDev[b.dev.DevPath] = true
		}
	}
	enumByID := make(map[string]bool, len(enumerated))
	enumByDev := make(map[string]bool, len(enumerated))
	for _, dev := range enumerated {
		enumByID[dev.ID] = true
		if dev.DevPath != "" {
			enumByDev[dev.DevPath] = true
		}
	}

	for _, dev := range enumerated {
		if curByID[dev.ID] || (dev.DevPath != "" && curByDev[dev.DevPath]) {
			continue // already bridged → untouched
		}
		added = append(added, dev)
	}
	for _, b := range current {
		if enumByID[b.dev.ID] || (b.dev.DevPath != "" && enumByDev[b.dev.DevPath]) {
			continue // still present → untouched
		}
		removed = append(removed, b)
	}
	return added, removed
}

// reconcile runs one hot-plug pass: re-enumerate, filter, diff against the live
// bridge set, then tear down vanished bridges and stand up newly-appeared ones,
// leaving unchanged devices (and their clients) completely alone. Removals run
// before adds so a freed TCP port can be reused within the same pass. warned
// tracks devices whose serial open has already failed, so a permanently
// unopenable device is logged ONCE, not on every tick. It is owned by the single
// rescan goroutine, so no locking is needed on it.
func (reg *Registry) reconcile(cfg Config, enum deviceEnumerator, open SerialOpener, line LineParams, warned map[string]bool) {
	enumerated := filterAllowed(cfg, enum.Enumerate())
	added, removed := diffDevices(enumerated, reg.snapshot())

	for _, b := range removed {
		if reg.remove(b.dev.ID) != nil {
			b.Close()
			delete(warned, b.dev.ID)
			log.Printf("bridge removed %s (%s): device gone (tcp :%d freed)", b.dev.ID, b.dev.DevPath, b.tcpPort)
		}
	}

	for _, dev := range added {
		// A concurrent path can't add here (single rescan goroutine), but guard
		// against a within-pass duplicate id/path just in case an enumeration ever
		// yields one.
		if reg.has(dev.ID, dev.DevPath) {
			continue
		}
		port := reg.nextFreePort(cfg.BaseTCPPort)
		b, err := newBridge(dev, port, cfg.BindAddr, line, open)
		if err != nil {
			if !warned[dev.ID] {
				log.Printf("bridge deferred %s (%s): %v (will retry next rescan)", dev.ID, dev.DevPath, err)
				warned[dev.ID] = true
			}
			continue // transient (busy/permission) → retry next tick, no port consumed
		}
		if !reg.add(b) {
			// Registry closed underneath us (shutdown) — add already Closed it.
			return
		}
		go b.run()
		delete(warned, dev.ID)
		log.Printf("bridge added %s [id via %s%s]: %s <-> tcp :%d (vid:pid %s:%s)",
			dev.ID, dev.IDSource, unstableTag(dev.IDStable), dev.DevPath, port, orNone(dev.USBVid), orNone(dev.USBPid))
	}
}

// rescanLoop polls for hot-plug/unplug every cfg.RescanInterval, reconciling the
// live bridge set each tick, and returns when ctx is cancelled (SIGTERM). Only
// started when the interval is > 0 (see startRescan).
func (reg *Registry) rescanLoop(ctx context.Context, cfg Config, enum deviceEnumerator, open SerialOpener, line LineParams) {
	ticker := time.NewTicker(cfg.RescanInterval.Duration())
	defer ticker.Stop()
	warned := make(map[string]bool)
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			reg.reconcile(cfg, enum, open, line, warned)
		}
	}
}

// startRescan launches the hot-plug poll loop unless it is disabled (interval 0,
// which preserves the original startup-only behaviour). Reports whether a loop
// was started, so run can log the mode and tests can assert the 0-disables gate
// without racing a goroutine.
func startRescan(ctx context.Context, reg *Registry, cfg Config, enum deviceEnumerator, open SerialOpener, line LineParams) bool {
	if cfg.RescanInterval.Duration() <= 0 {
		return false
	}
	go reg.rescanLoop(ctx, cfg, enum, open, line)
	return true
}
