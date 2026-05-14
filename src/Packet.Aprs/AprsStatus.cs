namespace Packet.Aprs;

/// <summary>
/// A decoded APRS status report (DTI <c>&gt;</c>) per APRS101 §16.
/// </summary>
/// <param name="Timestamp">
/// 7-byte DHM-zulu timestamp string (<c>DDHHMMz</c>) if present, else
/// <c>null</c>. APRS101 §16 explicitly restricts status timestamps to
/// DHM-zulu — no DHM-local, no HMS, no MDHM.
/// </param>
/// <param name="Text">
/// The free-form status text. Up to 62 ASCII characters without a
/// timestamp, or 55 with. May contain any printable ASCII except
/// <c>|</c> or <c>~</c> per spec, though we don't enforce that on
/// receive (Postel).
/// </param>
public readonly record struct AprsStatus(
    string? Timestamp,
    string Text);
