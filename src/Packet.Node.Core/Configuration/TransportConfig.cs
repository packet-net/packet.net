namespace Packet.Node.Core.Configuration;

/// <summary>
/// How a port reaches its KISS modem — a closed, discriminated union. Each
/// concrete subtype is one transport kind; the <see cref="Kind"/> string is the
/// discriminator the YAML layer keys on (the <c>kind:</c> field) and the value
/// the <see cref="Transports.ITransportFactory"/> switches over.
/// </summary>
/// <remarks>
/// <para>
/// Modelled as an abstract record with sealed subtypes (C# has no native DU).
/// The set is closed: a new transport adds a subtype here, a kind constant, a
/// validator arm, a YAML mapping arm, and a factory arm — and the compiler's
/// exhaustiveness checking flags any arm you miss.
/// </para>
/// <para>
/// AXUDP (AX.25 frames over UDP, <see cref="AxudpTransport"/>) plugs into the
/// same seam via the <c>AxudpFrameTransport</c> adapter, which presents a
/// <see cref="Packet.Axudp.AxudpSocket"/> as a native <c>IAx25Transport</c> — so
/// the listener / console / reconcile path is shared with the KISS transports.
/// The telnet console is <b>not</b> a transport (it is not an
/// <c>IAx25Transport</c>); it lives under <see cref="ManagementConfig.Telnet"/>.
/// </para>
/// </remarks>
public abstract record TransportConfig
{
    private protected TransportConfig() { }

    /// <summary>The discriminator value (the YAML <c>kind:</c>). One of the
    /// constants on <see cref="TransportKinds"/>.</summary>
    public abstract string Kind { get; }

    /// <summary>A short, human-readable description of the transport endpoint —
    /// used for log lines and the duplicate-endpoint validation key.</summary>
    public abstract string DescribeEndpoint();
}

/// <summary>The canonical <c>kind:</c> discriminator strings.</summary>
public static class TransportKinds
{
    /// <summary>Generic serial-port KISS modem.</summary>
    public const string SerialKiss = "serial-kiss";

    /// <summary>NinoTNC over serial (KISS + a mode catalogue).</summary>
    public const string NinoTnc = "nino-tnc";

    /// <summary>Full-control NinoTNC over a split-station head-end's raw TCP pipe (GETVER / mode /
    /// GETRSSI / ACKMODE) — distinct from the control-less generic <see cref="KissTcp"/>.</summary>
    public const string NinoTncTcp = "nino-tnc-tcp";

    /// <summary>KISS over TCP (a softmodem / net-sim endpoint).</summary>
    public const string KissTcp = "kiss-tcp";

    /// <summary>AX.25 frames encapsulated in UDP datagrams (AXUDP / BPQAXIP).</summary>
    public const string Axudp = "axudp";

    /// <summary>Multipoint AXUDP — one UDP socket, many partners addressed by callsign
    /// (the BPQ <c>BPQAXIP</c> + <c>MAP</c> model).</summary>
    public const string AxudpMultipoint = "axudp-multipoint";

    /// <summary>A Tait TM8100/TM8200 radio in Transparent mode as the modem — no external TNC
    /// (AX.25 over the radio's own FFSK byte pipe with KISS SLIP framing).</summary>
    public const string TaitTransparent = "tait-transparent";

    /// <summary>An in-process soundcard modem (the pdn-soundmodem engine): audio in/out via
    /// ALSA, no external TNC or daemon — with native carrier-sense into the AX.25 stack.</summary>
    public const string SoundModem = "soundmodem";
}

/// <summary>A generic serial-port KISS modem (<c>KissSerialModem.Open</c>).</summary>
public sealed record SerialKissTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.SerialKiss;

    /// <summary>The serial device path (e.g. <c>/dev/ttyACM0</c> or <c>COM6</c>).</summary>
    public required string Device { get; init; }

    /// <summary>Serial baud rate.</summary>
    public int Baud { get; init; } = 57600;

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"serial-kiss:{Device}";
}

/// <summary>A NinoTNC over serial (<c>NinoTncSerialPort.Open</c> + <c>SetModeAsync</c>).</summary>
public sealed record NinoTncTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.NinoTnc;

    /// <summary>The serial device path the NinoTNC enumerates as.</summary>
    public required string Device { get; init; }

    /// <summary>Serial baud rate.</summary>
    public int Baud { get; init; } = 57600;

    /// <summary>NinoTNC mode 0..15 (the modem mode catalogue index).</summary>
    public int Mode { get; init; }

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"nino-tnc:{Device}";
}

/// <summary>
/// A <b>full-control</b> NinoTNC hosted on a split-station head-end and reached over its raw TCP
/// pipe (<c>NinoTncSerialPort.OpenTcp</c> — see <c>docs/research/split-station-rf-headend.md</c>).
/// Unlike the generic control-less <see cref="KissTcpTransport"/>, this is the NinoTNC's whole
/// surface remotely: GETVER, mode agility (<see cref="Mode"/>), GETRSSI, ACKMODE TX-completion. The
/// TNC is bound by <c>(headEndId, deviceId)</c>, resolved to the head-end's <c>tcpPort</c> at
/// bring-up (a re-addressed head-end keeps this config — the id is the key, not host:port).
/// </summary>
/// <remarks>
/// NinoTNC baud is fictional over USB-CDC, so there is no baud field (nothing for the head-end's
/// line verb to set). A head-end-hosted Tait <c>radio:</c> control channel — the co-located pair —
/// is legitimate on this kind (validation lifts the serial-only radio restriction specifically for
/// head-end-bound ports).
/// </remarks>
public sealed record NinoTncTcpTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.NinoTncTcp;

    /// <summary>The <see cref="HeadEndConfig.Id"/> of the head-end hosting this NinoTNC.</summary>
    public required string HeadEndId { get; init; }

    /// <summary>The stable device id (inventory <c>id</c>) of the NinoTNC on that head-end.</summary>
    public required string DeviceId { get; init; }

    /// <summary>NinoTNC mode 0..15 (the modem mode catalogue index), applied via KISS SETHW after
    /// the remote port opens — exactly as the local <see cref="NinoTncTransport.Mode"/> path does.</summary>
    public int Mode { get; init; }

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"nino-tnc-tcp:{HeadEndId}/{DeviceId}";
}

/// <summary>KISS over TCP (<c>KissTcpClient.ConnectAsync</c>) — a softmodem or net-sim.</summary>
public sealed record KissTcpTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.KissTcp;

    /// <summary>Host of the KISS-over-TCP listener.</summary>
    public required string Host { get; init; }

    /// <summary>TCP port of the KISS-over-TCP listener.</summary>
    public required int Port { get; init; }

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"kiss-tcp:{Host}:{Port}";
}

/// <summary>
/// AXUDP — AX.25 frames encapsulated in UDP datagrams (the RFC-1226 AXIP/AXUDP /
/// BPQAXIP transport). Each datagram payload is one AX.25 frame body (the same
/// KISS-form octets the listener produces) followed by the 2-octet AX.25 FCS.
/// The FCS is unconditional — it is the de-facto wire form that every real peer
/// (LinBPQ's BPQAXIP, XRouter, ax25ipd, JNOS, per RFC 1226) requires; see
/// <c>docs/strict-vs-pragmatic-audit.md</c>. Driven by the <c>AxudpFrameTransport</c>
/// adapter over a <see cref="Packet.Axudp.AxudpSocket"/>.
/// </summary>
/// <remarks>
/// AXUDP is a point-to-point UDP tunnel, not a shared RF channel: a port sends
/// every outbound frame to one configured remote (<see cref="Host"/>:<see cref="Port"/>)
/// and receives on its own bound <see cref="LocalPort"/>. There is no CSMA on a
/// UDP link, so the KISS TXDELAY/PERSIST/SLOTTIME knobs are inert for this kind
/// (the adapter accepts and ignores them — see <c>AxudpFrameTransport</c>).
/// </remarks>
public sealed record AxudpTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.Axudp;

    /// <summary>Hostname / IP of the remote AXUDP peer to send frames to.</summary>
    public required string Host { get; init; }

    /// <summary>UDP port of the remote AXUDP peer.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// Local UDP port to bind for receiving inbound datagrams. Conventionally the
    /// same as the remote's port for a symmetric tunnel; <c>0</c> picks an
    /// ephemeral port (send-only / monitor — the peer can't reach an ephemeral
    /// bind unless it learns the source port).
    /// </summary>
    public int LocalPort { get; init; }

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"axudp:{Host}:{Port}(local:{LocalPort})";
}

/// <summary>
/// Multipoint AXUDP — the BPQ <c>BPQAXIP</c> analog. ONE UDP socket bound to
/// <see cref="LocalPort"/> reaches MANY partners, each addressed by
/// <c>callsign → host:port</c> (BPQ's <c>MAP &lt;call&gt; &lt;ip&gt; UDP &lt;port&gt;</c>),
/// with a per-peer <see cref="AxudpPeerConfig.Broadcast"/> flag (the BPQ <c>B</c> suffix —
/// fan NODES/ID/BEACON broadcasts to that peer). The wire form is identical to point-to-point
/// <see cref="AxudpTransport"/> (AX.25 body + the mandatory 2-octet FCS); only the addressing
/// model differs. Driven by the <c>AxudpMultipointFrameTransport</c> adapter over a
/// <see cref="Packet.Axudp.AxudpMultipointSocket"/>.
/// </summary>
/// <remarks>
/// Outbound frames route by destination callsign to the matching peer; a NODES/ID/BEACON
/// broadcast (or any UI to an unmapped pseudo-destination) fans out to every
/// <c>broadcast: true</c> peer. Inbound datagrams are accepted from any sender on
/// <see cref="LocalPort"/> and routed up by callsign by the listener. As with point-to-point
/// AXUDP there is no CSMA on a UDP link, so the KISS TXDELAY/PERSIST/SLOTTIME knobs are inert.
/// </remarks>
public sealed record AxudpMultipointTransport : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.AxudpMultipoint;

    /// <summary>Local UDP port to bind for the one shared socket (send + receive for every
    /// peer). Conventionally a fixed well-known port so partners can MAP back to us.</summary>
    public required int LocalPort { get; init; }

    /// <summary>The partner table — each entry maps a callsign to a UDP endpoint
    /// (BPQ <c>MAP</c>). Empty is allowed (a receive-only listener) but unusual.</summary>
    public IReadOnlyList<AxudpPeerConfig> Peers { get; init; } = [];

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"axudp-multipoint:local:{LocalPort}({Peers.Count} peers)";

    // Peers is a collection member, which a record compares by REFERENCE — so a YAML
    // round-trip (fresh list) would read as changed and needlessly restart the port.
    // Hand-roll value equality over it, matching every other config record with a
    // collection member (see ConfigEquality). AxudpPeerConfig is a plain record (scalar
    // members only), so its own value equality drives the per-element comparison.
    public bool Equals(AxudpMultipointTransport? other) =>
        other is not null
        && LocalPort == other.LocalPort
        && ConfigEquality.ListEqual(Peers, other.Peers);

    public override int GetHashCode() => HashCode.Combine(LocalPort, ConfigEquality.ListHash(Peers));
}

/// <summary>
/// One multipoint-AXUDP partner (a BPQ <c>MAP &lt;call&gt; &lt;ip&gt; UDP &lt;port&gt; [B]</c>
/// line): the partner callsign, its host:port endpoint, and whether NODES/ID broadcasts fan
/// out to it.
/// </summary>
public sealed record AxudpPeerConfig
{
    /// <summary>The partner callsign (the routing key — an outbound frame whose AX.25
    /// destination is this callsign goes to this peer's endpoint).</summary>
    public required string Call { get; init; }

    /// <summary>Hostname / IP of the partner's AXUDP endpoint.</summary>
    public required string Host { get; init; }

    /// <summary>UDP port of the partner's AXUDP endpoint.</summary>
    public required int Port { get; init; }

    /// <summary>Whether NODES / ID / BEACON broadcasts fan out to this peer (BPQ's <c>B</c>
    /// suffix on a <c>MAP</c> line). Default false — a non-broadcast peer only ever receives
    /// frames whose AX.25 destination is its own callsign.</summary>
    public bool Broadcast { get; init; }
}

/// <summary>
/// A Tait TM8100/TM8200 radio in Transparent mode as the port's modem — <b>no external TNC</b>.
/// AX.25 frames ride the radio's internal FFSK modem (an 8-bit-clean byte pipe) with KISS SLIP
/// framing, driven by <c>Packet.Radio.Tait.TaitTransparentTransport</c>. The radio is bound by
/// device path (<see cref="Device"/>) OR — preferred — by CCDI serial (<see cref="Serial"/>),
/// resolved at bring-up so a re-enumerated <c>/dev/ttyUSB*</c> still finds the right radio; OR by
/// a split-station head-end device (<see cref="HeadEndId"/>+<see cref="DeviceId"/>, #585), in
/// which case the byte pipe is the head-end's raw TCP bridge and the Command↔Transparent runtime
/// re-clock rides the head-end's <c>POST /ports/{id}/line</c> verb. Exactly one binding mode.
/// </summary>
/// <remarks>
/// Unlike a serial-modem port with an optional <c>radio:</c> control channel, here the radio IS
/// the modem: there is no separate CCDI control channel while in Transparent mode, so this kind
/// provides no per-frame RSSI/SNR (only airtime timing). Teardown exits Transparent and restores
/// Command mode — a port left in Transparent is deaf to CCDI. A <c>radio:</c> block is therefore
/// invalid on this kind (validation rejects it).
/// </remarks>
public sealed record TaitTransparentTransportConfig : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.TaitTransparent;

    /// <summary>The radio's serial device path (e.g. <c>/dev/ttyUSB0</c>). One of the three
    /// mutually-exclusive binding modes (<see cref="Device"/> / <see cref="Serial"/> /
    /// <see cref="HeadEndId"/>+<see cref="DeviceId"/>): set exactly one.</summary>
    public string Device { get; init; } = "";

    /// <summary>The radio's CCDI serial number (e.g. <c>1G000123</c>) — the stable binding that
    /// survives <c>/dev/ttyUSB*</c> renumbering and the shared-USB-serial CP2102 dongle ambiguity.
    /// When set, bring-up scans for the radio answering with this serial. One of the three
    /// mutually-exclusive binding modes: set exactly one.</summary>
    public string Serial { get; init; } = "";

    /// <summary>
    /// <b>Head-end binding</b> (split-station topology, #585): the <see cref="HeadEndConfig.Id"/>
    /// of the head-end hosting this radio's serial port, paired with <see cref="DeviceId"/>. When
    /// set, the transport dials the head-end's raw TCP pipe instead of opening a local serial
    /// port, with the CCDI command baud and any Command↔Transparent runtime re-clock routed
    /// through the head-end's line verb (the data socket is a pure binary pipe and cannot carry
    /// line rate). The third mutually-exclusive binding mode; requires <see cref="DeviceId"/> too.
    /// </summary>
    public string HeadEndId { get; init; } = "";

    /// <summary>The stable device id (the inventory <c>id</c>) of the radio's serial port on the
    /// head-end named by <see cref="HeadEndId"/>. Required with, and only with,
    /// <see cref="HeadEndId"/>.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>True when this transport is bound to a head-end device (both
    /// <see cref="HeadEndId"/> and <see cref="DeviceId"/> set) rather than a local serial port /
    /// CCDI serial. The single authority the validator, the factory and the port supervisor all
    /// consult — mirrors <see cref="PortRadioConfig.IsHeadEndBound"/>.</summary>
    public bool IsHeadEndBound =>
        !string.IsNullOrWhiteSpace(HeadEndId) && !string.IsNullOrWhiteSpace(DeviceId);

    /// <summary>CCDI Command-mode serial baud (the rate the Transparent enter/exit commands use).
    /// Default 28800 — the Tait factory default (<c>TaitCcdiRadio.DefaultBaudRate</c>).</summary>
    public int Baud { get; init; } = 28800;

    /// <summary>Transparent-mode terminal baud. When it differs from <see cref="Baud"/> the port
    /// is re-clocked on enter and restored on exit; equal (the common case) needs no switch.
    /// Default 28800.</summary>
    public int TransparentBaud { get; init; } = 28800;

    /// <summary>FFSK over-air baud used to estimate frame airtime. Default 2400 (the TM8110 FFSK
    /// modem raw rate); effective throughput is lower, so airtime is a floor estimate.</summary>
    public int FfskBaud { get; init; } = 2400;

    /// <summary>Modelled transmit lead-in in milliseconds (radio key-up + FFSK preamble before
    /// data): on-air start ≈ submit + this. Default 100 ms.</summary>
    public int LeadInMs { get; init; } = 100;

    /// <inheritdoc/>
    public override string DescribeEndpoint() =>
        IsHeadEndBound
            ? $"tait-transparent:{HeadEndId}/{DeviceId}"
            : $"tait-transparent:{(string.IsNullOrWhiteSpace(Device) ? "serial:" + Serial : Device)}";
}

/// <summary>
/// An in-process soundcard modem port (the <c>pdn-soundmodem</c> engine, GPL-3.0-or-later,
/// combined per GPLv3 §13/AGPLv3 §13): the node runs the demodulator/modulator itself over an
/// ALSA device. Native DCD feeds the AX.25 stack's carrier-sense gate, TX-complete is
/// sample-accurate (the transport implements <c>ITxCompletionTransport</c>), and the KISS
/// channel-access parameters drive the modem's own p-persistent CSMA
/// (<c>ICsmaChannelParams</c>).
/// </summary>
public sealed record SoundModemTransportConfig : TransportConfig
{
    /// <inheritdoc/>
    public override string Kind => TransportKinds.SoundModem;

    /// <summary>ALSA device for capture and playback (e.g. <c>default</c>,
    /// <c>plughw:1,0</c>).</summary>
    public string Device { get; init; } = "default";

    /// <summary>Capture sample rate. Card-native 48000 recommended; the modem decimates
    /// with a real anti-aliasing filter. Must be a multiple of the mode's DSP rate
    /// (12000, or 48000 for the 9600 modes).</summary>
    public int CaptureRate { get; init; } = 48000;

    /// <summary>Modem mode: <c>afsk1200</c>, <c>afsk1200-multi</c>, <c>afsk1200-fx25</c>,
    /// <c>afsk1200-fx25rx</c>, <c>bpsk300</c>, <c>bpsk300-nocrc</c>, <c>qpsk2400</c>,
    /// <c>qpsk3600</c>, <c>fsk9600</c>, <c>fsk9600-il2p</c>.</summary>
    public string Mode { get; init; } = "afsk1200";

    /// <summary>Centre/carrier frequency in Hz; 0 = the mode's convention (1700 AFSK,
    /// 1500 BPSK/QPSK-2400, 1650 QPSK-3600; not applicable to 9600 baseband).</summary>
    public double Frequency { get; init; }

    /// <summary>PTT control spec: empty for VOX, <c>serial:/dev/ttyUSB0[:rts|:dtr]</c>,
    /// or <c>cm108:/dev/hidraw0[:gpio]</c>.</summary>
    public string Ptt { get; init; } = "";

    /// <inheritdoc/>
    public override string DescribeEndpoint() => $"soundmodem:{Device}/{Mode}";
}
