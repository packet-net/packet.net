namespace Packet.Node.Core.Configuration;

/// <summary>
/// Optional per-port <b>radio-control attachment</b> (<c>radio:</c>): the control channel
/// to the radio behind this port's modem. When present and the radio can report signal
/// strength, the port's transport is wrapped in <c>Packet.Radio.RssiTaggingTransport</c> at
/// bring-up so every inbound frame carries per-frame RSSI/SNR metadata
/// (<c>Ax25InboundFrame.Radio</c>) sampled from the radio's control channel —
/// the signal data standard KISS cannot provide — and hardware carrier-sense (DCD),
/// where available, gates the listener's CSMA. Null / absent = no radio
/// attached, byte-for-byte today's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which fields apply depends on <see cref="Kind"/>.</b> For <c>tait-ccdi</c> the control
/// channel is a <em>separate serial port</em> from the modem's (<see cref="Port"/> here vs the
/// transport's <c>device</c>), so the binding-mode fields (<see cref="Port"/> /
/// <see cref="Serial"/> / <see cref="HeadEndId"/>+<see cref="DeviceId"/>), <see cref="Baud"/> and
/// the Tait extras (<see cref="HealthIntervalSeconds"/>, the hail responder) all apply — and the
/// block is only valid on a transport with a co-located radio (<c>serial-kiss</c> /
/// <c>nino-tnc</c> locally, <c>nino-tnc-tcp</c> for a head-end binding). For <c>rig</c> the port's
/// <c>rig:</c> block (which must also be present) says which CAT daemon to dial — the node opens a
/// SECOND, dedicated connection to it and re-presents the rig's DCD / signal strength / PTT as the
/// port's radio, so the binding-mode fields must stay unset and no transport pairing applies (a
/// <c>kiss-tcp</c> soundmodem beside <c>rigctld</c> is the motivating case).
/// </para>
/// <para>
/// Pin a <c>tait-ccdi</c> radio EITHER by device path (<see cref="Port"/>) OR by CCDI serial
/// (<see cref="Serial"/>) — exactly one. <see cref="Serial"/> is the robust choice: USB serial
/// devices renumber across a replug or reboot (<c>/dev/ttyUSB0</c> ↔ <c>/dev/ttyUSB1</c>), and the
/// CP2102 CCDI dongles in the wild share a USB serial, so <c>/dev/serial/by-id</c> can't tell two
/// apart — the CCDI serial number can. A <see cref="Serial"/>-bound radio is located by scanning at
/// bring-up; no match (unplugged / powered off) degrades cleanly, exactly like a failed open.
/// </para>
/// <para>
/// A radio that fails to open at port start degrades cleanly: the fault is logged
/// and the port runs without radio metadata — an unplugged control cable (or an
/// unreachable rig daemon) must never take a working packet channel down. Changing
/// this block is a restart-class config edit (the wrap is a construction-time
/// choice — see <c>Hosting.ReconcilePlanner</c>).
/// </para>
/// </remarks>
public sealed record PortRadioConfig
{
    /// <summary>The radio-control protocol kind — one of <see cref="RadioKinds.Names"/>
    /// (<c>tait-ccdi</c> = Tait CCDI serial control; <c>rig</c> = the port's <c>rig:</c> CAT
    /// daemon re-presented as the radio, over a dedicated second connection). Matched
    /// case- and hyphen/underscore-insensitively.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The serial device of the radio's <em>control</em> channel (e.g.
    /// <c>/dev/ttyUSB0</c>) — distinct from the modem's own serial device. Mutually exclusive with
    /// <see cref="Serial"/>: set exactly one. Empty/absent when binding by <see cref="Serial"/>.</summary>
    public string Port { get; init; } = "";

    /// <summary>The radio's CCDI serial number (e.g. <c>1G000123</c>) — the <b>stable</b> way to pin
    /// which radio this port controls, surviving <c>/dev/ttyUSB*</c> renumbering and the shared-USB-
    /// serial ambiguity of CP2102 dongles. When set, bring-up scans the machine's candidate ports and
    /// opens whichever one answers a MODEL/serial query with this CCDI serial. One of the three
    /// mutually-exclusive binding modes (<see cref="Port"/> / <see cref="Serial"/> /
    /// <see cref="HeadEndId"/>+<see cref="DeviceId"/>): set exactly one.</summary>
    public string Serial { get; init; } = "";

    /// <summary>
    /// <b>Head-end binding</b> (split-station topology — see
    /// <c>docs/research/split-station-rf-headend.md</c>): the <see cref="HeadEndConfig.Id"/> of the
    /// head-end hosting this radio's CCDI control channel, paired with <see cref="DeviceId"/>. When
    /// set, the radio is opened over TCP (<c>TaitCcdiRadio.OpenTcp</c>) against the head-end's raw
    /// pipe instead of a local serial port — RSSI/SNR, DCD, tuning and SDM all work remotely. The
    /// co-located modem must be the same head-end's <c>nino-tnc-tcp</c> transport (the modem+radio
    /// pair is always on one instance). This is a third binding mode, mutually exclusive with
    /// <see cref="Port"/> and <see cref="Serial"/>; requires <see cref="DeviceId"/> too.
    /// </summary>
    public string HeadEndId { get; init; } = "";

    /// <summary>The stable device id (the inventory <c>id</c>) of the Tait CCDI control port on the
    /// head-end named by <see cref="HeadEndId"/>. Required with, and only with, <see cref="HeadEndId"/>.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>True when this radio is bound to a head-end device (both <see cref="HeadEndId"/> and
    /// <see cref="DeviceId"/> set) rather than a local serial port. The single authority the validator
    /// and the radio factory both consult, so a binding the validator accepted always resolves the
    /// same way at bring-up.</summary>
    public bool IsHeadEndBound =>
        !string.IsNullOrWhiteSpace(HeadEndId) && !string.IsNullOrWhiteSpace(DeviceId);

    /// <summary>Control-channel baud rate. Default 28800 — the Tait CCDI
    /// factory-programming default (<c>TaitCcdiRadio.DefaultBaudRate</c>).</summary>
    public int Baud { get; init; } = 28800;

    /// <summary>How often (seconds) the per-port radio-health monitor samples the radio (RSSI, PA
    /// temperature, forward/reverse detector trend). Null uses the driver default (10 s). Must be
    /// positive when set. Cheap — a sample is a few ~15 ms CCDI transactions.</summary>
    public int? HealthIntervalSeconds { get; init; }

    /// <summary>
    /// Opt-in: arm this port as an <b>SDM hail responder</b>. When <c>true</c>, the node listens on
    /// the radio's SDM side channel for hails from <see cref="HailResponderPeer"/> and auto-replies
    /// with this station's status (callsign, current NinoTNC mode + bitrate, channel, capabilities).
    /// Off by default — a station never advertises its state without an explicit enable. Because a
    /// reply is an SDM transmission, arm it only where transmitting is acceptable. Requires a
    /// <see cref="HailResponderPeer"/> to reply to.
    /// </summary>
    public bool HailResponder { get; init; }

    /// <summary>
    /// The 8-character SDM data identity of the neighbour this port answers hails from (and replies
    /// to) when <see cref="HailResponder"/> is on. Required when the responder is armed. (v1 is
    /// point-to-point, mirroring the mode-coordination link; answering arbitrary hailers by routing
    /// to the RING caller id is a documented follow-up.)
    /// </summary>
    public string HailResponderPeer { get; init; } = "";
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

    /// <summary>The port's <c>rig:</c> CAT daemon re-presented as the radio
    /// (<c>Packet.Radio.RigRadioControl</c> over a dedicated second connection to the
    /// same rigctld/flrig the status poller uses).</summary>
    public const string Rig = "rig";

    /// <summary>The recognised kind names (for the validator's error message + docs).</summary>
    public static IReadOnlyList<string> Names { get; } = [TaitCcdi, Rig];

    /// <summary>True if <paramref name="kind"/> names a known radio-control kind.
    /// Unlike an optional preset name, null/empty is NOT valid here — a radio block
    /// must say what protocol its control channel speaks.</summary>
    public static bool IsKnown(string? kind) => Is(kind, TaitCcdi) || Is(kind, Rig);

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
