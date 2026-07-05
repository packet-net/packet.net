package main

import (
	"encoding/json"
	"log"
	"net/http"
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

// lineRequest is the POST /ports/{id}/line body. Fields are pointers so a
// partial request (the common baud-only sweep call) leaves the other params
// unchanged; baud is required.
type lineRequest struct {
	Baud     *int    `json:"baud"`
	DataBits *int    `json:"dataBits"`
	Parity   *string `json:"parity"`
	StopBits *int    `json:"stopBits"`
}

// Registry is the set of live bridges the API serves over, plus the instance id.
type Registry struct {
	InstanceID string
	Bridges    []*Bridge // discovery order == inventory order == TCP-port order
	byID       map[string]*Bridge
}

func newRegistry(instanceID string, bridges []*Bridge) *Registry {
	byID := make(map[string]*Bridge, len(bridges))
	for _, b := range bridges {
		byID[b.dev.ID] = b
	}
	return &Registry{InstanceID: instanceID, Bridges: bridges, byID: byID}
}

// handler wires the machine API. Routing uses net/http method+wildcard patterns
// (Go 1.22+) — no external router dependency.
func (reg *Registry) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /inventory", reg.handleInventory)
	mux.HandleFunc("POST /ports/{id}/line", reg.handleLine)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = w.Write([]byte("ok\n"))
	})
	return mux
}

func (reg *Registry) handleInventory(w http.ResponseWriter, _ *http.Request) {
	resp := InventoryResponse{InstanceID: reg.InstanceID, Ports: make([]PortInfo, 0, len(reg.Bridges))}
	for _, b := range reg.Bridges {
		resp.Ports = append(resp.Ports, b.info())
	}
	writeJSON(w, http.StatusOK, resp)
}

func (reg *Registry) handleLine(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	b, ok := reg.byID[id]
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
