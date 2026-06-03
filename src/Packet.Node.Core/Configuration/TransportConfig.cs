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
/// AXUDP is intentionally absent from slice 1 — there is no <c>IKissModem</c>
/// AXUDP adapter yet, so the factory throws a clear "unsupported in slice 1"
/// rather than silently doing nothing. The telnet console is <b>not</b> a
/// transport (it is not an <c>IKissModem</c>); it lives under
/// <see cref="ManagementConfig.Telnet"/>.
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
