using System.Net;
using System.Net.Sockets;
using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Kiss.Serial;
using Packet.Node.Core.Configuration;

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

            default:
                throw new NotSupportedException(
                    $"transport kind '{transport.Kind}' has no IAx25Transport implementation in this build.");
        }
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
