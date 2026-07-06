package main

import (
	"context"
	"fmt"
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
// enumeration and the currently-bridged set. A device and a bridge are the SAME
// physical device when their resolved kernel path matches, OR when their id
// matches and neither pins a *different* kernel path (so a device with no
// resolvable path, or one whose id shifted on a late udev by-path upgrade, still
// reads as the same device). But an id match where BOTH sides pin a DIFFERENT
// kernel path is a cross-pass id COLLISION, NOT a match — the returned device is a
// genuinely new device and must be added (reconcile disambiguates its id), never
// swallowed as "already bridged" (the #574 shared-serial by-id flip). So:
//   - added   = enumerated devices that are not the same device as any live bridge;
//   - removed = live bridges that are not the same device as any enumeration entry.
//
// Everything else is left out of both lists, so its bridge and any connected
// client are untouched. Pure, for unit testing.
func diffDevices(enumerated []DiscoveredPort, current []*Bridge) (added []DiscoveredPort, removed []*Bridge) {
	curDev := make(map[string]bool, len(current))      // resolved kernel path → bridged
	curIDPath := make(map[string]string, len(current)) // stable id → that bridge's kernel path
	for _, b := range current {
		if b.dev.DevPath != "" {
			curDev[b.dev.DevPath] = true
		}
		curIDPath[b.dev.ID] = b.dev.DevPath
	}
	enumDev := make(map[string]bool, len(enumerated))
	enumIDPath := make(map[string]string, len(enumerated))
	for _, dev := range enumerated {
		if dev.DevPath != "" {
			enumDev[dev.DevPath] = true
		}
		enumIDPath[dev.ID] = dev.DevPath
	}

	// samePath reports whether an id match is a genuine same-device match rather
	// than a cross-pass id collision: it is unless BOTH sides pin a different,
	// non-empty kernel path.
	samePath := func(a, b string) bool { return a == "" || b == "" || a == b }

	for _, dev := range enumerated {
		if dev.DevPath != "" && curDev[dev.DevPath] {
			continue // same physical device (kernel path) → untouched
		}
		if bridgePath, ok := curIDPath[dev.ID]; ok && samePath(dev.DevPath, bridgePath) {
			continue // same device by id, no conflicting path → untouched
		}
		added = append(added, dev)
	}
	for _, b := range current {
		if b.dev.DevPath != "" && enumDev[b.dev.DevPath] {
			continue // same kernel path present → still bridged
		}
		if enumPath, ok := enumIDPath[b.dev.ID]; ok && samePath(b.dev.DevPath, enumPath) {
			continue // same device by id, no conflicting path → still bridged
		}
		removed = append(removed, b)
	}
	return added, removed
}

// disambiguateAgainst returns dev with an id guaranteed absent from used. If
// dev.ID is already free it is returned unchanged; otherwise a discriminator
// (by-path basename → /dev basename → an incrementing index) is appended,
// mirroring enumerate's within-pass dedupeIDs, downgrading IDStable when the
// chosen discriminator is itself unstable. Cross-pass belt-and-suspenders for an
// id collision the by-path id chain should already make impossible (#574).
func disambiguateAgainst(dev DiscoveredPort, used map[string]bool) DiscoveredPort {
	if !used[dev.ID] {
		return dev
	}
	base := dev.ID
	for _, d := range discriminators(dev, 0) {
		cand := base + "_" + d.token
		if !used[cand] {
			dev.ID = cand
			if !d.stable {
				dev.IDStable = false
			}
			return dev
		}
	}
	// Guaranteed-terminating fallback if every named discriminator was taken.
	for i := 0; ; i++ {
		cand := fmt.Sprintf("%s_i%d", base, i)
		if !used[cand] {
			dev.ID = cand
			dev.IDStable = false
			return dev
		}
	}
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

	// Cross-pass id-collision guard: a newly-added device's id must never equal a
	// live bridge's id (nor another device added earlier in this pass). The by-path
	// id chain makes a collision impossible by construction, so this is
	// belt-and-suspenders — but if some residual /dev-fallback clash ever slips
	// through, disambiguate the new device (append a by-path/dev discriminator, like
	// the within-pass dedupeIDs) rather than dropping it or mis-binding it onto the
	// sibling's bridge (the #574 failure mode). Seeded AFTER removals so a just-freed
	// id can be reused, and grown as we add within this pass.
	used := make(map[string]bool)
	for _, b := range reg.snapshot() {
		used[b.dev.ID] = true
	}

	for _, dev := range added {
		dev = disambiguateAgainst(dev, used)
		// A concurrent path can't add here (single rescan goroutine), but guard
		// against a within-pass duplicate id/path just in case an enumeration ever
		// yields one. After disambiguation dev.ID can't false-match a live bridge,
		// so this only catches a genuine duplicate kernel path.
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
		used[dev.ID] = true
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
