// Command packetnet-tsnet is pdn's embedded Tailscale node.
//
// It joins the operator's tailnet via tailscale.com/tsnet (userspace — no
// tailscaled, no root, no TUN), terminates TLS for pdn.<tailnet>.ts.net with
// the auto Let's Encrypt cert, and reverse-proxies to pdn's loopback HTTP. The
// loopback hop carries X-Forwarded-Proto: https + X-Forwarded-Host so pdn's
// loopback-trusted ForwardedHeaders see the request as HTTPS at the .ts.net
// host — which is what makes WebAuthn/passkeys work remotely.
//
// stdout is a JSON status stream (one object per line) the .NET supervisor
// parses; stderr is free-form logs. SIGTERM → graceful shutdown, exit 0.
//
// SIGHUP → live-reload the --forwards-file ONLY: re-read it, diff against the
// active forwards, and open/close just the listeners that changed — all on the
// existing tsnet node (WireGuard, DERP, the netmap, and the web --target proxy
// are untouched). This is what lets enabling/disabling a forward-declaring app
// reconfigure forwards WITHOUT tearing down + rejoining the tailnet (which would
// drop every tailnet connection, including the operator's control-panel session
// over the tailnet). A forwards-only change is a SIGHUP, not a restart.
//
// Tags are NOT a flag here: a tsnet node inherits its tags from the pre-auth
// key it joins with. Mint the key with the desired tags (e.g. tag:server).
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"

	"tailscale.com/ipn"
	"tailscale.com/tsnet"
)

func main() {
	hostname := flag.String("hostname", "pdn", "desired node name → <hostname>.<tailnet>.ts.net")
	stateDir := flag.String("state-dir", "", "persistent tsnet state directory (load-bearing for stable hostname/cert)")
	target := flag.String("target", "127.0.0.1:8080", "loopback HTTP host:port to reverse-proxy to")
	authKeyFile := flag.String("authkey-file", "", "optional path to a file holding a tailnet pre-auth key (first-join only)")
	funnel := flag.Bool("funnel", false, "expose publicly via Tailscale Funnel instead of tailnet-only")
	forwardsFile := flag.String("forwards-file", "", "optional path to a JSON array of app-declared port forwards")
	flag.Parse()

	if *stateDir == "" {
		emit(status{State: "error", Error: "--state-dir is required"})
		os.Exit(1)
	}

	// SIGTERM/SIGINT → cancel ctx → graceful shutdown, exit 0.
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	// SIGHUP → live-reload the forwards file (kept off the ctx so it never tears
	// the node down — it only reconciles the forward listeners).
	hup := make(chan os.Signal, 1)
	signal.Notify(hup, syscall.SIGHUP)
	defer signal.Stop(hup)

	if err := run(ctx, hup, *hostname, *stateDir, *target, *authKeyFile, *forwardsFile, *funnel); err != nil {
		// A cancelled context is the clean SIGTERM path, not a fatal error.
		if ctx.Err() != nil {
			os.Exit(0)
		}
		emit(status{State: "error", Error: err.Error()})
		os.Exit(1)
	}
}

func run(ctx context.Context, hup <-chan os.Signal, hostname, stateDir, target, authKeyFile, forwardsFile string, funnel bool) error {
	emit(status{State: "starting"})

	authKey := readAuthKey(authKeyFile) // "" if absent/empty — falls back to interactive login.

	srv := &tsnet.Server{
		Hostname: hostname,
		Dir:      stateDir,
		AuthKey:  authKey,
		Logf:     func(format string, args ...any) { fmt.Fprintf(os.Stderr, format+"\n", args...) },
	}
	defer srv.Close()

	// Up blocks until the node is running (or ctx is cancelled). We watch the
	// IPN bus concurrently so we can surface the interactive login URL while Up
	// is still waiting for the operator to authenticate.
	watchCtx, cancelWatch := context.WithCancel(ctx)
	defer cancelWatch()
	go watchLogin(watchCtx, srv)

	if _, err := srv.Up(ctx); err != nil {
		return fmt.Errorf("tsnet up: %w", err)
	}
	cancelWatch() // joined — stop nagging about login.

	fqdn, err := waitForFQDN(ctx, srv)
	if err != nil {
		return err
	}

	// App-declared port forwards run alongside the web reverse-proxy on the
	// same tsnet node. They are best-effort: a missing/garbled forwards file
	// or a single bad entry never blocks the web path. The registry tracks the
	// active forward per listen port so SIGHUP can diff + reconcile it live.
	active := make(map[int]*activeForward)
	openForward := newForwardOpener(srv)
	startForwards(forwardsFile, active, openForward)
	// On shutdown, close every active forward (their accept loops unblock).
	defer closeAllForwards(active)

	proxy := newProxy(target, fqdn)

	var ln net.Listener
	if funnel {
		ln, err = srv.ListenFunnel("tcp", ":443")
	} else {
		ln, err = srv.ListenTLS("tcp", ":443")
	}
	if err != nil {
		return fmt.Errorf("listen :443 (funnel=%v): %w", funnel, err)
	}
	defer ln.Close()

	emit(status{State: "running", FQDN: fqdn})

	httpSrv := &http.Server{Handler: proxy}
	serveErr := make(chan error, 1)
	go func() {
		err := httpSrv.Serve(ln)
		if err != nil && err != http.ErrServerClosed {
			serveErr <- fmt.Errorf("serve: %w", err)
			return
		}
		serveErr <- nil
	}()

	// The supervisor loop: serve the web proxy until ctx-cancel (SIGTERM →
	// graceful shutdown) or a fatal serve error, reloading forwards on SIGHUP.
	// SIGHUP never touches the node, WireGuard, or the web proxy — only the
	// forward listeners — so existing tailnet connections survive a reload.
	for {
		select {
		case <-ctx.Done():
			shutCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			_ = httpSrv.Shutdown(shutCtx)
			cancel()
			return <-serveErr
		case err := <-serveErr:
			return err
		case <-hup:
			reloadForwards(forwardsFile, active, openForward)
		}
	}
}

// newProxy reverse-proxies to the loopback target, stamping the forwarded
// headers pdn's loopback-trusted middleware reads. X-Forwarded-For is left to
// the default Director (it appends the peer).
func newProxy(target, fqdn string) *httputil.ReverseProxy {
	backend := &url.URL{Scheme: "http", Host: target}
	proxy := httputil.NewSingleHostReverseProxy(backend)
	base := proxy.Director
	proxy.Director = func(r *http.Request) {
		base(r)
		r.Header.Set("X-Forwarded-Proto", "https")
		r.Header.Set("X-Forwarded-Host", fqdn)
	}
	return proxy
}

// watchLogin tails the IPN bus and emits needs-login with the BrowseToURL the
// control plane hands back when interactive auth is required.
func watchLogin(ctx context.Context, srv *tsnet.Server) {
	lc, err := srv.LocalClient()
	if err != nil {
		return
	}
	watcher, err := lc.WatchIPNBus(ctx, ipn.NotifyInitialState)
	if err != nil {
		return
	}
	defer watcher.Close()

	var once sync.Once
	for {
		n, err := watcher.Next()
		if err != nil {
			return // ctx cancelled or bus closed.
		}
		if n.BrowseToURL != nil && *n.BrowseToURL != "" {
			url := *n.BrowseToURL
			once.Do(func() { emit(status{State: "needs-login", AuthURL: url}) })
		}
	}
}

// waitForFQDN polls the local status until Self.DNSName is populated, then
// returns it with the trailing dot trimmed.
func waitForFQDN(ctx context.Context, srv *tsnet.Server) (string, error) {
	lc, err := srv.LocalClient()
	if err != nil {
		return "", fmt.Errorf("local client: %w", err)
	}
	for {
		st, err := lc.Status(ctx)
		if err == nil && st.Self != nil && st.Self.DNSName != "" {
			return strings.TrimSuffix(st.Self.DNSName, "."), nil
		}
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
}

func readAuthKey(path string) string {
	if path == "" {
		return ""
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "" // absent/unreadable → interactive login path.
	}
	return strings.TrimSpace(string(b))
}

// status is one line of the stdout JSON status stream the supervisor parses.
type status struct {
	State   string `json:"state"`
	AuthURL string `json:"authURL,omitempty"`
	FQDN    string `json:"fqdn,omitempty"`
	Error   string `json:"error,omitempty"`
}

var emitMu sync.Mutex

func emit(s status) {
	emitMu.Lock()
	defer emitMu.Unlock()
	b, _ := json.Marshal(s)
	fmt.Fprintln(os.Stdout, string(b))
}

// forward is one entry of the --forwards-file: a TCP listener on the tsnet
// node piped to a loopback target. tls "terminate" wraps the listener in the
// node's auto LE cert (the IMAPS/SMTPS case — the phone gets a trusted cert,
// the app stays plaintext on loopback); tls "raw" listens plaintext and relies
// on the tailnet's WireGuard encryption for the hop.
type forward struct {
	Listen int    `json:"listen"`
	Target string `json:"target"`
	TLS    string `json:"tls"`
}

// activeForward is a live entry in the forwards registry: the listener serving
// it plus the desired spec it was opened for, so a reload can tell an unchanged
// forward (leave it) from a changed one (same listen port, different tls/target
// — close + reopen).
type activeForward struct {
	forward
	listener net.Listener
}

// tsListener is the slice of *tsnet.Server that newForwardOpener depends on,
// extracted as an interface so the wiring stays testable without a tailnet.
type tsListener interface {
	Listen(network, addr string) (net.Listener, error)
	ListenTLS(network, addr string) (net.Listener, error)
}

// openFunc opens the listener for one desired forward (terminate ⇒ ListenTLS,
// raw ⇒ Listen) and starts its accept/serve loop; closeFunc closes the listener
// for a given listen port, ending that loop. Factored as functions so
// reconcileForwards is unit-testable with fakes (no tsnet, no real sockets).
type (
	openFunc  func(forward) (net.Listener, error)
	closeFunc func(listen int)
)

// newForwardOpener binds the open side to a real tsnet node: it opens the right
// listener for the forward's tls mode and pumps it with serveForward. The
// returned listener is handed back so the registry can close it on reload/exit.
func newForwardOpener(srv tsListener) openFunc {
	return func(f forward) (net.Listener, error) {
		addr := fmt.Sprintf(":%d", f.Listen)
		var (
			ln  net.Listener
			err error
		)
		if f.TLS == "terminate" {
			ln, err = srv.ListenTLS("tcp", addr)
		} else {
			ln, err = srv.Listen("tcp", addr)
		}
		if err != nil {
			return nil, err
		}
		go serveForward(ln, f.Target)
		return ln, nil
	}
}

// reconcileForwards diffs the desired forwards against the active registry and
// applies the minimal set of open/close operations on the EXISTING tsnet node:
//
//   - a forward present in desired but not active            → open
//   - a forward present in active but not desired            → close
//   - a forward whose listen port matches but whose tls or
//     target differs                                         → close, then open
//   - an unchanged forward                                   → left alone
//
// It mutates active in place and returns the listen ports it opened/closed (for
// logging + assertions). open/close are injected so this is testable with fakes:
// the node, WireGuard, DERP, and the web proxy are never touched by a reconcile.
func reconcileForwards(
	desired []forward, active map[int]*activeForward, open openFunc, closeListen closeFunc,
) (opened, closed []int) {
	want := make(map[int]forward, len(desired))
	for _, f := range desired {
		want[f.Listen] = f
	}

	// Close anything that is gone, or whose tls/target changed (it is reopened
	// from the want set below). Compare on the spec, not the listener identity.
	for listen, act := range active {
		w, keep := want[listen]
		if keep && w == act.forward {
			continue // unchanged — leave the live listener serving.
		}
		closeListen(listen)
		delete(active, listen)
		closed = append(closed, listen)
	}

	// Open anything desired that is not (now) active.
	for _, f := range desired {
		if _, live := active[f.Listen]; live {
			continue
		}
		ln, err := open(f)
		if err != nil {
			fmt.Fprintf(os.Stderr, "forward +%d (%s): listen failed: %v — skipping\n", f.Listen, f.TLS, err)
			continue
		}
		active[f.Listen] = &activeForward{forward: f, listener: ln}
		opened = append(opened, f.Listen)
		fmt.Fprintf(os.Stderr, "forward +%d (%s) -> %s\n", f.Listen, f.TLS, f.Target)
	}
	return opened, closed
}

// readDesiredForwards reads + parses the forwards file into the desired set. A
// missing/garbled file logs and yields no forwards (so a reload with a bad file
// closes everything rather than faulting) — forwards are a best-effort overlay.
func readDesiredForwards(forwardsFile string) []forward {
	if forwardsFile == "" {
		return nil
	}
	b, err := os.ReadFile(forwardsFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "forwards-file %q: %v — treating as no forwards\n", forwardsFile, err)
		return nil
	}
	forwards, err := parseForwards(b)
	if err != nil {
		fmt.Fprintf(os.Stderr, "%v — treating as no forwards\n", err)
		return nil
	}
	return forwards
}

// closeActiveForward closes one active forward's listener by listen port, which
// unblocks its serveForward accept loop. Used as the closeFunc for the real node.
func closeActiveForward(active map[int]*activeForward) closeFunc {
	return func(listen int) {
		if act, ok := active[listen]; ok {
			_ = act.listener.Close()
			fmt.Fprintf(os.Stderr, "forward -%d\n", listen)
		}
	}
}

// closeAllForwards closes every active forward (the SIGTERM/exit teardown). The
// registry entries are left for the caller to drop — the process is exiting.
func closeAllForwards(active map[int]*activeForward) {
	for _, act := range active {
		_ = act.listener.Close()
	}
}

// parseForwards parses the --forwards-file bytes into validated forward
// entries. It is lenient by design: a malformed top-level document is an
// error (caller logs + runs with no forwards), but well-formed-but-bad
// individual entries are skipped (logged) rather than failing the whole set.
func parseForwards(jsonBytes []byte) ([]forward, error) {
	trimmed := strings.TrimSpace(string(jsonBytes))
	if trimmed == "" {
		return nil, nil // empty file ⇒ no forwards, not an error.
	}
	var raw []forward
	if err := json.Unmarshal([]byte(trimmed), &raw); err != nil {
		return nil, fmt.Errorf("forwards-file: %w", err)
	}
	out := make([]forward, 0, len(raw))
	for i, f := range raw {
		if f.Listen <= 0 || f.Listen > 65535 {
			fmt.Fprintf(os.Stderr, "forward[%d]: invalid listen port %d — skipping\n", i, f.Listen)
			continue
		}
		if f.Target == "" {
			fmt.Fprintf(os.Stderr, "forward[%d] (listen %d): empty target — skipping\n", i, f.Listen)
			continue
		}
		mode := f.TLS
		if mode == "" {
			mode = "raw" // default: plaintext over the tailnet's WireGuard.
		}
		if mode != "terminate" && mode != "raw" {
			fmt.Fprintf(os.Stderr, "forward[%d] (listen %d): unknown tls mode %q — skipping\n", i, f.Listen, f.TLS)
			continue
		}
		f.TLS = mode
		out = append(out, f)
	}
	return out, nil
}

// startForwards opens the initial forward set by reconciling the desired set
// (from the forwards file) against the empty registry. It never returns an
// error: forwards are a best-effort overlay on the web path, so every failure
// is logged and the web reverse-proxy continues regardless.
func startForwards(forwardsFile string, active map[int]*activeForward, open openFunc) {
	reconcileForwards(readDesiredForwards(forwardsFile), active, open, closeActiveForward(active))
}

// reloadForwards is the SIGHUP path: re-read the forwards file and reconcile the
// live registry against it on the existing tsnet node — open newly-added
// forwards, close removed ones, close+reopen changed ones. The node, WireGuard,
// and the web proxy are untouched, so existing tailnet connections survive.
func reloadForwards(forwardsFile string, active map[int]*activeForward, open openFunc) {
	desired := readDesiredForwards(forwardsFile)
	opened, closed := reconcileForwards(desired, active, open, closeActiveForward(active))
	fmt.Fprintf(os.Stderr, "forwards reloaded (SIGHUP): +%d listener(s), -%d listener(s), %d active\n",
		len(opened), len(closed), len(active))
}

// serveForward accepts connections on ln and pipes each to a freshly-dialled
// plaintext TCP connection to target. The tls:terminate vs raw distinction is
// entirely in which listener the caller hands us — the byte-pump below is
// identical for both (and so is fully unit-testable with a plain net.Listener).
//
// A dial failure for one connection is logged and that conn closed; the
// listener keeps serving. serveForward returns when the listener is closed
// (ctx-cancel/SIGTERM), so it never crashes the sidecar.
func serveForward(ln net.Listener, target string) {
	for {
		conn, err := ln.Accept()
		if err != nil {
			// A closed listener (shutdown) is the normal exit, not an error.
			if errors.Is(err, net.ErrClosed) {
				return
			}
			// Transient accept errors: log and keep serving.
			fmt.Fprintf(os.Stderr, "forward -> %s: accept: %v\n", target, err)
			return
		}
		go pipeConn(conn, target)
	}
}

// pipeConn dials target and bidirectionally copies bytes between the accepted
// connection and the target until either side closes.
func pipeConn(client net.Conn, target string) {
	defer client.Close()
	upstream, err := net.Dial("tcp", target)
	if err != nil {
		fmt.Fprintf(os.Stderr, "forward -> %s: dial: %v\n", target, err)
		return
	}
	defer upstream.Close()

	var wg sync.WaitGroup
	wg.Add(2)
	cp := func(dst, src net.Conn) {
		defer wg.Done()
		_, _ = io.Copy(dst, src)
		// Half-close so the peer's Copy sees EOF and the pair tears down.
		if cw, ok := dst.(interface{ CloseWrite() error }); ok {
			_ = cw.CloseWrite()
		} else {
			_ = dst.Close()
		}
	}
	go cp(upstream, client)
	go cp(client, upstream)
	wg.Wait()
}
