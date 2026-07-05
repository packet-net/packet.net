package main

import (
	"encoding/json"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// Config is the head-end daemon's full runtime configuration. Every field has a
// sane default (see defaultConfig); an operator overrides only what they need.
// Resolution order is defaults < JSON file < environment < explicit flags — see
// loadConfig.
type Config struct {
	// InstanceID is the stable identity advertised over mDNS and returned in the
	// inventory. PDN keys device→port bindings by (instanceId, stableSerial), so
	// this MUST NOT change across reboots/address changes. Defaults to hostname.
	InstanceID string `json:"instanceId"`

	// HTTPPort is the machine API listener (inventory + line-control + healthz).
	HTTPPort int `json:"httpPort"`

	// BaseTCPPort is the first raw-serial bridge port; devices are allocated
	// sequentially from here (base, base+1, …) in discovery order.
	BaseTCPPort int `json:"baseTcpPort"`

	// Baud is the default line rate every port's serial is opened at. NinoTNC
	// CDC-ACM ignores it; a Tait UART needs it but PDN re-clocks via the
	// line-control verb, so the default rarely matters.
	Baud int `json:"baud"`

	// Allow/Deny are shell-style globs (path.Match) matched against a device's
	// /dev basename AND its by-id basename. A device is bridged when it matches
	// some Allow glob and no Deny glob. Default Allow=["*"], Deny=[] → bridge all.
	Allow []string `json:"allow"`
	Deny  []string `json:"deny"`
}

// EnvPrefix namespaces the daemon's environment variables.
const EnvPrefix = "PACKETNET_HEADEND_"

// defaultConfig is the zero-operator-config baseline: identity = hostname, the
// documented default ports, 9600 baud, bridge every discovered serial device.
func defaultConfig() Config {
	host, _ := os.Hostname()
	if strings.TrimSpace(host) == "" {
		host = "pdn-headend"
	}
	return Config{
		InstanceID:  host,
		HTTPPort:    7300,
		BaseTCPPort: 7301,
		Baud:        9600,
		Allow:       []string{"*"},
		Deny:        nil,
	}
}

// flagOverrides carries the flags an operator set explicitly (nil = unset), so
// flags win over env/file only when actually passed. main builds this from the
// flag set via flag.Visit.
type flagOverrides struct {
	InstanceID  *string
	HTTPPort    *int
	BaseTCPPort *int
	Baud        *int
	Allow       *string // comma-separated
	Deny        *string // comma-separated
	ConfigPath  *string
}

// loadConfig resolves the effective config from all sources in precedence order:
// defaults < JSON file < environment < explicit flags. env/flags for Allow/Deny
// are comma-separated lists. It is pure over its inputs (env is an injected
// lookup) so it is unit-testable without touching the real process environment.
func loadConfig(env func(string) (string, bool), flags flagOverrides) (Config, error) {
	cfg := defaultConfig()

	// The config-file path itself may come from env or a flag.
	configPath := ""
	if v, ok := env(EnvPrefix + "CONFIG"); ok {
		configPath = v
	}
	if flags.ConfigPath != nil {
		configPath = *flags.ConfigPath
	}
	if configPath != "" {
		b, err := os.ReadFile(configPath)
		if err != nil {
			return Config{}, fmt.Errorf("config file %q: %w", configPath, err)
		}
		// json.Unmarshal onto the pre-seeded cfg overlays only the keys present
		// in the file, leaving the defaults for everything omitted.
		if err := json.Unmarshal(b, &cfg); err != nil {
			return Config{}, fmt.Errorf("config file %q: %w", configPath, err)
		}
	}

	// Environment overlay.
	if v, ok := env(EnvPrefix + "INSTANCE"); ok && v != "" {
		cfg.InstanceID = v
	}
	if err := applyIntEnv(env, EnvPrefix+"HTTP_PORT", &cfg.HTTPPort); err != nil {
		return Config{}, err
	}
	if err := applyIntEnv(env, EnvPrefix+"BASE_TCP_PORT", &cfg.BaseTCPPort); err != nil {
		return Config{}, err
	}
	if err := applyIntEnv(env, EnvPrefix+"BAUD", &cfg.Baud); err != nil {
		return Config{}, err
	}
	if v, ok := env(EnvPrefix + "ALLOW"); ok {
		cfg.Allow = splitList(v)
	}
	if v, ok := env(EnvPrefix + "DENY"); ok {
		cfg.Deny = splitList(v)
	}

	// Explicit-flag overlay (highest precedence).
	if flags.InstanceID != nil {
		cfg.InstanceID = *flags.InstanceID
	}
	if flags.HTTPPort != nil {
		cfg.HTTPPort = *flags.HTTPPort
	}
	if flags.BaseTCPPort != nil {
		cfg.BaseTCPPort = *flags.BaseTCPPort
	}
	if flags.Baud != nil {
		cfg.Baud = *flags.Baud
	}
	if flags.Allow != nil {
		cfg.Allow = splitList(*flags.Allow)
	}
	if flags.Deny != nil {
		cfg.Deny = splitList(*flags.Deny)
	}

	return cfg, cfg.validate()
}

// validate rejects nonsensical combinations that would otherwise fail obscurely
// at listen time.
func (c Config) validate() error {
	if c.InstanceID == "" {
		return fmt.Errorf("instanceId must not be empty")
	}
	if c.HTTPPort < 1 || c.HTTPPort > 65535 {
		return fmt.Errorf("httpPort %d out of range 1..65535", c.HTTPPort)
	}
	if c.BaseTCPPort < 1 || c.BaseTCPPort > 65535 {
		return fmt.Errorf("baseTcpPort %d out of range 1..65535", c.BaseTCPPort)
	}
	if c.Baud <= 0 {
		return fmt.Errorf("baud %d must be positive", c.Baud)
	}
	return nil
}

// allowed reports whether a device with the given /dev and by-id basenames
// should be bridged: it must match some Allow glob and no Deny glob. Either
// basename matching a glob counts (so an operator can filter on either the
// kernel name or the stable by-id string).
func (c Config) allowed(devBase, byIDBase string) bool {
	if !matchesAny(c.Allow, devBase, byIDBase) {
		return false
	}
	if matchesAny(c.Deny, devBase, byIDBase) {
		return false
	}
	return true
}

func applyIntEnv(env func(string) (string, bool), key string, dst *int) error {
	v, ok := env(key)
	if !ok || v == "" {
		return nil
	}
	n, err := strconv.Atoi(strings.TrimSpace(v))
	if err != nil {
		return fmt.Errorf("%s=%q: not an integer", key, v)
	}
	*dst = n
	return nil
}

// splitList parses a comma/whitespace-separated list, dropping empty entries.
func splitList(s string) []string {
	fields := strings.FieldsFunc(s, func(r rune) bool { return r == ',' || r == ' ' || r == '\t' })
	out := make([]string, 0, len(fields))
	for _, f := range fields {
		if f = strings.TrimSpace(f); f != "" {
			out = append(out, f)
		}
	}
	return out
}
