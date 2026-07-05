package main

import (
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
)

// DiscoveredPort is one serial device the head-end can bridge, with a STABLE
// identity key. It mirrors the shape of PDN's C# NinoTncCandidatePort /
// TaitDiscoveredRadio discovery, generalised off the local /dev tree — the
// head-end does NO device identification (no GETVER, no CCDI MODEL); PDN reaches
// through the raw pipe with its own drivers to identify.
type DiscoveredPort struct {
	// ID is the device's stable identity key: the /dev/serial/by-id basename
	// when one exists (udev-stable across reboots and re-plug ordering),
	// otherwise the kernel /dev basename as a best-effort fallback.
	ID string
	// DevPath is the resolved kernel device (/dev/ttyUSB0, /dev/ttyACM0).
	DevPath string
	// ByID is the full /dev/serial/by-id/... symlink, or "" when none.
	ByID string
	// USBVid / USBPid are the 4-hex USB IDs read from sysfs (hints for PDN's
	// identify step), or "" when unavailable.
	USBVid string
	USBPid string
}

// Enumerator discovers serial devices. Its filesystem roots are injectable so
// the by-id/glob/sysfs logic is unit-testable against a fake /dev + /sys tree;
// production uses the real paths (see defaultEnumerator).
type Enumerator struct {
	ByIDDir     string // /dev/serial/by-id
	DevDir      string // /dev
	SysClassTty string // /sys/class/tty
}

func defaultEnumerator() Enumerator {
	return Enumerator{
		ByIDDir:     "/dev/serial/by-id",
		DevDir:      "/dev",
		SysClassTty: "/sys/class/tty",
	}
}

// Enumerate returns every candidate serial device, keyed by resolved kernel
// path so a device reachable via multiple by-id symlinks (the shared-serial
// CP2102 CCDI dongles do this) is bridged exactly once. by-id entries are
// preferred (their basename becomes the stable ID); raw /dev/ttyUSB* and
// /dev/ttyACM* not covered by a by-id link are added as a fallback. Results are
// ordered by ID for a stable inventory + deterministic TCP-port allocation.
func (e Enumerator) Enumerate() []DiscoveredPort {
	byDev := make(map[string]DiscoveredPort)

	// Preferred: /dev/serial/by-id symlinks → real device.
	for _, link := range listByID(e.ByIDDir) {
		dev, ok := resolveSymlink(link)
		if !ok {
			continue
		}
		// First by-id link wins for a device (listByID is sorted), so the ID is
		// deterministic even under the shared-serial ambiguity.
		if _, seen := byDev[dev]; seen {
			continue
		}
		byDev[dev] = DiscoveredPort{
			ID:      filepath.Base(link),
			DevPath: dev,
			ByID:    link,
		}
	}

	// Fallback: raw USB/ACM ttys not already covered by a by-id link.
	for _, dev := range listDevTtys(e.DevDir) {
		if _, seen := byDev[dev]; seen {
			continue
		}
		byDev[dev] = DiscoveredPort{
			ID:      filepath.Base(dev),
			DevPath: dev,
		}
	}

	out := make([]DiscoveredPort, 0, len(byDev))
	for _, p := range byDev {
		p.USBVid, p.USBPid = readUsbIds(e.SysClassTty, filepath.Base(p.DevPath))
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

// listByID returns the by-id symlink paths, sorted, or nil if the directory is
// absent (minimal udev configs skip it — the glob fallback covers that).
func listByID(byIDDir string) []string {
	entries, err := os.ReadDir(byIDDir)
	if err != nil {
		return nil
	}
	var out []string
	for _, ent := range entries {
		out = append(out, filepath.Join(byIDDir, ent.Name()))
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
