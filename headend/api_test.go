package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// testRegistry builds a registry over bridges wired to fake serials, without
// opening any TCP listeners (the API never touches the listener), so the HTTP
// surface can be exercised in isolation.
func testRegistry() (*Registry, *fakeSerial) {
	fake := newFakeSerial(defaultLine(9600))
	b0 := &Bridge{
		dev: DiscoveredPort{
			ID: "usb-NinoTNC_TARPN-if00", DevPath: "/dev/ttyACM0",
			ByID: "/dev/serial/by-id/usb-NinoTNC_TARPN-if00", USBVid: "04d8", USBPid: "000a",
		},
		tcpPort: 7301, port: fake, line: defaultLine(9600),
	}
	b1 := &Bridge{
		dev: DiscoveredPort{
			ID: "usb-FTDI_Tait-if00-port0", DevPath: "/dev/ttyUSB0",
			ByID: "/dev/serial/by-id/usb-FTDI_Tait-if00-port0", USBVid: "0403", USBPid: "6001",
		},
		tcpPort: 7302, port: newFakeSerial(defaultLine(9600)), line: defaultLine(9600),
	}
	return newRegistry("pi-shack", []*Bridge{b0, b1}), fake
}

func TestInventoryShape(t *testing.T) {
	reg, _ := testRegistry()
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, httptest.NewRequest("GET", "/inventory", nil))

	if rr.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rr.Code)
	}
	if ct := rr.Header().Get("Content-Type"); !strings.HasPrefix(ct, "application/json") {
		t.Errorf("Content-Type = %q, want application/json", ct)
	}

	var inv InventoryResponse
	if err := json.Unmarshal(rr.Body.Bytes(), &inv); err != nil {
		t.Fatalf("decode inventory: %v\nbody: %s", err, rr.Body.String())
	}
	if inv.InstanceID != "pi-shack" {
		t.Errorf("instanceId = %q, want pi-shack", inv.InstanceID)
	}
	if len(inv.Ports) != 2 {
		t.Fatalf("ports = %d, want 2", len(inv.Ports))
	}
	p := inv.Ports[0]
	if p.ID != "usb-NinoTNC_TARPN-if00" || p.DevPath != "/dev/ttyACM0" || p.TCPPort != 7301 {
		t.Errorf("port[0] = %+v", p)
	}
	if p.USBVid != "04d8" || p.USBPid != "000a" || p.ByID == "" {
		t.Errorf("port[0] usb hints/byId missing: %+v", p)
	}
	if p.Baud != 9600 || p.DataBits != 8 || p.Parity != "none" || p.StopBits != 1 {
		t.Errorf("port[0] line params = %d %d %s %d, want 9600 8 none 1", p.Baud, p.DataBits, p.Parity, p.StopBits)
	}

	// Verify the exact JSON keys Stage 3 (PDN scanner) will bind to.
	var raw map[string]any
	_ = json.Unmarshal(rr.Body.Bytes(), &raw)
	if _, ok := raw["instanceId"]; !ok {
		t.Errorf("missing top-level key instanceId")
	}
	ports, _ := raw["ports"].([]any)
	if len(ports) == 0 {
		t.Fatal("missing ports array")
	}
	first, _ := ports[0].(map[string]any)
	for _, key := range []string{"id", "devPath", "usbVid", "usbPid", "byId", "tcpPort", "baud", "dataBits", "parity", "stopBits"} {
		if _, ok := first[key]; !ok {
			t.Errorf("port JSON missing key %q", key)
		}
	}
}

func TestLineControl_MergesPartialRequest(t *testing.T) {
	reg, fake := testRegistry()

	body := bytes.NewBufferString(`{"baud":19200}`)
	req := httptest.NewRequest("POST", "/ports/usb-NinoTNC_TARPN-if00/line", body)
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body: %s", rr.Code, rr.Body.String())
	}
	var got LineParams
	if err := json.Unmarshal(rr.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	// baud changed; dataBits/parity/stopBits preserved from current (8N1).
	if got.Baud != 19200 || got.DataBits != 8 || got.Parity != "none" || got.StopBits != 1 {
		t.Errorf("response params = %+v, want baud 19200 + 8N1 preserved", got)
	}
	if fake.setCalls != 1 || fake.line.Baud != 19200 {
		t.Errorf("device not reconfigured: calls=%d line=%+v", fake.setCalls, fake.line)
	}
}

func TestLineControl_FullParams(t *testing.T) {
	reg, fake := testRegistry()
	req := httptest.NewRequest("POST", "/ports/usb-NinoTNC_TARPN-if00/line",
		bytes.NewBufferString(`{"baud":28800,"dataBits":7,"parity":"even","stopBits":2}`))
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body: %s", rr.Code, rr.Body.String())
	}
	if fake.line.Baud != 28800 || fake.line.DataBits != 7 || fake.line.Parity != "even" || fake.line.StopBits != 2 {
		t.Errorf("device line = %+v, want 28800 7E2", fake.line)
	}
}

func TestLineControl_UnknownPort404(t *testing.T) {
	reg, _ := testRegistry()
	req := httptest.NewRequest("POST", "/ports/nope/line", bytes.NewBufferString(`{"baud":9600}`))
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", rr.Code)
	}
}

func TestLineControl_MissingBaud400(t *testing.T) {
	reg, _ := testRegistry()
	req := httptest.NewRequest("POST", "/ports/usb-NinoTNC_TARPN-if00/line", bytes.NewBufferString(`{"parity":"even"}`))
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400 (baud required)", rr.Code)
	}
}

func TestLineControl_BadStopBits400(t *testing.T) {
	reg, _ := testRegistry()
	req := httptest.NewRequest("POST", "/ports/usb-NinoTNC_TARPN-if00/line",
		bytes.NewBufferString(`{"baud":9600,"stopBits":5}`))
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400 (invalid stopBits)", rr.Code)
	}
}

func TestHealthz(t *testing.T) {
	reg, _ := testRegistry()
	rr := httptest.NewRecorder()
	reg.handler().ServeHTTP(rr, httptest.NewRequest("GET", "/healthz", nil))
	if rr.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rr.Code)
	}
	if strings.TrimSpace(rr.Body.String()) != "ok" {
		t.Errorf("body = %q, want ok", rr.Body.String())
	}
}
