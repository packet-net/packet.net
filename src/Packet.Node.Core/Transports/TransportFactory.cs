using System.Net;
using System.Net.Sockets;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Kiss.Serial;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Transports;

/// <summary>
/// The slice-1 <see cref="ITransportFactory"/>: maps each
/// <see cref="TransportConfig"/> arm onto its concrete IAx25Transport.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="SerialKissTransport"/> → <c>KissSerialModem.Open</c>.</item>
/// <item><see cref="NinoTncTransport"/> → <c>NinoTncSerialPort.Open</c> +
/// <c>SetModeAsync</c>.</item>
/// <item><see cref="KissTcpTransport"/> → <c>KissTcpClient.ConnectAsync</c> — a
/// softmodem / net-sim (the software-RF channel).</item>
/// <item><see cref="AxudpTransport"/> → <c>AxudpFrameTransport</c> over a
/// <c>Packet.Axudp.AxudpSocket</c> (AX.25 frames over UDP — the BPQAXIP tunnel).</item>
/// </list>
/// <para>
/// The closed union means the switch is exhaustive over the known kinds; the
/// default arm throws <see cref="NotSupportedException"/> with a clear message,
/// guarding against a future subtype added without a factory arm.
/// </para>
/// </remarks>
public sealed class TransportFactory : ITransportFactory
{
    /// <summary>A shared default instance (the factory holds no state).</summary>
    public static TransportFactory Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<IAx25Transport> CreateAsync(
        TransportConfig transport,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        switch (transport)
        {
            case SerialKissTransport s:
                // Native IAx25Transport (no ACKMODE — no ITxCompletionTransport).
                return KissSerialModem.Open(s.Device, s.Baud, timeProvider);

            case NinoTncTransport n:
                {
                    var tnc = NinoTncSerialPort.Open(n.Device, n.Baud, timeProvider);
                    try
                    {
                        await tnc.SetModeAsync((byte)n.Mode, persistToFlash: false, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        await tnc.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    return tnc;
                }

            case NinoTncTcpTransport nt:
                {
                    // Full-control NinoTNC over a split-station head-end's raw TCP pipe: resolve
                    // (headEndId, deviceId) → host:tcpPort via the inventory, open, then apply the
                    // configured mode — the whole NinoTNC surface (GETVER / mode / GETRSSI / ACKMODE)
                    // works remotely, distinct from the control-less kiss-tcp arm. A NinoTNC's KISS
                    // baud is a fixed 57600 (never changes), so clock the head-end line to it via the
                    // line verb before opening the pipe (#567) — the raw socket cannot carry line rate.
                    var resolver = headEndResolver
                        ?? throw new InvalidOperationException(
                            $"nino-tnc-tcp transport for head-end '{nt.HeadEndId}' device '{nt.DeviceId}' needs a " +
                            "head-end resolver, but none was supplied.");
                    var binding = await resolver.ResolveAsync(nt.HeadEndId, nt.DeviceId, cancellationToken).ConfigureAwait(false);
                    await binding.SetBaud(HeadEndRadioScanner.NinoTncKissBaud, cancellationToken).ConfigureAwait(false);
                    var tnc = await NinoTncSerialPort.OpenTcp(binding.Host, binding.TcpPort, timeProvider, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await tnc.SetModeAsync((byte)nt.Mode, persistToFlash: false, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        await tnc.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    return tnc;
                }

            case KissTcpTransport k:
                // Native IAx25Transport. The read-idle liveness timeout converts a
                // half-open TCP drop (peer rebooted with no FIN) into an end-of-stream
                // so the port self-heals via ReconnectingKissModem instead of hanging (#464).
                return await KissTcpClient.ConnectAsync(
                    k.Host, k.Port,
                    readIdleTimeout: KissTcpClient.DefaultReadIdleTimeout,
                    timeProvider: timeProvider,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            case AxudpTransport a:
                {
                    // AXUDP is a native IAx25Transport — no KISS, no synthesis, no CSMA/ACKMODE
                    // capabilities (a UDP link has none). Returned directly.
                    var remote = await ResolveAsync(a.Host, a.Port, cancellationToken).ConfigureAwait(false);
                    return new AxudpFrameTransport(remote, a.LocalPort, timeProvider);
                }

            case AxudpMultipointTransport m:
                {
                    // Multipoint AXUDP (the BPQAXIP analog): one socket, many callsign-mapped
                    // peers. Resolve each peer's host (the factory owns DNS) into the already-
                    // resolved endpoint form the transport routes against, then construct the
                    // native IAx25Transport. Validation has already guaranteed unique, valid
                    // callsigns + in-range ports, so the parse below cannot fail.
                    var peers = new List<AxudpMultipointPeerEndpoint>(m.Peers.Count);
                    foreach (var peer in m.Peers)
                    {
                        var endpoint = await ResolveAsync(peer.Host, peer.Port, cancellationToken).ConfigureAwait(false);
                        var call = Callsign.Parse(peer.Call);
                        peers.Add(new AxudpMultipointPeerEndpoint(call, endpoint, peer.Broadcast));
                    }
                    return new AxudpMultipointFrameTransport(peers, m.LocalPort, timeProvider, logger: null);
                }

            case TaitTransparentTransportConfig t:
                {
                    // The radio IS the modem: open it, enter Transparent mode, and frame AX.25
                    // over its FFSK byte pipe with KISS SLIP. Bind by serial (scanned) or device
                    // path — same stable-identity resolution as the radio-attach path.
                    var device = await ResolveTaitTransparentDeviceAsync(t, cancellationToken).ConfigureAwait(false);
                    var opts = new TaitTransparentTransportOptions
                    {
                        CommandBaud = t.Baud,
                        TransparentBaud = t.TransparentBaud,
                        FfskBaud = t.FfskBaud,
                        LeadIn = TimeSpan.FromMilliseconds(t.LeadInMs),
                    };
                    return await TaitTransparentTransport
                        .OpenAsync(device, opts, timeProvider, cancellationToken).ConfigureAwait(false);
                }

            default:
                throw new NotSupportedException(
                    $"transport kind '{transport.Kind}' has no IAx25Transport implementation in this build.");
        }
    }

    /// <summary>
    /// Resolve a tait-transparent transport's config to the device path to open: a
    /// <c>device</c>-bound radio resolves to itself; a <c>serial</c>-bound radio is located by
    /// scanning candidate ports (at the configured command baud) for the CCDI serial number
    /// (shared with the radio-attach path via <see cref="RadioSerialResolver"/>), so a
    /// re-enumerated <c>/dev/ttyUSB*</c> still resolves to the right physical radio. A serial with
    /// no plugged-in match throws — the port supervisor logs it and the port stays down, exactly
    /// like any other transport open failure.
    /// </summary>
    private static async Task<string> ResolveTaitTransparentDeviceAsync(
        TaitTransparentTransportConfig t, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(t.Serial))
        {
            return t.Device;
        }

        var found = new List<TaitDiscoveredRadio>();
        await foreach (var candidate in TaitRadioPortDiscovery
                           .DiscoverAsync([t.Baud], cancellationToken).ConfigureAwait(false))
        {
            found.Add(candidate);
        }

        if (RadioSerialResolver.Match(found, t.Serial) is { } match)
        {
            return match.Port;
        }

        throw new InvalidOperationException(
            $"no tait-transparent radio with CCDI serial '{t.Serial}' found among {found.Count} " +
            $"probed port(s) at {t.Baud} baud — is it plugged in and powered?");
    }

    // Resolve a host:port to an IPEndPoint. A literal IP short-circuits DNS;
    // a name resolves to its first address (IPv4 preferred for AXUDP, which is
    // overwhelmingly v4 in the field). Throws if the name doesn't resolve.
    private static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new IPEndPoint(literal, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var address =
            Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? (addresses.Length > 0 ? addresses[0] : null)
            ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(address, port);
    }
}
