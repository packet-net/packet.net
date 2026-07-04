namespace Packet.Node.Core.Api;

/// <summary>
/// A peer station's status as learned over the SDM side channel by
/// <c>POST /api/v1/ports/{id}/hail</c> — the read model of a hail reply. Because the side channel
/// is modulation-agnostic, this is returned even when the peer is unreachable on the packet path
/// because of a mode mismatch (the mismatch is exactly what <see cref="Mode"/> then reveals).
/// System.Text.Json's web defaults camel-case the properties.
/// </summary>
/// <param name="Callsign">The peer's callsign.</param>
/// <param name="Mode">The peer's current NinoTNC mode number, or <c>null</c> when it could not be
/// determined.</param>
/// <param name="ModeName">The human name of <see cref="Mode"/> (e.g. <c>1200 AFSK AX.25</c>), or
/// <c>null</c>.</param>
/// <param name="BitRateHz">The peer's current over-air bit rate (bits/s), or <c>null</c>.</param>
/// <param name="Channel">The peer's radio channel as its radio reports it, or <c>null</c>.</param>
/// <param name="SupportedModes">The NinoTNC mode numbers the peer advertises it can run.</param>
/// <param name="Capabilities">Capability tokens the peer advertises (e.g. <c>hail</c>,
/// <c>modecoord</c>, <c>tune</c>).</param>
/// <param name="RssiOfHailDbm">The peer's receiver RSSI (dBm) sampled at hail receipt — the hail's
/// link quality as heard at the far end — or <c>null</c> when not sampled.</param>
public sealed record PortHailStatus(
    string Callsign,
    byte? Mode,
    string? ModeName,
    int? BitRateHz,
    string? Channel,
    IReadOnlyList<byte> SupportedModes,
    IReadOnlyList<string> Capabilities,
    double? RssiOfHailDbm);
