package main

import (
	"encoding/json"
	"log"
	"net/http"
	"sort"
	"sync"
)

// PortInfo is one row of the inventory: the stable identity + USB hints PDN uses
// to reach through and identify, the TCP port carrying the raw byte pipe, and
// the current serial line params.
type PortInfo struct {
	ID      string `json:"id"`
	DevPath string `json:"devPath"`
	USBVid  string `json:"usbVid"`
	USBPid  string `json:"usbPid"`
	ByID    string `json:"byId"`
	ByPath  string `json:"byPath"`
	// IDSource is which link the stable id came from: "by-id" | "by-path" |
	// "dev". IDStable is the convenience bool (false only for the "dev"
	// last-resort fallback). Both additive — PDN can warn on an unstable binding.
	IDSource string `json:"idSource"`
	IDStable bool   `json:"idStable"`
	TCPPort  int    `json:"tcpPort"`
	Baud     int    `json:"baud"`
	DataBits int    `json:"dataBits"`
	Parity   string `json:"parity"`
	StopBits int    `json:"stopBits"`
}

// InventoryResponse is GET /inventory: the instance identity + every bridged
// port. PDN discovers the fleet over mDNS, pulls each instance's inventory, and
// keys device→port bindings by (instanceId, port.id).
type InventoryResponse struct {
	InstanceID string     `json:"instanceId"`
	Ports      []PortInfo `json:"ports"`
}

// BridgeStatus is one row of GET /statusz: the bridge's identity, its pipe
// port, and whether a client is currently attached to the pipe.
type BridgeStatus struct {
	ID              string `json:"id"`
	TCPPort         int    `json:"tcpPort"`
	ClientConnected bool   `json:"clientConnected"`
}

// StatusResponse is GET /statusz — head-end self-observability (#583), so
// external monitoring can watch the Pi directly instead of inferring its state
// through PDN: the instance identity, the live bridge count, and each bridge's
// client-connection state. /healthz stays a bare liveness "ok" (backward
// compatible); this is the richer, additive surface.
type StatusResponse struct {
	InstanceID  string         `json:"instanceId"`
	BridgeCount int            `json:"bridgeCount"`
	Bridges     []BridgeStatus `json:"bridges"`
}

// lineRequest is the POST /ports/{id}/line body. Fields are pointers so a
// partial request (the common baud-only sweep call) leaves the other params
// unchanged; baud is required.
type lineRequest struct {
	Baud     *int    `json:"baud"`
	DataBits *int    `json:"dataBits"`
	Parity   *string `json:"parity"`
	StopBits *int    `json:"stopBits"`
}

// Registry is the LIVE set of bridges the API serves over, plus the instance id.
// The hot-plug rescan loop mutates the set (add on plug-in, remove on unplug)
// while the HTTP handlers read it, so every access goes through the mutex. It is
// keyed two ways — by stable device id AND by resolved kernel path — so a device
// reachable via several symlinks stays one bridge, and the diff can match a
// device whose id shifts (e.g. a by-path→by-id upgrade after udev catches up)
// without churning its bridge.
type Registry struct {
	// InstanceID is set once at construction and never mutated, so it is read
	// without the lock.
	InstanceID string

	mu     sync.Mutex
	byID   map[string]*Bridge // stable device id → bridge
	byDev  map[string]*Bridge // resolved kernel DevPath → bridge
	closed bool               // set by closeAll; blocks a late rescan add from leaking
}

func newRegistry(instanceID string, bridges []*Bridge) *Registry {
	reg := &Registry{
		InstanceID: instanceID,
		byID:       make(map[string]*Bridge, len(bridges)),
		byDev:      make(map[string]*Bridge, len(bridges)),
	}
	for _, b := range bridges {
		reg.byID[b.dev.ID] = b
		if b.dev.DevPath != "" {
			reg.byDev[b.dev.DevPath] = b
		}
	}
	return reg
}

// snapshot returns the live bridges ordered by TCP port (== inventory order).
func (reg *Registry) snapshot() []*Bridge {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	out := make([]*Bridge, 0, len(reg.byID))
	for _, b := range reg.byID {
		out = append(out, b)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].tcpPort < out[j].tcpPort })
	return out
}

// lookup finds a bridge by stable device id (for POST /ports/{id}/line).
func (reg *Registry) lookup(id string) (*Bridge, bool) {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	b, ok := reg.byID[id]
	return b, ok
}

// has reports whether a device (by stable id OR resolved kernel path) is already
// bridged — the second key means a symlink/id shift on the same physical device
// doesn't read as a new device.
func (reg *Registry) has(id, devPath string) bool {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	if _, ok := reg.byID[id]; ok {
		return true
	}
	if devPath != "" {
		if _, ok := reg.byDev[devPath]; ok {
			return true
		}
	}
	return false
}

// add registers a started bridge. If the registry is already closed (shutdown
// racing a rescan tick), it Closes the bridge instead of adding it so no
// listener/serial handle leaks. Reports whether the bridge was added.
func (reg *Registry) add(b *Bridge) bool {
	reg.mu.Lock()
	if reg.closed {
		reg.mu.Unlock()
		b.Close()
		return false
	}
	reg.byID[b.dev.ID] = b
	if b.dev.DevPath != "" {
		reg.byDev[b.dev.DevPath] = b
	}
	reg.mu.Unlock()
	return true
}

// remove detaches the bridge with the given id and returns it (nil if absent).
// The caller Closes it OUTSIDE the lock (Close does network + serial teardown).
func (reg *Registry) remove(id string) *Bridge {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	b, ok := reg.byID[id]
	if !ok {
		return nil
	}
	delete(reg.byID, id)
	if b.dev.DevPath != "" && reg.byDev[b.dev.DevPath] == b {
		delete(reg.byDev, b.dev.DevPath)
	}
	return b
}

// nextFreePort returns the lowest TCP port ≥ base not currently in use by a live
// bridge, so a re-plugged device reuses a freed port before climbing.
func (reg *Registry) nextFreePort(base int) int {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	used := make(map[int]bool, len(reg.byID))
	for _, b := range reg.byID {
		used[b.tcpPort] = true
	}
	p := base
	for used[p] {
		p++
	}
	return p
}

// closeAll marks the registry closed and Closes every live bridge (SIGTERM
// shutdown). After this, add is a no-op that Closes its argument.
func (reg *Registry) closeAll() {
	reg.mu.Lock()
	reg.closed = true
	bridges := make([]*Bridge, 0, len(reg.byID))
	for _, b := range reg.byID {
		bridges = append(bridges, b)
	}
	reg.byID = map[string]*Bridge{}
	reg.byDev = map[string]*Bridge{}
	reg.mu.Unlock()
	for _, b := range bridges {
		b.Close()
	}
}

// handler wires the machine API. Routing uses net/http method+wildcard patterns
// (Go 1.22+) — no external router dependency.
func (reg *Registry) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /inventory", reg.handleInventory)
	mux.HandleFunc("POST /ports/{id}/line", reg.handleLine)
	mux.HandleFunc("GET /statusz", reg.handleStatusz)
	// /healthz stays exactly "ok" — a pure liveness probe, pinned for backward
	// compatibility (PDN's HealthAsync, the .deb install smoke). Rich state
	// lives on /statusz above.
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = w.Write([]byte("ok\n"))
	})
	return mux
}

func (reg *Registry) handleStatusz(w http.ResponseWriter, _ *http.Request) {
	bridges := reg.snapshot()
	resp := StatusResponse{
		InstanceID:  reg.InstanceID,
		BridgeCount: len(bridges),
		Bridges:     make([]BridgeStatus, 0, len(bridges)),
	}
	for _, b := range bridges {
		resp.Bridges = append(resp.Bridges, BridgeStatus{
			ID:              b.dev.ID,
			TCPPort:         b.tcpPort,
			ClientConnected: b.clientConnected(),
		})
	}
	writeJSON(w, http.StatusOK, resp)
}

func (reg *Registry) handleInventory(w http.ResponseWriter, _ *http.Request) {
	// Snapshot the live set under the lock, then render outside it — reflects the
	// current bridge set even as the rescan loop adds/removes concurrently.
	bridges := reg.snapshot()
	resp := InventoryResponse{InstanceID: reg.InstanceID, Ports: make([]PortInfo, 0, len(bridges))}
	for _, b := range bridges {
		resp.Ports = append(resp.Ports, b.info())
	}
	writeJSON(w, http.StatusOK, resp)
}

func (reg *Registry) handleLine(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	b, ok := reg.lookup(id)
	if !ok {
		writeErr(w, http.StatusNotFound, "no such port: "+id)
		return
	}

	var req lineRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid JSON body: "+err.Error())
		return
	}
	if req.Baud == nil {
		writeErr(w, http.StatusBadRequest, "baud is required")
		return
	}

	// Merge the partial request onto the port's current params so an omitted
	// dataBits/parity/stopBits is preserved.
	want := b.currentLine()
	want.Baud = *req.Baud
	if req.DataBits != nil {
		want.DataBits = *req.DataBits
	}
	if req.Parity != nil {
		want.Parity = *req.Parity
	}
	if req.StopBits != nil {
		want.StopBits = *req.StopBits
	}

	got, err := b.SetLine(want)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "set line params: "+err.Error())
		return
	}
	log.Printf("port %s: line params set to %+v", id, got)
	writeJSON(w, http.StatusOK, got)
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(v); err != nil {
		log.Printf("write json: %v", err)
	}
}

func writeErr(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}
