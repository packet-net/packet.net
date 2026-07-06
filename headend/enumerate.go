package main

import (
	"log"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// ID source classes, in decreasing stability order. Surfaced in the inventory
// (IDSource) so PDN can warn on an unstable binding.
const (
	// idSourceByPath — /dev/serial/by-path basename. The PRIMARY id for every
	// device: it names the physical USB socket, so it is unique by construction
	// (two devices can't share a socket, so it can never collide the way a shared,
	// non-unique USB serial makes by-id collide — #574) and stable across reboot +
	// same-port replug. Moving a device to a DIFFERENT port changes this id (a
	// physical reconfiguration → an intentional re-adopt on PDN).
	idSourceByPath = "by-path"
	// idSourceDev — kernel /dev basename. LAST RESORT, only when a device has no
	// by-path link; NOT stable across reboot/replug.
	idSourceDev = "dev"
	// idSourceByID — /dev/serial/by-id basename. NO LONGER an id source: a fixed,
	// non-unique USB serial (e.g. the CP2102 CCDI dongles all report 0001) makes it
	// collide, and udev can flip the single by-id symlink to a replugged sibling
	// (#574, superseding the by-id-first chain from #569). The by-id string is still
	// captured in the inventory's ByID field as an informational device serial/model
	// hint; this constant is retained for that reference and for tests. Enumerate
	// never sets IDSource to this value.
	idSourceByID = "by-id"
)

// DiscoveredPort is one serial device the head-end can bridge, with a STABLE
// identity key. It mirrors the shape of PDN's C# NinoTncCandidatePort /
// TaitDiscoveredRadio discovery, generalised off the local /dev tree — the
// head-end does NO device identification (no GETVER, no CCDI MODEL); PDN reaches
// through the raw pipe with its own drivers to identify.
type DiscoveredPort struct {
	// ID is the device's stable-unique identity key, derived by a fallback chain
	// (see Enumerate): the /dev/serial/by-path basename — the physical USB socket,
	// unique by construction and stable across reboot/same-port replug — when one
	// exists; else — LAST RESORT — the kernel /dev basename, which is NOT stable
	// across reboot/replug. Guaranteed unique within one enumeration (see
	// dedupeIDs). The /dev/serial/by-id link is NOT used to derive the id — a
	// shared, non-unique USB serial makes it collide and flip between siblings
	// (#574) — but it is captured in ByID as an informational hint.
	ID string
	// DevPath is the resolved kernel device (/dev/ttyUSB0, /dev/ttyACM0).
	DevPath string
	// ByID is the full /dev/serial/by-id/... symlink, or "" when none.
	// INFORMATIONAL only (device serial/model hint for PDN's identify step, and the
	// by-id basename the allow/deny filter matches on) — it is NOT the id source,
	// see idSourceByID / #574.
	ByID string
	// ByPath is the full /dev/serial/by-path/... symlink, or "" when none.
	ByPath string
	// IDSource records which link the ID came from: idSourceByPath (normal) or
	// idSourceDev (no by-path link). Never idSourceByID. PDN reads it to warn on an
	// unstable binding.
	IDSource string
	// IDStable is a convenience flag derived from IDSource (and downgraded if a
	// device had to be disambiguated onto its unstable /dev name): true for
	// by-path, false for a /dev-basename fallback.
	IDStable bool
	// USBVid / USBPid are the 4-hex USB IDs read from sysfs (hints for PDN's
	// identify step), or "" when unavailable.
	USBVid string
	USBPid string
}

// Enumerator discovers serial devices. Its filesystem roots are injectable so
// the by-id/by-path/glob/sysfs logic is unit-testable against a fake /dev + /sys
// tree; production uses the real paths (see defaultEnumerator).
type Enumerator struct {
	ByIDDir     string // /dev/serial/by-id
	ByPathDir   string // /dev/serial/by-path
	DevDir      string // /dev
	SysClassTty string // /sys/class/tty
}

func defaultEnumerator() Enumerator {
	return Enumerator{
		ByIDDir:     "/dev/serial/by-id",
		ByPathDir:   "/dev/serial/by-path",
		DevDir:      "/dev",
		SysClassTty: "/sys/class/tty",
	}
}

// Enumerate returns every candidate serial device with a STABLE + UNIQUE
// identity key, keyed by resolved kernel path so a device reachable via multiple
// symlinks (the shared-serial CP2102 CCDI dongles do this) is bridged exactly
// once. The id is the physical USB topology, with a /dev last resort:
//
//  1. /dev/serial/by-path basename — the id for every device that has one. It
//     names the physical USB socket, so it is unique by construction (two devices
//     can't share a socket, so — unlike a shared USB serial's by-id link, #574 —
//     it can never collide or flip between siblings) and stable across reboot +
//     same-port replug. Moving a device to a DIFFERENT port changes its id (a
//     physical reconfiguration → an intentional re-adopt on PDN).
//  2. else — LAST RESORT — the raw /dev/ttyUSB* / /dev/ttyACM* basename, which is
//     NOT stable across reboot/replug (logged as unstable).
//
// The /dev/serial/by-id link is captured in ByID as an informational hint (device
// serial/model) but is NOT used to derive the id: a fixed, non-unique USB serial
// (the CP2102 CCDI dongles all report 0001) makes by-id collide, and udev can flip
// the single by-id symlink to a replugged sibling — the #574 failure this chain
// closes by design (superseding #569's by-id-first chain).
//
// A final dedupe pass guarantees no two devices ever share an ID. Results are
// ordered by ID for a stable inventory + deterministic TCP-port allocation.
func (e Enumerator) Enumerate() []DiscoveredPort {
	byDev := make(map[string]DiscoveredPort)

	// Collect the by-id link per device up front. by-id is NO LONGER an id source
	// (#574 — a fixed, non-unique USB serial makes it collide + flip between sibling
	// devices on replug, superseding #569's by-id-first chain); it is retained ONLY
	// as the informational ByID hint (device serial/model + the allow/deny match
	// key). First by-id link wins for a device (listLinks is sorted).
	byIDForDev := make(map[string]string)
	for _, link := range listLinks(e.ByIDDir) {
		if dev, ok := resolveSymlink(link); ok {
			if _, seen := byIDForDev[dev]; !seen {
				byIDForDev[dev] = link
			}
		}
	}

	// 1. Primary id: /dev/serial/by-path basename. by-path names the physical USB
	//    socket → unique by construction (two devices can't share a socket, so it
	//    can never collide) and stable across reboot + same-port replug. First
	//    by-path link wins for a device (sorted) so the id is deterministic when
	//    udev makes several by-path variants (e.g. usb- vs usbv2-).
	for _, link := range listLinks(e.ByPathDir) {
		dev, ok := resolveSymlink(link)
		if !ok {
			continue
		}
		if _, seen := byDev[dev]; seen {
			continue
		}
		byDev[dev] = DiscoveredPort{
			ID:       filepath.Base(link),
			DevPath:  dev,
			ByPath:   link,
			IDSource: idSourceByPath,
			IDStable: true,
		}
	}

	// devFallback records a device on its UNSTABLE kernel /dev basename — the last
	// resort when it has no by-path link (a minimal udev config lacking the standard
	// 60-serial.rules). Logged so the operator (and PDN) see the binding risk.
	devFallback := func(dev string) {
		if _, seen := byDev[dev]; seen {
			return
		}
		log.Printf("device %s has no /dev/serial/by-path link; using unstable /dev basename %q as its id — this binding may not survive a reboot/replug (install the standard 60-serial.rules udev rules, or pin the port physically)",
			dev, filepath.Base(dev))
		byDev[dev] = DiscoveredPort{
			ID:       filepath.Base(dev),
			DevPath:  dev,
			IDSource: idSourceDev,
			IDStable: false,
		}
	}

	// 2. Raw USB/ACM ttys with no by-path link → /dev fallback.
	for _, dev := range listDevTtys(e.DevDir) {
		devFallback(dev)
	}
	// 3. A device reachable ONLY via a by-id link (no by-path, and a /dev name
	//    outside the ttyUSB*/ttyACM* glob) still gets enumerated — on its unstable
	//    /dev basename — so the by-path chain never drops a device the old
	//    by-id-first chain would have seen. (Realistically empty: udev's
	//    60-serial.rules makes a by-path link for every USB-serial tty, all of which
	//    the glob above already covers.)
	for dev := range byIDForDev {
		devFallback(dev)
	}

	out := make([]DiscoveredPort, 0, len(byDev))
	for _, p := range byDev {
		p.ByID = byIDForDev[p.DevPath] // informational hint; never the id source
		p.USBVid, p.USBPid = readUsbIds(e.SysClassTty, filepath.Base(p.DevPath))
		out = append(out, p)
	}
	// Sort by (ID, DevPath) so the order — and hence which member of a colliding
	// group keeps a shorter key — is deterministic across runs.
	sort.Slice(out, func(i, j int) bool {
		if out[i].ID != out[j].ID {
			return out[i].ID < out[j].ID
		}
		return out[i].DevPath < out[j].DevPath
	})
	dedupeIDs(out)
	return out
}

// dedupeIDs guarantees no two ports in one enumeration share an ID. A by-path
// basename is unique per physical socket, so a residual collision can only be a
// cross-source basename clash (e.g. a device's by-path basename equal to
// another's /dev basename). Every member of a colliding group gets a unique
// discriminator appended, tried in decreasing stability order (see
// discriminators); a device forced onto its unstable /dev name to disambiguate
// is flagged IDStable=false. It mutates ports in place (IDs already sorted).
func dedupeIDs(ports []DiscoveredPort) {
	counts := make(map[string]int, len(ports))
	for i := range ports {
		counts[ports[i].ID]++
	}
	// Seed the used-set with the IDs that are already unique so a discriminator
	// can never re-collide onto one of them.
	used := make(map[string]bool, len(ports))
	for i := range ports {
		if counts[ports[i].ID] == 1 {
			used[ports[i].ID] = true
		}
	}
	for i := range ports {
		if counts[ports[i].ID] == 1 {
			continue // already unique
		}
		for _, d := range discriminators(ports[i], i) {
			cand := ports[i].ID + "_" + d.token
			if used[cand] {
				continue
			}
			ports[i].ID = cand
			used[cand] = true
			if !d.stable {
				ports[i].IDStable = false
			}
			break
		}
	}
}

// discriminator is one candidate suffix for disambiguating a collided ID, with
// whether appending it keeps the ID stable.
type discriminator struct {
	token  string
	stable bool
}

// discriminators lists the disambiguators for a port in decreasing stability
// order: the by-path basename (stable, unique per physical port) when the device
// has one, then the /dev basename (unique per device but UNSTABLE), then the
// enumeration index (a guaranteed-unique last resort). The /dev basename is
// always present, so a unique token is always found before the index.
func discriminators(p DiscoveredPort, idx int) []discriminator {
	var out []discriminator
	if p.ByPath != "" {
		out = append(out, discriminator{token: filepath.Base(p.ByPath), stable: true})
	}
	if p.DevPath != "" {
		out = append(out, discriminator{token: filepath.Base(p.DevPath), stable: false})
	}
	out = append(out, discriminator{token: strconv.Itoa(idx), stable: false})
	return out
}

// listLinks returns the symlink paths under dir (by-id or by-path), sorted, or
// nil if the directory is absent (minimal udev configs skip serial/by-*; the
// remaining chain links cover that).
func listLinks(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []string
	for _, ent := range entries {
		out = append(out, filepath.Join(dir, ent.Name()))
	}
	sort.Strings(out)
	return out
}

// listDevTtys returns /dev/ttyUSB* and /dev/ttyACM* device paths, sorted.
func listDevTtys(devDir string) []string {
	entries, err := os.ReadDir(devDir)
	if err != nil {
		return nil
	}
	var out []string
	for _, ent := range entries {
		name := ent.Name()
		if strings.HasPrefix(name, "ttyUSB") || strings.HasPrefix(name, "ttyACM") {
			out = append(out, filepath.Join(devDir, name))
		}
	}
	sort.Strings(out)
	return out
}

// resolveSymlink follows link to its final absolute target. A relative target
// (the udev convention, e.g. ../../ttyUSB0) is resolved against the link's
// directory. Returns false on any error.
func resolveSymlink(link string) (string, bool) {
	target, err := os.Readlink(link)
	if err != nil {
		return "", false
	}
	if !filepath.IsAbs(target) {
		target = filepath.Join(filepath.Dir(link), target)
	}
	return filepath.Clean(target), true
}

// readUsbIds walks up the sysfs device tree from /sys/class/tty/<dev>/device
// until it finds idVendor + idProduct (the USB device node a few levels up from
// the tty), returning them as lowercase 4-hex strings. Best-effort: any misstep
// yields ("", "").
func readUsbIds(sysClassTty, devBase string) (vid, pid string) {
	if sysClassTty == "" || devBase == "" {
		return "", ""
	}
	start := filepath.Join(sysClassTty, devBase, "device")
	dir, err := filepath.EvalSymlinks(start)
	if err != nil {
		// Fall back to the unresolved path; the fake sysfs trees tests build
		// use plain directories rather than symlinks.
		dir = start
	}
	// Bound the walk so a pathological tree can't loop forever.
	for i := 0; i < 12 && dir != "" && dir != string(filepath.Separator) && dir != "."; i++ {
		v := strings.TrimSpace(readFile(filepath.Join(dir, "idVendor")))
		p := strings.TrimSpace(readFile(filepath.Join(dir, "idProduct")))
		if v != "" && p != "" {
			return strings.ToLower(v), strings.ToLower(p)
		}
		dir = filepath.Dir(dir)
	}
	return "", ""
}

func readFile(p string) string {
	b, err := os.ReadFile(p)
	if err != nil {
		return ""
	}
	return string(b)
}

// baseName is filepath.Base but maps "" to "" (filepath.Base("") is ".") so an
// absent by-id path stays empty for the glob filter and logs.
func baseName(p string) string {
	if p == "" {
		return ""
	}
	return filepath.Base(p)
}

// orNone renders an empty hint as "-" for logs.
func orNone(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// unstableTag renders ", UNSTABLE" for an unstable id (false), "" otherwise, for
// the bridge log line.
func unstableTag(stable bool) string {
	if stable {
		return ""
	}
	return ", UNSTABLE"
}

// matchesAny reports whether any glob in pats matches any of the candidate
// strings (path.Match semantics). An empty pattern list matches nothing.
func matchesAny(pats []string, candidates ...string) bool {
	for _, pat := range pats {
		for _, c := range candidates {
			if c == "" {
				continue
			}
			if ok, err := path.Match(pat, c); err == nil && ok {
				return true
			}
		}
	}
	return false
}
