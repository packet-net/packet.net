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
	idSourceByID   = "by-id"   // /dev/serial/by-id basename — udev-stable, unique per serial.
	idSourceByPath = "by-path" // /dev/serial/by-path basename — physical USB topology; unique even when serials collide, stable while the device stays in the same port.
	idSourceDev    = "dev"     // kernel /dev basename — LAST RESORT, NOT stable across reboot/replug.
)

// DiscoveredPort is one serial device the head-end can bridge, with a STABLE
// identity key. It mirrors the shape of PDN's C# NinoTncCandidatePort /
// TaitDiscoveredRadio discovery, generalised off the local /dev tree — the
// head-end does NO device identification (no GETVER, no CCDI MODEL); PDN reaches
// through the raw pipe with its own drivers to identify.
type DiscoveredPort struct {
	// ID is the device's stable-unique identity key, derived by a fallback
	// chain (see Enumerate): the /dev/serial/by-id basename when one exists;
	// else the /dev/serial/by-path basename (physical USB topology — unique even
	// when two devices share a USB serial, which udev can only by-id ONE of);
	// else — last resort — the kernel /dev basename, which is NOT stable across
	// reboot/replug. Guaranteed unique within one enumeration (see dedupeIDs).
	ID string
	// DevPath is the resolved kernel device (/dev/ttyUSB0, /dev/ttyACM0).
	DevPath string
	// ByID is the full /dev/serial/by-id/... symlink, or "" when none.
	ByID string
	// ByPath is the full /dev/serial/by-path/... symlink, or "" when none.
	ByPath string
	// IDSource records which link in the derivation chain the ID came from:
	// idSourceByID / idSourceByPath / idSourceDev. PDN reads it to warn on an
	// unstable binding.
	IDSource string
	// IDStable is a convenience flag derived from IDSource (and downgraded if a
	// device had to be disambiguated onto its unstable /dev name): true for
	// by-id/by-path, false for a /dev-basename fallback.
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
// once. The ID is derived by a fallback chain, each link more stable than the
// next:
//
//  1. /dev/serial/by-id basename — udev-stable, unique per serial (as today);
//  2. else /dev/serial/by-path basename — the physical USB topology, unique even
//     when two devices share a USB serial (udev can only create a by-id link for
//     ONE of a colliding pair, so the other MUST fall to by-path, not to the
//     unstable /dev name), and stable while the device stays in the same port;
//  3. else — LAST RESORT — the raw /dev/ttyUSB* / /dev/ttyACM* basename, which is
//     NOT stable across reboot/replug (logged as unstable).
//
// A final dedupe pass guarantees no two devices ever share an ID. Results are
// ordered by ID for a stable inventory + deterministic TCP-port allocation.
func (e Enumerator) Enumerate() []DiscoveredPort {
	byDev := make(map[string]DiscoveredPort)

	// 1. Preferred: /dev/serial/by-id symlinks → real device.
	for _, link := range listLinks(e.ByIDDir) {
		dev, ok := resolveSymlink(link)
		if !ok {
			continue
		}
		// First by-id link wins for a device (listLinks is sorted), so the ID is
		// deterministic even under the shared-serial ambiguity.
		if _, seen := byDev[dev]; seen {
			continue
		}
		byDev[dev] = DiscoveredPort{
			ID:       filepath.Base(link),
			DevPath:  dev,
			ByID:     link,
			IDSource: idSourceByID,
			IDStable: true,
		}
	}

	// 2. /dev/serial/by-path symlinks → real device. For a device already keyed
	//    by by-id we only record the by-path link (informational); for one not
	//    yet covered — the shared-serial device udev couldn't by-id — the by-path
	//    basename becomes its stable, unique ID.
	for _, link := range listLinks(e.ByPathDir) {
		dev, ok := resolveSymlink(link)
		if !ok {
			continue
		}
		if p, seen := byDev[dev]; seen {
			if p.ByPath == "" {
				p.ByPath = link
				byDev[dev] = p
			}
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

	// 3. Last resort: raw USB/ACM ttys not covered by any by-id/by-path link. The
	//    kernel /dev name reorders across reboot/replug, so this ID is UNSTABLE —
	//    log it so the operator (and PDN) can see the binding risk.
	for _, dev := range listDevTtys(e.DevDir) {
		if _, seen := byDev[dev]; seen {
			continue
		}
		log.Printf("device %s has no /dev/serial/by-id or by-path link; using unstable /dev basename %q as its id — this binding may not survive a reboot/replug (fix udev, or pin the port physically)",
			dev, filepath.Base(dev))
		byDev[dev] = DiscoveredPort{
			ID:       filepath.Base(dev),
			DevPath:  dev,
			IDSource: idSourceDev,
			IDStable: false,
		}
	}

	out := make([]DiscoveredPort, 0, len(byDev))
	for _, p := range byDev {
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

// dedupeIDs guarantees no two ports in one enumeration share an ID. by-id and
// by-path names are each unique per device, so a residual collision can only be
// a cross-source basename clash (e.g. a device's by-path basename equal to
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
