package main

import (
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"testing"
)

// envMap returns an env lookup backed by a map, for pure loadConfig tests.
func envMap(m map[string]string) func(string) (string, bool) {
	return func(k string) (string, bool) {
		v, ok := m[k]
		return v, ok
	}
}

func TestLoadConfig_Defaults(t *testing.T) {
	cfg, err := loadConfig(envMap(nil), flagOverrides{})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.HTTPPort != 7300 || cfg.BaseTCPPort != 7301 || cfg.Baud != 9600 {
		t.Errorf("defaults = %+v, want http 7300 / base 7301 / baud 9600", cfg)
	}
	if cfg.InstanceID == "" {
		t.Errorf("default instanceId must not be empty (hostname fallback)")
	}
	if len(cfg.Allow) != 1 || cfg.Allow[0] != "*" {
		t.Errorf("default Allow = %v, want [*]", cfg.Allow)
	}
}

func TestLoadConfig_EnvOverridesDefaults(t *testing.T) {
	env := envMap(map[string]string{
		EnvPrefix + "INSTANCE":      "pi-shack",
		EnvPrefix + "HTTP_PORT":     "8000",
		EnvPrefix + "BASE_TCP_PORT": "8100",
		EnvPrefix + "BAUD":          "28800",
		EnvPrefix + "ALLOW":         "ttyUSB*,usb-Nino*",
		EnvPrefix + "DENY":          "ttyUSB9",
	})
	cfg, err := loadConfig(env, flagOverrides{})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.InstanceID != "pi-shack" || cfg.HTTPPort != 8000 || cfg.BaseTCPPort != 8100 || cfg.Baud != 28800 {
		t.Errorf("env overlay = %+v", cfg)
	}
	if len(cfg.Allow) != 2 || cfg.Allow[0] != "ttyUSB*" || cfg.Allow[1] != "usb-Nino*" {
		t.Errorf("Allow = %v, want [ttyUSB* usb-Nino*]", cfg.Allow)
	}
	if len(cfg.Deny) != 1 || cfg.Deny[0] != "ttyUSB9" {
		t.Errorf("Deny = %v, want [ttyUSB9]", cfg.Deny)
	}
}

func TestLoadConfig_FlagsBeatEnv(t *testing.T) {
	env := envMap(map[string]string{EnvPrefix + "HTTP_PORT": "8000", EnvPrefix + "INSTANCE": "from-env"})
	port := 9999
	inst := "from-flag"
	cfg, err := loadConfig(env, flagOverrides{HTTPPort: &port, InstanceID: &inst})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.HTTPPort != 9999 {
		t.Errorf("HTTPPort = %d, want 9999 (flag beats env)", cfg.HTTPPort)
	}
	if cfg.InstanceID != "from-flag" {
		t.Errorf("InstanceID = %q, want from-flag (flag beats env)", cfg.InstanceID)
	}
}

func TestLoadConfig_FilePrecedence(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "headend.json")
	// File sets instance + httpPort; env overrides httpPort; flag not set.
	if err := os.WriteFile(path, []byte(`{"instanceId":"file-id","httpPort":7000,"baud":19200}`), 0o600); err != nil {
		t.Fatal(err)
	}
	env := envMap(map[string]string{
		EnvPrefix + "CONFIG":    path,
		EnvPrefix + "HTTP_PORT": "7500",
	})
	cfg, err := loadConfig(env, flagOverrides{})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.InstanceID != "file-id" {
		t.Errorf("InstanceID = %q, want file-id (from file)", cfg.InstanceID)
	}
	if cfg.HTTPPort != 7500 {
		t.Errorf("HTTPPort = %d, want 7500 (env beats file)", cfg.HTTPPort)
	}
	if cfg.Baud != 19200 {
		t.Errorf("Baud = %d, want 19200 (from file, untouched by env)", cfg.Baud)
	}
	// baseTcpPort was absent from the file → default preserved.
	if cfg.BaseTCPPort != 7301 {
		t.Errorf("BaseTCPPort = %d, want default 7301 (omitted from file)", cfg.BaseTCPPort)
	}
}

func TestLoadConfig_BadIntEnv(t *testing.T) {
	env := envMap(map[string]string{EnvPrefix + "HTTP_PORT": "notanumber"})
	if _, err := loadConfig(env, flagOverrides{}); err == nil {
		t.Fatal("want error for non-integer HTTP_PORT, got nil")
	}
}

func TestLoadConfig_ValidatesRange(t *testing.T) {
	bad := -1
	if _, err := loadConfig(envMap(nil), flagOverrides{HTTPPort: &bad}); err == nil {
		t.Fatal("want error for out-of-range httpPort, got nil")
	}
}

// errNoFile is returned by an injected readFile for any path, standing in for a
// host with no machine-id file at all.
func errNoFile(string) ([]byte, error) { return nil, errors.New("no such file") }

// fileMap returns a readFile that serves bytes for the given paths and errors
// for anything else — so a test picks exactly which machine-id source "exists".
func fileMap(m map[string]string) func(string) ([]byte, error) {
	return func(p string) ([]byte, error) {
		if v, ok := m[p]; ok {
			return []byte(v), nil
		}
		return nil, errors.New("no such file")
	}
}

var eightHex = regexp.MustCompile(`^[0-9a-f]{8}$`)

func TestDeriveInstanceID(t *testing.T) {
	if got := deriveInstanceID("raspberrypi", "1a2b3c4d"); got != "raspberrypi-1a2b3c4d" {
		t.Errorf("deriveInstanceID = %q, want raspberrypi-1a2b3c4d", got)
	}
	// Whitespace hostname collapses to the fixed placeholder.
	if got := deriveInstanceID("  ", "1a2b3c4d"); got != "pdn-headend-1a2b3c4d" {
		t.Errorf("deriveInstanceID(empty) = %q, want pdn-headend-1a2b3c4d", got)
	}
}

func TestMachineSuffix_DeterministicAndPerMachine(t *testing.T) {
	noMAC := func() ([]string, error) { return nil, nil }
	warn := func(string) { t.Fatalf("warn should not fire when a machine-id is present") }

	readA := fileMap(map[string]string{"/etc/machine-id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"})
	readB := fileMap(map[string]string{"/etc/machine-id": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n"})

	a1 := machineSuffix(machineIDFiles, readA, noMAC, warn)
	a2 := machineSuffix(machineIDFiles, readA, noMAC, warn)
	b1 := machineSuffix(machineIDFiles, readB, noMAC, warn)

	if !eightHex.MatchString(a1) {
		t.Errorf("suffix %q is not 8 lowercase hex chars", a1)
	}
	if a1 != a2 {
		t.Errorf("suffix not deterministic: %q vs %q for the same machine-id", a1, a2)
	}
	if a1 == b1 {
		t.Errorf("suffix collided across different machine-ids: both %q", a1)
	}
}

func TestMachineSuffix_DBusFallbackFile(t *testing.T) {
	warn := func(string) { t.Fatalf("warn should not fire when the D-Bus machine-id is present") }
	// systemd file absent, D-Bus copy present → still derives (not the literal).
	read := fileMap(map[string]string{"/var/lib/dbus/machine-id": "cccccccccccccccccccccccccccccccc"})
	got := machineSuffix(machineIDFiles, read, func() ([]string, error) { return nil, nil }, warn)
	if !eightHex.MatchString(got) {
		t.Errorf("D-Bus fallback suffix %q is not 8 hex chars", got)
	}
	if got == fallbackMachineToken {
		t.Errorf("D-Bus machine-id present but got the literal fallback")
	}
}

func TestMachineSuffix_FallbackToMAC(t *testing.T) {
	warn := func(string) { t.Fatalf("warn should not fire when a MAC is available") }
	macs := func() ([]string, error) { return []string{"", "de:ad:be:ef:00:01"}, nil }

	m1 := machineSuffix(machineIDFiles, errNoFile, macs, warn)
	m2 := machineSuffix(machineIDFiles, errNoFile, macs, warn)
	if !eightHex.MatchString(m1) {
		t.Errorf("MAC-derived suffix %q is not 8 hex chars", m1)
	}
	if m1 != m2 {
		t.Errorf("MAC-derived suffix not deterministic: %q vs %q", m1, m2)
	}
	if m1 == fallbackMachineToken {
		t.Errorf("MAC available but got the literal fallback")
	}
	// A different MAC yields a different suffix, and a machine-id domain-tag means
	// a machine-id equal to the MAC string can't collide with the MAC hash.
	other := machineSuffix(machineIDFiles, errNoFile,
		func() ([]string, error) { return []string{"de:ad:be:ef:00:02"}, nil }, warn)
	if other == m1 {
		t.Errorf("different MACs produced the same suffix %q", m1)
	}
	if collide := shortHash("machine-id:" + "de:ad:be:ef:00:01"); collide == m1 {
		t.Errorf("machine-id and MAC domains collided for the same string")
	}
}

func TestMachineSuffix_LastResortLiteralWarns(t *testing.T) {
	warned := false
	macs := func() ([]string, error) { return nil, errors.New("no interfaces") }
	got := machineSuffix(machineIDFiles, errNoFile, macs, func(string) { warned = true })
	if got != fallbackMachineToken {
		t.Errorf("last-resort suffix = %q, want %q", got, fallbackMachineToken)
	}
	if !warned {
		t.Errorf("last-resort fallback did not emit a warning")
	}
}

func TestDefaultInstanceID_IsHostnameDashSuffix(t *testing.T) {
	// The real derivation (reads this host's machine-id) must produce a non-empty
	// "{something}-{8hex-or-literal}" — never a bare hostname.
	got := defaultInstanceID()
	if got == "" {
		t.Fatal("defaultInstanceID returned empty")
	}
	re := regexp.MustCompile(`^.+-([0-9a-f]{8}|` + regexp.QuoteMeta(fallbackMachineToken) + `)$`)
	if !re.MatchString(got) {
		t.Errorf("defaultInstanceID = %q, want {hostname}-{8hex|%s}", got, fallbackMachineToken)
	}
}

func TestLoadConfig_OverrideBeatsDerivedDefault(t *testing.T) {
	// The operator override must win wholesale over the derived default — env leg.
	env := envMap(map[string]string{EnvPrefix + "INSTANCE": "shack-north"})
	cfg, err := loadConfig(env, flagOverrides{})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.InstanceID != "shack-north" {
		t.Errorf("InstanceID = %q, want shack-north (override beats derived default)", cfg.InstanceID)
	}
	// …and the flag leg.
	pin := "garage-pi"
	cfg, err = loadConfig(envMap(nil), flagOverrides{InstanceID: &pin})
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.InstanceID != "garage-pi" {
		t.Errorf("InstanceID = %q, want garage-pi (flag override beats derived default)", cfg.InstanceID)
	}
}

func TestConfigAllowed(t *testing.T) {
	for _, tc := range []struct {
		name             string
		allow, deny      []string
		devBase, byIDBse string
		want             bool
	}{
		{"default all", []string{"*"}, nil, "ttyUSB0", "usb-Nino_x", true},
		{"allow by dev glob", []string{"ttyUSB*"}, nil, "ttyUSB0", "", true},
		{"allow by byid glob", []string{"usb-FTDI*"}, nil, "ttyUSB0", "usb-FTDI_cp2102", true},
		{"not in allow", []string{"ttyACM*"}, nil, "ttyUSB0", "usb-x", false},
		{"denied", []string{"*"}, []string{"ttyUSB9"}, "ttyUSB9", "usb-x", false},
		{"deny wins over allow match", []string{"ttyUSB*"}, []string{"*cp2102*"}, "ttyUSB0", "usb-FTDI_cp2102", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			c := Config{Allow: tc.allow, Deny: tc.deny}
			if got := c.allowed(tc.devBase, tc.byIDBse); got != tc.want {
				t.Errorf("allowed(%q,%q) = %v, want %v", tc.devBase, tc.byIDBse, got, tc.want)
			}
		})
	}
}
