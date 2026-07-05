namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// Browses the LAN for split-station RF head-ends advertising <c>_pdnhead._tcp</c> (see
/// <c>docs/research/split-station-rf-headend.md</c>). The seam is deliberately thin — one bounded,
/// one-shot browse — so the address resolver, the remote radio scanner, and the tests all consume
/// discovery through a substitutable interface (a fake in tests; the real Zeroconf-backed
/// <see cref="MdnsHeadEndDiscovery"/> in the node).
/// </summary>
/// <remarks>
/// A configured <see cref="Configuration.HeadEndConfig.Address"/> (non-empty) is the manual
/// fallback and is used <b>directly</b>, never requiring an mDNS hit — discovery only fills the
/// address of head-ends whose config address is blank (the flat-LAN "plug in and go" path). The
/// authoritative instance id is the TXT <c>instance=</c> value, regardless of the DNS-SD label.
/// </remarks>
public interface IHeadEndDiscovery
{
    /// <summary>Browse <c>_pdnhead._tcp</c> for up to <paramref name="timeout"/> and return every
    /// advertiser found (deduplicated is NOT guaranteed — the same instance id can legitimately or
    /// illegitimately appear at more than one address; the caller decides what a duplicate means).
    /// Total: a box with no multicast, no responder, or a browse failure yields an empty list, never
    /// an exception.</summary>
    Task<IReadOnlyList<DiscoveredHeadEnd>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// One head-end found by an mDNS browse: its stable <see cref="InstanceId"/> (TXT <c>instance=</c>),
/// the <see cref="Host"/> to dial, and the <see cref="HttpPort"/> of its inventory / line-control
/// HTTP API. PDN keys bindings by <see cref="InstanceId"/>, resolving it to this address at bring-up.
/// </summary>
public sealed record DiscoveredHeadEnd(string InstanceId, string Host, int HttpPort)
{
    /// <summary>The <c>host:port</c> of the HTTP control plane (what a manual
    /// <see cref="Configuration.HeadEndConfig.Address"/> would hold) — the conflict/dedup key.</summary>
    public string Address => $"{Host}:{HttpPort}";
}
