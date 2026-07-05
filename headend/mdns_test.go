package main

import "testing"

// TestBuildRegistration_UsesInstanceIDAsLabel pins the deconfliction-critical
// mapping: the instanceId is advertised as the DNS-SD instance LABEL (browse-
// visible, and the field a probing responder would rename), not only inside TXT.
func TestBuildRegistration_UsesInstanceIDAsLabel(t *testing.T) {
	cfg := Config{InstanceID: "shack-north", HTTPPort: 7300}
	reg := buildRegistration(cfg)

	if reg.Instance != "shack-north" {
		t.Errorf("registration instance label = %q, want shack-north (== instanceId)", reg.Instance)
	}
	if reg.Service != mdnsService {
		t.Errorf("service = %q, want %q", reg.Service, mdnsService)
	}
	if reg.Domain != mdnsDomain {
		t.Errorf("domain = %q, want %q", reg.Domain, mdnsDomain)
	}
	if reg.Port != 7300 {
		t.Errorf("SRV port = %d, want 7300 (the HTTP API port)", reg.Port)
	}

	// TXT still carries instance=<id> (PDN's binding key), plus httpport + v.
	want := map[string]bool{"instance=shack-north": false, "httpport=7300": false, "v=1": false}
	for _, kv := range reg.TXT {
		if _, ok := want[kv]; ok {
			want[kv] = true
		}
	}
	for kv, seen := range want {
		if !seen {
			t.Errorf("TXT missing %q (got %v)", kv, reg.TXT)
		}
	}
}
