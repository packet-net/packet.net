package main

import (
	"strconv"

	"github.com/grandcat/zeroconf"
)

// mdnsService is the DNS-SD service type PDN browses to discover the head-end
// fleet on a flat LAN. (A manual host:port list is PDN's fallback for routed /
// Tailscale setups where multicast doesn't cross — that lives on the PDN side.)
const mdnsService = "_pdnhead._tcp"

// mdnsDomain is the standard multicast DNS domain.
const mdnsDomain = "local."

// mdnsTXT builds the advertised TXT record. The load-bearing key is the STABLE
// instance id, so PDN can tell instances apart and re-find one whose IP changed.
// httpport echoes the SRV port for clients that read TXT only; v is the schema
// version.
func mdnsTXT(cfg Config) []string {
	return []string{
		"instance=" + cfg.InstanceID,
		"httpport=" + strconv.Itoa(cfg.HTTPPort),
		"v=1",
	}
}

// mdnsRegistration is the resolved set of arguments used to register the
// head-end over DNS-SD. Split out from the network call so the mapping — in
// particular that the human-visible DNS-SD instance LABEL is the instanceId, not
// just a TXT key — is unit-testable without touching the multicast stack.
type mdnsRegistration struct {
	Instance string   // DNS-SD instance label — the instanceId, browse-visible
	Service  string   // service type, mdnsService
	Domain   string   // mDNS domain, mdnsDomain
	Port     int      // SRV port → the HTTP API
	TXT      []string // TXT record (still carries instance=<id>, httpport, v)
}

// buildRegistration maps a Config to its DNS-SD registration. The instanceId is
// used as BOTH the instance label (so it shows up in a `dns-sd -B`/Avahi browse
// and rides any probing responder's §8.1/§9 rename) AND the TXT instance= key
// (PDN's binding key). Pure, for testability.
func buildRegistration(cfg Config) mdnsRegistration {
	return mdnsRegistration{
		Instance: cfg.InstanceID,
		Service:  mdnsService,
		Domain:   mdnsDomain,
		Port:     cfg.HTTPPort,
		TXT:      mdnsTXT(cfg),
	}
}

// advertise registers the head-end over mDNS and returns the server (call
// Shutdown to stop). The SRV port is the HTTP API port, so a PDN browse result
// can hit /inventory directly. Failure is returned, not fatal: main logs and
// continues without mDNS (the manual-address fallback still reaches the daemon).
func advertise(cfg Config) (*zeroconf.Server, error) {
	r := buildRegistration(cfg)
	return zeroconf.Register(
		r.Instance, // service instance name = instanceId (browse-visible label)
		r.Service,
		r.Domain,
		r.Port, // SRV port → the HTTP API
		r.TXT,
		nil, // advertise on all interfaces
	)
}
