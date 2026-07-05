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

// advertise registers the head-end over mDNS and returns the server (call
// Shutdown to stop). The SRV port is the HTTP API port, so a PDN browse result
// can hit /inventory directly. Failure is returned, not fatal: main logs and
// continues without mDNS (the manual-address fallback still reaches the daemon).
func advertise(cfg Config) (*zeroconf.Server, error) {
	return zeroconf.Register(
		cfg.InstanceID, // service instance name
		mdnsService,
		mdnsDomain,
		cfg.HTTPPort, // SRV port → the HTTP API
		mdnsTXT(cfg),
		nil, // advertise on all interfaces
	)
}
