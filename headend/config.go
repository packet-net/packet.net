package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"os"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Duration is a time.Duration that (un)marshals JSON as an operator-friendly
// duration STRING ("3s", "500ms") — so the rescanInterval reads clearly in the
// JSON config — while also accepting a bare JSON number as a count of SECONDS
// (so "rescanInterval": 3 means 3s, the least-surprising reading for a hand-edited
// config, NOT 3ns as encoding/json's default int64 handling would give).
type Duration time.Duration

// Duration returns the wrapped time.Duration.
func (d Duration) Duration() time.Duration { return time.Duration(d) }

// String renders the Go duration form ("3s"), for logs.
func (d Duration) String() string { return time.Duration(d).String() }

// MarshalJSON emits the duration as a Go duration string.
func (d Duration) MarshalJSON() ([]byte, error) {
	return json.Marshal(time.Duration(d).String())
}

// UnmarshalJSON accepts either a duration string ("3s") or a bare number of
// seconds (3 → 3s).
func (d *Duration) UnmarshalJSON(b []byte) error {
	var v any
	if err := json.Unmarshal(b, &v); err != nil {
		return err
	}
	switch x := v.(type) {
	case string:
		parsed, err := time.ParseDuration(x)
		if err != nil {
			return fmt.Errorf("rescanInterval %q: %w", x, err)
		}
		*d = Duration(parsed)
	case float64:
		*d = Duration(time.Duration(x * float64(time.Second)))
	default:
		return fmt.Errorf("rescanInterval: want a duration string or a number of seconds, got %T", v)
	}
	return nil
}

// Config is the head-end daemon's full runtime configuration. Every field has a
// sane default (see defaultConfig); an operator overrides only what they need.
// Resolution order is defaults < JSON file < environment < explicit flags — see
// loadConfig.
type Config struct {
	// InstanceID is the stable identity advertised over mDNS (both the DNS-SD
	// instance label AND the TXT instance= key) and returned in the inventory. PDN
	// keys device→port bindings by (instanceId, stableSerial), so this MUST NOT
	// change across reboots/address changes AND must be unique per box on the LAN.
	// Zero-config default: "{hostname}-{short machine-id hash}" (see
	// defaultInstanceID) — distinct across image-cloned Pis that share a hostname.
	// Fixed installs SHOULD pin an explicit stable id (--instance) instead.
	InstanceID string `json:"instanceId"`

	// BindAddr optionally restricts every listener (the HTTP machine API AND every
	// raw-serial bridge) to a single local interface address, e.g. a Tailscale
	// "100.x.y.z" or a "tailscale0" address. Empty (the default) binds all
	// interfaces (":port") — byte-for-byte today's behaviour. Set it to keep the
	// auth-less-by-design head-end off untrusted networks. See listenAddr.
	BindAddr string `json:"bindAddr"`

	// HTTPPort is the machine API listener (inventory + line-control + healthz
	// + statusz).
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

	// RescanInterval is the hot-plug re-enumeration poll period. Every interval the
	// daemon re-enumerates serial devices (same allow/deny filter) and diffs against
	// the live bridge set: a newly-plugged device gets a bridge on the lowest free
	// TCP port; an unplugged device's bridge is torn down and its port freed; an
	// unchanged device — and any client connected to it — is left completely
	// untouched. ZERO DISABLES the poll → startup-only enumeration, byte-for-byte
	// today's behaviour (no regression). Default 3s. Must be ≥ 0.
	RescanInterval Duration `json:"rescanInterval"`
}

// EnvPrefix namespaces the daemon's environment variables.
const EnvPrefix = "PACKETNET_HEADEND_"

// defaultConfig is the zero-operator-config baseline: a robustly-unique derived
// identity (see defaultInstanceID), the documented default ports, 9600 baud,
// bridge every discovered serial device.
func defaultConfig() Config {
	return Config{
		InstanceID:     defaultInstanceID(),
		HTTPPort:       7300,
		BaseTCPPort:    7301,
		Baud:           9600,
		Allow:          []string{"*"},
		Deny:           nil,
		RescanInterval: Duration(3 * time.Second),
	}
}

// machineIDFiles are read (in order) to derive the stable per-machine suffix for
// the zero-config default instanceId. systemd's /etc/machine-id is a per-install
// id regenerated on first boot of a fresh image; the D-Bus copy is the fallback
// on hosts without the systemd one.
var machineIDFiles = []string{"/etc/machine-id", "/var/lib/dbus/machine-id"}

// fallbackMachineToken is the last-resort suffix when neither a machine-id file
// nor a NIC MAC is available. It is deliberately NOT unique — reaching it is a
// signal (logged) that the operator should pin an explicit --instance.
const fallbackMachineToken = "nomachineid"

// defaultInstanceID derives the zero-config default identity from the real host:
// "{hostname}-{suffix}", where {suffix} is a stable 8-hex-char per-machine token
// (see machineSuffix). Deterministic across reboots yet distinct across
// image-cloned machines — two Pis flashed from one image (both "raspberrypi")
// carry different machine-ids, so they DON'T collide on the bare hostname. An
// operator override (--instance / env / config) still wins wholesale; for fixed
// installs pinning an explicit stable id is the recommended setup (see README).
func defaultInstanceID() string {
	host, _ := os.Hostname()
	suffix := machineSuffix(machineIDFiles, os.ReadFile, nicMACs, func(msg string) { log.Print(msg) })
	return deriveInstanceID(host, suffix)
}

// deriveInstanceID joins a hostname with a stable suffix, substituting a fixed
// placeholder when the OS reports an empty hostname. Pure, for testability.
func deriveInstanceID(hostname, suffix string) string {
	host := strings.TrimSpace(hostname)
	if host == "" {
		host = "pdn-headend"
	}
	return host + "-" + suffix
}

// machineSuffix returns an 8-hex-char stable per-machine token. It hashes the
// first readable, non-empty machine-id file; failing that, the first non-loopback
// NIC MAC; failing that it warns and returns a fixed literal. The value source is
// domain-tagged before hashing so a machine-id can never collide with a MAC. The
// file reader and MAC source are injected so tests never read the real host.
func machineSuffix(paths []string, readFile func(string) ([]byte, error), macs func() ([]string, error), warn func(string)) string {
	for _, p := range paths {
		b, err := readFile(p)
		if err != nil {
			continue
		}
		if id := strings.TrimSpace(string(b)); id != "" {
			return shortHash("machine-id:" + id)
		}
	}
	if list, err := macs(); err == nil {
		for _, mac := range list {
			if m := strings.TrimSpace(mac); m != "" {
				return shortHash("mac:" + m)
			}
		}
	}
	warn("could not derive a stable machine id (no machine-id file, no NIC MAC); " +
		"using a fixed fallback instance suffix — set an explicit --instance " +
		"(or PACKETNET_HEADEND_INSTANCE) for a unique identity")
	return fallbackMachineToken
}

// shortHash returns the first 8 hex chars (the leading 4 bytes) of SHA-256(s).
func shortHash(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:4])
}

// nicMACs returns the hardware addresses of non-loopback interfaces that have
// one, sorted by interface name so the "first" pick is deterministic across
// reboots. Used only as a fallback when no machine-id file is readable.
func nicMACs() ([]string, error) {
	ifaces, err := net.Interfaces()
	if err != nil {
		return nil, err
	}
	sort.Slice(ifaces, func(i, j int) bool { return ifaces[i].Name < ifaces[j].Name })
	var out []string
	for _, ifi := range ifaces {
		if ifi.Flags&net.FlagLoopback != 0 {
			continue
		}
		if len(ifi.HardwareAddr) == 0 {
			continue
		}
		out = append(out, ifi.HardwareAddr.String())
	}
	return out, nil
}

// flagOverrides carries the flags an operator set explicitly (nil = unset), so
// flags win over env/file only when actually passed. main builds this from the
// flag set via flag.Visit.
type flagOverrides struct {
	InstanceID     *string
	BindAddr       *string
	HTTPPort       *int
	BaseTCPPort    *int
	Baud           *int
	Allow          *string // comma-separated
	Deny           *string // comma-separated
	RescanInterval *time.Duration
	ConfigPath     *string
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
	// BIND_ADDR intentionally honours an explicit empty value (bind-all) too, so an
	// env-level "" can clear a file-set address — hence no `&& v != ""` guard.
	if v, ok := env(EnvPrefix + "BIND_ADDR"); ok {
		cfg.BindAddr = v
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
	if err := applyDurationEnv(env, EnvPrefix+"RESCAN_INTERVAL", &cfg.RescanInterval); err != nil {
		return Config{}, err
	}

	// Explicit-flag overlay (highest precedence).
	if flags.InstanceID != nil {
		cfg.InstanceID = *flags.InstanceID
	}
	if flags.BindAddr != nil {
		cfg.BindAddr = *flags.BindAddr
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
	if flags.RescanInterval != nil {
		cfg.RescanInterval = Duration(*flags.RescanInterval)
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
	if c.RescanInterval < 0 {
		return fmt.Errorf("rescanInterval %s must not be negative (0 disables hot-plug rescan)", c.RescanInterval)
	}
	return nil
}

// listenAddr composes the "host:port" a listener binds to from the optional
// BindAddr and a port. An empty bindAddr yields ":port" (all interfaces — today's
// default behaviour); a set bindAddr yields "bindAddr:port", restricting the
// listener to that one interface/address (e.g. a Tailscale 100.x.y.z). Both the
// HTTP machine API (main.go) and every raw-serial bridge (bridge.go) go through
// here, so one BindAddr fences the whole auth-less daemon onto a trusted network.
func listenAddr(bindAddr string, port int) string {
	return fmt.Sprintf("%s:%d", bindAddr, port)
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

// applyDurationEnv overlays a Go duration string ("3s", "500ms", "0") from the
// environment onto dst. Empty/unset leaves dst untouched.
func applyDurationEnv(env func(string) (string, bool), key string, dst *Duration) error {
	v, ok := env(key)
	if !ok || strings.TrimSpace(v) == "" {
		return nil
	}
	parsed, err := time.ParseDuration(strings.TrimSpace(v))
	if err != nil {
		return fmt.Errorf("%s=%q: not a duration (want e.g. 3s, 500ms, 0)", key, v)
	}
	*dst = Duration(parsed)
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
