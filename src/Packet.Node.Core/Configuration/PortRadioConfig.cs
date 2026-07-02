namespace Packet.Node.Core.Configuration;

/// <summary>
/// Optional per-port <b>radio-control attachment</b> (<c>radio:</c>): the serial
/// control channel to the radio behind this port's modem. When present, the port's
/// transport is wrapped in <c>Packet.Radio.RssiTaggingTransport</c> at bring-up so
/// every inbound frame carries per-frame RSSI/SNR metadata
/// (<c>Ax25InboundFrame.Radio</c>) sampled from the radio's control channel —
/// the signal data standard KISS cannot provide. Null / absent = no radio
/// attached, byte-for-byte today's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The radio control channel is a <em>separate serial port</em> from the modem's
/// (<see cref="Port"/> here vs the transport's <c>device</c>), so this block is only
/// valid on the serial-modem transport kinds (<c>serial-kiss</c>, <c>nino-tnc</c>) —
/// a <c>kiss-tcp</c> / AXUDP port has no physical radio beside it that this node
/// could cable to. Validation enforces that pairing.
/// </para>
/// <para>
/// A radio that fails to open at port start degrades cleanly: the fault is logged
/// and the port runs without radio metadata — an unplugged control cable must never
/// take a working packet channel down. Changing this block is a restart-class
/// config edit (the wrap is a construction-time choice — see
/// <c>Hosting.ReconcilePlanner</c>).
/// </para>
/// </remarks>
public sealed record PortRadioConfig
{
    /// <summary>The radio-control protocol kind — one of <see cref="RadioKinds.Names"/>
    /// (currently only <c>tait-ccdi</c>; CAT / CI-V kinds can join later). Matched
    /// case- and hyphen/underscore-insensitively.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The serial device of the radio's <em>control</em> channel (e.g.
    /// <c>/dev/ttyUSB0</c>) — distinct from the modem's own serial device.</summary>
    public string Port { get; init; } = "";

    /// <summary>Control-channel baud rate. Default 28800 — the Tait CCDI
    /// factory-programming default (<c>TaitCcdiRadio.DefaultBaudRate</c>).</summary>
    public int Baud { get; init; } = 28800;
}

/// <summary>
/// The canonical radio-control <c>kind:</c> strings and their matching rules — the
/// single authority the validator and the <c>Radios.RadioControlFactory</c> both use,
/// so a kind the validator accepted can never fail to resolve at bring-up.
/// </summary>
public static class RadioKinds
{
    /// <summary>Tait TM8100/TM8200 CCDI serial control (<c>Packet.Radio.Tait</c>).</summary>
    public const string TaitCcdi = "tait-ccdi";

    /// <summary>The recognised kind names (for the validator's error message + docs).</summary>
    public static IReadOnlyList<string> Names { get; } = [TaitCcdi];

    /// <summary>True if <paramref name="kind"/> names a known radio-control kind.
    /// Unlike an optional preset name, null/empty is NOT valid here — a radio block
    /// must say what protocol its control channel speaks.</summary>
    public static bool IsKnown(string? kind) => Is(kind, TaitCcdi);

    /// <summary>Case- and hyphen/underscore-insensitive kind comparison
    /// (<c>tait-ccdi</c> == <c>TaitCcdi</c> == <c>TAIT_CCDI</c>), matching the
    /// transport-kind and compat-preset conventions.</summary>
    public static bool Is(string? kind, string canonical) =>
        !string.IsNullOrWhiteSpace(kind) &&
        string.Equals(Normalise(kind), Normalise(canonical), StringComparison.Ordinal);

    private static string Normalise(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal)
           .Replace("_", "", StringComparison.Ordinal)
           .ToLowerInvariant();
}
