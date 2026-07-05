package main

import (
	"os"
	"path/filepath"
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
