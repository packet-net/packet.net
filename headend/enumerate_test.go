package main

import (
	"os"
	"path/filepath"
	"testing"
)

// fakeDevTree builds a temp filesystem mirroring the real layout so the relative
// by-id / by-path symlinks (../../ttyXXX) resolve correctly:
//
//	<root>/dev/ttyUSB0, ttyACM0, ttyUSB1        (device nodes, as plain files)
//	<root>/dev/serial/by-id/<link> -> ../../ttyXXX
//	<root>/dev/serial/by-path/<link> -> ../../ttyXXX
//	<root>/sys/class/tty/<dev>/device/idVendor  (+ idProduct)
//
// ttyUSB0 + ttyACM0 have a by-path link (→ by-path id, the primary); ttyUSB1 has
// neither by-path nor by-id (→ the unstable /dev fallback).
func fakeDevTree(t *testing.T) Enumerator {
	t.Helper()
	root := t.TempDir()
	devDir := filepath.Join(root, "dev")
	byIDDir := filepath.Join(devDir, "serial", "by-id")
	byPathDir := filepath.Join(devDir, "serial", "by-path")
	sysTty := filepath.Join(root, "sys", "class", "tty")
	for _, d := range []string{devDir, byIDDir, byPathDir, sysTty} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}

	// Device nodes.
	for _, dev := range []string{"ttyUSB0", "ttyACM0", "ttyUSB1"} {
		if err := os.WriteFile(filepath.Join(devDir, dev), nil, 0o644); err != nil {
			t.Fatal(err)
		}
	}

	mklinkIn := func(dir, name, target string) {
		if err := os.Symlink(target, filepath.Join(dir, name)); err != nil {
			t.Fatal(err)
		}
	}
	// by-id symlinks (relative, udev-style). ttyUSB0 is reachable via TWO links
	// (the shared-serial CP2102 ambiguity) → the informational ByID dedups to the
	// first sorted; by-id is NOT the id source.
	mklinkIn(byIDDir, "usb-FTDI_TaitRadio_A-if00-port0", "../../ttyUSB0")
	mklinkIn(byIDDir, "usb-FTDI_TaitRadio_B-if00-port0", "../../ttyUSB0") // shared serial → same dev
	mklinkIn(byIDDir, "usb-NinoTNC_TARPN-if00", "../../ttyACM0")
	// by-path symlinks → the PRIMARY id (physical socket). ttyUSB1 has none → it
	// exercises the /dev fallback.
	mklinkIn(byPathDir, "pci-0000:00:14.0-usb-0:1:1.0-port0", "../../ttyUSB0")
	mklinkIn(byPathDir, "pci-0000:00:14.0-usb-0:3:1.0", "../../ttyACM0")

	// sysfs VID:PID for ttyUSB0 (found at level 0 of the walk).
	usb0 := filepath.Join(sysTty, "ttyUSB0", "device")
	if err := os.MkdirAll(usb0, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(usb0, "idVendor"), []byte("0403\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(usb0, "idProduct"), []byte("6001\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	// ttyACM0: idVendor/idProduct one level up from device/ (the walk climbs).
	acm := filepath.Join(sysTty, "ttyACM0", "device", "subsys")
	if err := os.MkdirAll(acm, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sysTty, "ttyACM0", "device", "idVendor"), []byte("04d8"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sysTty, "ttyACM0", "device", "idProduct"), []byte("000a"), 0o644); err != nil {
		t.Fatal(err)
	}

	return Enumerator{ByIDDir: byIDDir, ByPathDir: byPathDir, DevDir: devDir, SysClassTty: sysTty}
}

func TestEnumerate_ByPathPrimaryByIdInformational(t *testing.T) {
	e := fakeDevTree(t)
	ports := e.Enumerate()

	// 3 device nodes but ttyUSB0 has two by-id links → 3 unique devices.
	if len(ports) != 3 {
		t.Fatalf("got %d ports, want 3: %+v", len(ports), ports)
	}

	byDev := map[string]DiscoveredPort{}
	for _, p := range ports {
		byDev[p.DevPath] = p
	}

	usb0 := byDev[filepath.Join(e.DevDir, "ttyUSB0")]
	if usb0.ID != "pci-0000:00:14.0-usb-0:1:1.0-port0" {
		t.Errorf("ttyUSB0 ID = %q, want its by-path basename (the primary id)", usb0.ID)
	}
	if usb0.IDSource != idSourceByPath || !usb0.IDStable {
		t.Errorf("ttyUSB0 = {source %q, stable %v}, want {by-path, true}", usb0.IDSource, usb0.IDStable)
	}
	// by-id is retained ONLY as the informational hint, deduped to the first sorted.
	if filepath.Base(usb0.ByID) != "usb-FTDI_TaitRadio_A-if00-port0" {
		t.Errorf("ttyUSB0 ByID hint = %q, want the first-sorted by-id (dedup determinism)", usb0.ByID)
	}
	if usb0.USBVid != "0403" || usb0.USBPid != "6001" {
		t.Errorf("ttyUSB0 vid:pid = %s:%s, want 0403:6001", usb0.USBVid, usb0.USBPid)
	}

	acm := byDev[filepath.Join(e.DevDir, "ttyACM0")]
	if acm.ID != "pci-0000:00:14.0-usb-0:3:1.0" {
		t.Errorf("ttyACM0 ID = %q, want its by-path basename", acm.ID)
	}
	if acm.IDSource != idSourceByPath {
		t.Errorf("ttyACM0 IDSource = %q, want by-path", acm.IDSource)
	}
	if filepath.Base(acm.ByID) != "usb-NinoTNC_TARPN-if00" {
		t.Errorf("ttyACM0 ByID hint = %q, want its by-id basename", acm.ByID)
	}
	if acm.USBVid != "04d8" || acm.USBPid != "000a" {
		t.Errorf("ttyACM0 vid:pid = %s:%s, want 04d8:000a (found up the sysfs walk)", acm.USBVid, acm.USBPid)
	}

	usb1 := byDev[filepath.Join(e.DevDir, "ttyUSB1")]
	if usb1.ID != "ttyUSB1" {
		t.Errorf("ttyUSB1 ID = %q, want the dev basename (no by-path → fallback)", usb1.ID)
	}
	if usb1.IDSource != idSourceDev || usb1.IDStable {
		t.Errorf("ttyUSB1 = {source %q, stable %v}, want {dev, false}", usb1.IDSource, usb1.IDStable)
	}
	if usb1.ByID != "" {
		t.Errorf("ttyUSB1 ByID = %q, want empty (no by-id link)", usb1.ByID)
	}
}

func TestEnumerate_SortedByID(t *testing.T) {
	e := fakeDevTree(t)
	ports := e.Enumerate()
	for i := 1; i < len(ports); i++ {
		if ports[i-1].ID > ports[i].ID {
			t.Errorf("ports not sorted by ID: %q before %q", ports[i-1].ID, ports[i].ID)
		}
	}
}

func TestEnumerate_NoByIDDirFallsBackToGlob(t *testing.T) {
	root := t.TempDir()
	devDir := filepath.Join(root, "dev")
	if err := os.MkdirAll(devDir, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, dev := range []string{"ttyACM0", "ttyUSB0", "unrelated0"} {
		if err := os.WriteFile(filepath.Join(devDir, dev), nil, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	e := Enumerator{ByIDDir: filepath.Join(devDir, "serial", "by-id"), DevDir: devDir, SysClassTty: filepath.Join(root, "sys")}
	ports := e.Enumerate()
	if len(ports) != 2 {
		t.Fatalf("got %d ports, want 2 (ttyACM0, ttyUSB0; 'unrelated0' ignored): %+v", len(ports), ports)
	}
	if ports[0].ID != "ttyACM0" || ports[1].ID != "ttyUSB0" {
		t.Errorf("fallback IDs = %q,%q, want ttyACM0,ttyUSB0", ports[0].ID, ports[1].ID)
	}
}

// devSpec is one symlink/device row for buildTree. Several rows may name the
// same dev (e.g. two by-id links to one device); byID/byPath are optional ("").
type devSpec struct {
	dev    string // /dev basename, e.g. "ttyUSB0"
	byID   string // /dev/serial/by-id link name, or "" for none
	byPath string // /dev/serial/by-path link name, or "" for none
}

// buildTree materialises a fake /dev tree matching the real layout so the
// relative "../../ttyXXX" by-id AND by-path symlinks resolve. No sysfs is built
// (VID/PID come back empty), keeping these tests focused on id derivation.
func buildTree(t *testing.T, specs []devSpec) Enumerator {
	t.Helper()
	root := t.TempDir()
	devDir := filepath.Join(root, "dev")
	byIDDir := filepath.Join(devDir, "serial", "by-id")
	byPathDir := filepath.Join(devDir, "serial", "by-path")
	sysTty := filepath.Join(root, "sys", "class", "tty")
	for _, d := range []string{devDir, byIDDir, byPathDir, sysTty} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	made := map[string]bool{}
	for _, s := range specs {
		if !made[s.dev] {
			if err := os.WriteFile(filepath.Join(devDir, s.dev), nil, 0o644); err != nil {
				t.Fatal(err)
			}
			made[s.dev] = true
		}
		if s.byID != "" {
			if err := os.Symlink("../../"+s.dev, filepath.Join(byIDDir, s.byID)); err != nil {
				t.Fatal(err)
			}
		}
		if s.byPath != "" {
			if err := os.Symlink("../../"+s.dev, filepath.Join(byPathDir, s.byPath)); err != nil {
				t.Fatal(err)
			}
		}
	}
	return Enumerator{ByIDDir: byIDDir, ByPathDir: byPathDir, DevDir: devDir, SysClassTty: sysTty}
}

func portByDev(ports []DiscoveredPort, dev string) (DiscoveredPort, bool) {
	for _, p := range ports {
		if filepath.Base(p.DevPath) == dev {
			return p, true
		}
	}
	return DiscoveredPort{}, false
}

// The headline case: two CP2102 dongles sharing USB serial "0001", neither with
// a by-id link → BOTH must get a distinct STABLE id from by-path, and NEITHER
// may fall back to its (unstable, reorderable) /dev basename.
func TestEnumerate_SharedSerialBothViaByPath(t *testing.T) {
	e := buildTree(t, []devSpec{
		{dev: "ttyUSB0", byPath: "pci-0000:00:14.0-usb-0:1:1.0-port0"},
		{dev: "ttyUSB1", byPath: "pci-0000:00:14.0-usb-0:2:1.0-port0"},
	})
	ports := e.Enumerate()
	if len(ports) != 2 {
		t.Fatalf("got %d ports, want 2: %+v", len(ports), ports)
	}
	for _, dev := range []string{"ttyUSB0", "ttyUSB1"} {
		p, ok := portByDev(ports, dev)
		if !ok {
			t.Fatalf("%s missing from inventory: %+v", dev, ports)
		}
		if p.IDSource != idSourceByPath {
			t.Errorf("%s IDSource = %q, want %q (shared serial → by-path)", dev, p.IDSource, idSourceByPath)
		}
		if !p.IDStable {
			t.Errorf("%s IDStable = false, want true (by-path is stable)", dev)
		}
		if p.ID == dev {
			t.Errorf("%s id fell back to the unstable /dev basename %q", dev, p.ID)
		}
		if p.ID != filepath.Base(p.ByPath) {
			t.Errorf("%s id = %q, want its by-path basename %q", dev, p.ID, filepath.Base(p.ByPath))
		}
	}
	if ports[0].ID == ports[1].ID {
		t.Fatalf("shared-serial devices collided on id %q", ports[0].ID)
	}
}

// The real bench mix: one dongle holds the (single) by-id link udev could create
// for the colliding serial, the other doesn't — but BOTH now derive their id from
// by-path (the physical socket), so the shared/flippable by-id is never the id. The
// holder keeps its by-id string ONLY as an informational hint.
func TestEnumerate_SharedSerialBothByPathByIdHintOnly(t *testing.T) {
	const cp2102 = "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_0001-if00-port0"
	e := buildTree(t, []devSpec{
		// ttyUSB0: no by-id (udev could only make one for serial 0001), has by-path.
		{dev: "ttyUSB0", byPath: "pci-0000:00:14.0-usb-0:1:1.0-port0"},
		// ttyUSB1: holds the single by-id link + has its own by-path.
		{dev: "ttyUSB1", byID: cp2102, byPath: "pci-0000:00:14.0-usb-0:2:1.0-port0"},
	})
	ports := e.Enumerate()
	if len(ports) != 2 {
		t.Fatalf("got %d ports, want 2: %+v", len(ports), ports)
	}
	for _, dev := range []string{"ttyUSB0", "ttyUSB1"} {
		p, ok := portByDev(ports, dev)
		if !ok {
			t.Fatalf("%s missing from inventory: %+v", dev, ports)
		}
		// BOTH by-path — the shared by-id is never an id source.
		if p.IDSource != idSourceByPath || !p.IDStable {
			t.Errorf("%s = {source %q, stable %v}, want {by-path, true}", dev, p.IDSource, p.IDStable)
		}
		if p.IDSource == idSourceByID {
			t.Errorf("%s used the shared by-id as its id source", dev)
		}
		if p.ID != filepath.Base(p.ByPath) {
			t.Errorf("%s id = %q, want its by-path basename %q", dev, p.ID, filepath.Base(p.ByPath))
		}
	}
	// The by-id holder keeps its by-id string as an informational hint only.
	usb1, _ := portByDev(ports, "ttyUSB1")
	if filepath.Base(usb1.ByID) != cp2102 {
		t.Errorf("ttyUSB1 ByID hint = %q, want the retained by-id string", usb1.ByID)
	}
	if ports[0].ID == ports[1].ID {
		t.Fatalf("shared-serial devices collided on id %q", ports[0].ID)
	}
}

// The #574 flip scenario: two dongles share USB serial 0001; udev's single by-id
// symlink sits on ttyUSB0 in one enumeration and FLIPS to ttyUSB1 in the next (a
// hot-replug). Because the id is the by-path (physical socket) and never the
// by-id, each device's id is identical across both enumerations, distinct, and
// never collides — the flip is irrelevant.
func TestEnumerate_SharedSerialByIdFlipIrrelevant(t *testing.T) {
	const (
		pUSB0  = "pci-0000:00:14.0-usb-0:1:1.0-port0"
		pUSB1  = "pci-0000:00:14.0-usb-0:2:1.0-port0"
		cp2102 = "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_0001-if00-port0"
	)
	// Enumeration 1: the shared by-id symlink sits on ttyUSB0.
	e1 := buildTree(t, []devSpec{
		{dev: "ttyUSB0", byID: cp2102, byPath: pUSB0},
		{dev: "ttyUSB1", byPath: pUSB1},
	})
	// Enumeration 2 (post hot-replug): udev FLIPPED the same by-id symlink to ttyUSB1.
	e2 := buildTree(t, []devSpec{
		{dev: "ttyUSB0", byPath: pUSB0},
		{dev: "ttyUSB1", byID: cp2102, byPath: pUSB1},
	})
	p1, p2 := e1.Enumerate(), e2.Enumerate()

	for _, tc := range []struct{ dev, wantID string }{
		{"ttyUSB0", pUSB0},
		{"ttyUSB1", pUSB1},
	} {
		a, _ := portByDev(p1, tc.dev)
		b, _ := portByDev(p2, tc.dev)
		if a.ID != tc.wantID || b.ID != tc.wantID {
			t.Errorf("%s id flipped with the by-id symlink: enum1=%q enum2=%q, want stable %q", tc.dev, a.ID, b.ID, tc.wantID)
		}
		if a.IDSource != idSourceByPath || b.IDSource != idSourceByPath {
			t.Errorf("%s not by-path across both enums: %q / %q", tc.dev, a.IDSource, b.IDSource)
		}
	}
	if p1[0].ID == p1[1].ID || p2[0].ID == p2[1].ID {
		t.Fatal("shared-serial devices collided on id despite the by-path chain")
	}
}

// A unique serial: by-path is the primary id even when a by-id link exists; the
// by-id string is retained only as the informational ByID hint.
func TestEnumerate_ByPathPrimaryOverByID(t *testing.T) {
	e := buildTree(t, []devSpec{
		{dev: "ttyACM0", byID: "usb-NinoTNC_TARPN-if00", byPath: "pci-0000:00:14.0-usb-0:3:1.0"},
	})
	ports := e.Enumerate()
	if len(ports) != 1 {
		t.Fatalf("got %d ports, want 1: %+v", len(ports), ports)
	}
	p := ports[0]
	if p.ID != "pci-0000:00:14.0-usb-0:3:1.0" {
		t.Errorf("id = %q, want the by-path basename (the primary id)", p.ID)
	}
	if p.IDSource != idSourceByPath || !p.IDStable {
		t.Errorf("{source %q, stable %v}, want {by-path, true}", p.IDSource, p.IDStable)
	}
	if filepath.Base(p.ByID) != "usb-NinoTNC_TARPN-if00" {
		t.Errorf("by-id string should still be recorded as an informational hint, got %q", p.ByID)
	}
}

// A by-id link but NO by-path (a minimal udev config lacking 60-serial.rules): the
// id falls to the unstable /dev basename, and the by-id string is still recorded as
// an informational hint (by-id is never promoted to the id).
func TestEnumerate_ByPathAbsentByIdHintOnDevFallback(t *testing.T) {
	const cp2102 = "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_0001-if00-port0"
	e := buildTree(t, []devSpec{{dev: "ttyUSB0", byID: cp2102}})
	ports := e.Enumerate()
	if len(ports) != 1 {
		t.Fatalf("got %d ports, want 1: %+v", len(ports), ports)
	}
	p := ports[0]
	if p.ID != "ttyUSB0" {
		t.Errorf("id = %q, want the /dev basename (no by-path → unstable fallback)", p.ID)
	}
	if p.IDSource != idSourceDev || p.IDStable {
		t.Errorf("{source %q, stable %v}, want {dev, false} — by-id is never the id", p.IDSource, p.IDStable)
	}
	if filepath.Base(p.ByID) != cp2102 {
		t.Errorf("ByID hint = %q, want the retained by-id string", p.ByID)
	}
}

// No by-id AND no by-path → the /dev basename, flagged UNSTABLE.
func TestEnumerate_DevBasenameLastResortUnstable(t *testing.T) {
	e := buildTree(t, []devSpec{{dev: "ttyUSB0"}})
	ports := e.Enumerate()
	if len(ports) != 1 {
		t.Fatalf("got %d ports, want 1: %+v", len(ports), ports)
	}
	p := ports[0]
	if p.ID != "ttyUSB0" {
		t.Errorf("id = %q, want the /dev basename", p.ID)
	}
	if p.IDSource != idSourceDev {
		t.Errorf("IDSource = %q, want %q", p.IDSource, idSourceDev)
	}
	if p.IDStable {
		t.Errorf("IDStable = true, want false (a /dev-basename id is unstable)")
	}
	if p.ByID != "" || p.ByPath != "" {
		t.Errorf("ByID/ByPath should be empty for a /dev-only device: %q/%q", p.ByID, p.ByPath)
	}
}

// Dedupe guarantee: a contrived cross-source basename clash (device A's by-path
// basename equals device B's /dev basename) must NOT leave two identical ids.
func TestEnumerate_DedupeNeverDuplicateIDs(t *testing.T) {
	e := buildTree(t, []devSpec{
		{dev: "ttyACM0", byPath: "ttyUSB0"}, // by-path basename collides with B's /dev name
		{dev: "ttyUSB0"},                    // no links → /dev fallback, base id "ttyUSB0"
	})
	ports := e.Enumerate()
	if len(ports) != 2 {
		t.Fatalf("got %d ports, want 2: %+v", len(ports), ports)
	}
	seen := map[string]bool{}
	for _, p := range ports {
		if p.ID == "" {
			t.Errorf("empty id: %+v", p)
		}
		if seen[p.ID] {
			t.Fatalf("duplicate id after dedupe: %q", p.ID)
		}
		seen[p.ID] = true
	}
	// The two devices derived the same base id "ttyUSB0"; dedupe must have
	// appended a discriminator to keep them apart.
	for _, p := range ports {
		if p.ID == "ttyUSB0" {
			// At most one may keep a bare "ttyUSB0"; but since BOTH collided here,
			// neither should — assert a discriminator was applied.
			t.Errorf("id %q was not disambiguated", p.ID)
		}
	}
}

// A by-path discriminator keeps a disambiguated id STABLE; a /dev discriminator
// does not. Contrived: two devices derive the same base id, one from a stable
// by-path link, the other from the /dev fallback.
func TestEnumerate_DedupeStableDiscriminatorKeepsStability(t *testing.T) {
	e := buildTree(t, []devSpec{
		{dev: "ttyACM0", byPath: "ttyUSB1"}, // by-path basename == B's /dev name
		{dev: "ttyUSB1"},                    // /dev fallback, base id "ttyUSB1"
	})
	ports := e.Enumerate()
	if len(ports) != 2 {
		t.Fatalf("got %d ports, want 2: %+v", len(ports), ports)
	}
	stable, _ := portByDev(ports, "ttyACM0") // derived from by-path
	if stable.IDSource != idSourceByPath || !stable.IDStable {
		t.Errorf("ttyACM0 = {source %q, stable %v}, want {by-path, true} after a stable-discriminator dedupe", stable.IDSource, stable.IDStable)
	}
	unstable, _ := portByDev(ports, "ttyUSB1") // /dev fallback
	if unstable.IDStable {
		t.Errorf("ttyUSB1 should remain unstable (/dev fallback)")
	}
	if stable.ID == unstable.ID {
		t.Fatalf("ids not disambiguated: both %q", stable.ID)
	}
}

func TestReadUsbIds_Missing(t *testing.T) {
	// A sysfs tree with no idVendor anywhere → empty hints, no panic.
	root := t.TempDir()
	dev := filepath.Join(root, "ttyUSB0", "device")
	if err := os.MkdirAll(dev, 0o755); err != nil {
		t.Fatal(err)
	}
	vid, pid := readUsbIds(root, "ttyUSB0")
	if vid != "" || pid != "" {
		t.Errorf("got %s:%s, want empty for a tree with no USB ids", vid, pid)
	}
}
