// Command packetnet-headend is the split-station RF head-end daemon.
//
// It runs headless on a Raspberry Pi that physically holds the NinoTNC modems +
// Tait radio-control cables, and exposes every attached serial device as a raw
// TCP byte pipe plus a tiny machine API (inventory + line-control) and an mDNS
// advertisement. A remote PDN box discovers the fleet, pulls each instance's
// inventory, dials the raw pipes, and drives them with its OWN drivers.
//
// The head-end does NO device identification and NO protocol parsing: it neither
// speaks KISS nor CCDI — both are just bytes on the wire. It is a dumb,
// transparent multiplexer. Identification (NinoTNC GETVER, Tait CCDI MODEL) and
// the entire AX.25 / radio-control stack live on the PDN side, reaching through
// the pipe. Design: docs/research/split-station-rf-headend.md.
//
// SIGTERM/SIGINT → graceful shutdown (close listeners, drop serial handles,
// withdraw mDNS), exit 0.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("headend: ")

	cfg, err := parseConfig(os.Args[1:], os.LookupEnv)
	if err != nil {
		log.Fatalf("config: %v", err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	if err := run(ctx, cfg, defaultEnumerator(), openRealSerial); err != nil {
		log.Fatalf("%v", err)
	}
}

// parseConfig builds the flag set, parses argv, and resolves the effective
// config (defaults < file < env < explicit flags). Split out so it is testable.
func parseConfig(args []string, lookupEnv func(string) (string, bool)) (Config, error) {
	fs := flag.NewFlagSet("headend", flag.ContinueOnError)
	def := defaultConfig()
	var (
		instance = fs.String("instance", def.InstanceID, "stable instance id/name advertised over mDNS + inventory (default: hostname)")
		bindAddr = fs.String("bind-addr", def.BindAddr, "restrict every listener to this local address (e.g. a Tailscale 100.x.y.z); empty = all interfaces")
		httpPort = fs.Int("http-port", def.HTTPPort, "HTTP machine-API port (inventory + line-control + healthz)")
		baseTCP  = fs.Int("base-tcp-port", def.BaseTCPPort, "first raw-serial bridge TCP port; devices allocate sequentially from here")
		baud     = fs.Int("baud", def.Baud, "default serial baud every port is opened at (PDN re-clocks Tait via the line verb)")
		allow    = fs.String("allow", "", "comma-separated globs of /dev or by-id basenames to bridge (default: all)")
		deny     = fs.String("deny", "", "comma-separated globs of /dev or by-id basenames to skip")
		rescan   = fs.Duration("rescan-interval", def.RescanInterval.Duration(), "hot-plug re-enumeration poll period (e.g. 3s); 0 disables → startup-only enumeration")
		confPath = fs.String("config", "", "optional path to a JSON config file")
	)
	if err := fs.Parse(args); err != nil {
		return Config{}, err
	}

	// Only pass through flags the operator set explicitly, so env/file aren't
	// clobbered by a flag left at its default.
	var ov flagOverrides
	fs.Visit(func(f *flag.Flag) {
		switch f.Name {
		case "instance":
			ov.InstanceID = instance
		case "bind-addr":
			ov.BindAddr = bindAddr
		case "http-port":
			ov.HTTPPort = httpPort
		case "base-tcp-port":
			ov.BaseTCPPort = baseTCP
		case "baud":
			ov.Baud = baud
		case "allow":
			ov.Allow = allow
		case "deny":
			ov.Deny = deny
		case "rescan-interval":
			ov.RescanInterval = rescan
		case "config":
			ov.ConfigPath = confPath
		}
	})
	return loadConfig(lookupEnv, ov)
}

// run enumerates + bridges the serial devices, starts the HTTP API and mDNS, and
// blocks until ctx is cancelled (SIGTERM), then shuts everything down cleanly.
// The enumerator and serial opener are injected so the whole daemon is testable
// without real hardware.
func run(ctx context.Context, cfg Config, enum deviceEnumerator, open SerialOpener) error {
	bindScope := "all interfaces"
	if cfg.BindAddr != "" {
		bindScope = fmt.Sprintf("bound to %s", cfg.BindAddr)
	}
	log.Printf("instance %q — http :%d, bridges from :%d, default %d baud (%s)",
		cfg.InstanceID, cfg.HTTPPort, cfg.BaseTCPPort, cfg.Baud, bindScope)

	bridges := buildBridges(cfg, enum, open)
	if len(bridges) == 0 {
		log.Printf("no serial devices bridged (none discovered / all filtered) — serving an empty inventory")
	}

	reg := newRegistry(cfg.InstanceID, bridges)
	// The registry now owns the live bridge set (startup + hot-plugged − unplugged);
	// closeAll on shutdown tears down whatever is live, not just the startup set.
	defer reg.closeAll()
	for _, b := range bridges {
		go b.run()
	}

	// Hot-plug: poll-based re-enumerate + diff (dep-free, no udev/netlink). Disabled
	// when the interval is 0 → exactly the original startup-only enumeration.
	if startRescan(ctx, reg, cfg, enum, open, defaultLine(cfg.Baud)) {
		log.Printf("hot-plug rescan every %s (0 disables)", cfg.RescanInterval)
	} else {
		log.Printf("hot-plug rescan disabled (rescan-interval 0) — startup enumeration only")
	}

	// mDNS is best-effort: on a routed/Tailscale LAN where multicast can't cross,
	// PDN falls back to a manual address list — so a registration failure logs
	// and the daemon carries on serving the HTTP API + pipes.
	if server, err := advertise(cfg); err != nil {
		log.Printf("mDNS advertise failed (continuing without it): %v", err)
	} else {
		log.Printf("mDNS advertising %s instance=%q on :%d", mdnsService, cfg.InstanceID, cfg.HTTPPort)
		defer server.Shutdown()
	}

	httpSrv := &http.Server{
		Addr:              listenAddr(cfg.BindAddr, cfg.HTTPPort),
		Handler:           reg.handler(),
		ReadHeaderTimeout: 5 * time.Second,
	}
	serveErr := make(chan error, 1)
	go func() {
		err := httpSrv.ListenAndServe()
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			serveErr <- fmt.Errorf("http serve: %w", err)
			return
		}
		serveErr <- nil
	}()
	log.Printf("HTTP machine API on :%d (GET /inventory, POST /ports/{id}/line, GET /healthz)", cfg.HTTPPort)

	select {
	case <-ctx.Done():
		log.Printf("shutting down")
		shutCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = httpSrv.Shutdown(shutCtx)
		return <-serveErr
	case err := <-serveErr:
		return err
	}
}

// buildBridges enumerates serial devices, applies the allow/deny filter, and
// opens a bridge per surviving device with a sequential TCP port. A device that
// fails to open (busy / permission) is logged and skipped rather than aborting
// the daemon; the rest still come up.
func buildBridges(cfg Config, enum deviceEnumerator, open SerialOpener) []*Bridge {
	line := defaultLine(cfg.Baud)
	var bridges []*Bridge
	next := cfg.BaseTCPPort
	for _, dev := range enum.Enumerate() {
		if !cfg.allowed(baseName(dev.DevPath), baseName(dev.ByID)) {
			log.Printf("skip %s (%s): filtered by allow/deny", dev.ID, dev.DevPath)
			continue
		}
		b, err := newBridge(dev, next, cfg.BindAddr, line, open)
		if err != nil {
			log.Printf("skip %s (%s): %v", dev.ID, dev.DevPath, err)
			continue
		}
		log.Printf("bridge %s [id via %s%s]: %s <-> tcp :%d (vid:pid %s:%s)",
			dev.ID, dev.IDSource, unstableTag(dev.IDStable), dev.DevPath, next, orNone(dev.USBVid), orNone(dev.USBPid))
		bridges = append(bridges, b)
		next++
	}
	return bridges
}
