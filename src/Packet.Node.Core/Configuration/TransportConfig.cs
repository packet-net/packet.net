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
/// same seam via the <c>AxudpKissModem</c> adapter, which presents a
/// <see cref="Packet.Axudp.AxudpSocket"/> as an <c>IKissModem</c> — so the
/// listener / console / reconcile path is shared with the KISS transports. The
/// telnet console is <b>not</b> a transport (it is not an <c>IKissModem</c>); it
/// lives under <see cref="ManagementConfig.Telnet"/>.
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

    /// <summary>KISS over TCP (a softmodem / net-sim endpoint).</summary>
    public const string KissTcp = "kiss-tcp";

    /// <summary>AX.25 frames encapsulated in UDP datagrams (AXUDP / BPQAXIP).</summary>
    public const string Axudp = "axudp";
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
/// <c>docs/strict-vs-pragmatic-audit.md</c>. Driven by the <c>AxudpKissModem</c>
/// adapter over a <see cref="Packet.Axudp.AxudpSocket"/>.
/// </summary>
/// <remarks>
/// AXUDP is a point-to-point UDP tunnel, not a shared RF channel: a port sends
/// every outbound frame to one configured remote (<see cref="Host"/>:<see cref="Port"/>)
/// and receives on its own bound <see cref="LocalPort"/>. There is no CSMA on a
/// UDP link, so the KISS TXDELAY/PERSIST/SLOTTIME knobs are inert for this kind
/// (the adapter accepts and ignores them — see <c>AxudpKissModem</c>).
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
