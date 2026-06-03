using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Kiss.Serial;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Transports;

/// <summary>
/// The slice-1 <see cref="ITransportFactory"/>: maps each
/// <see cref="TransportConfig"/> arm onto its concrete <see cref="IKissModem"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="SerialKissTransport"/> → <c>KissSerialModem.Open</c>.</item>
/// <item><see cref="NinoTncTransport"/> → <c>NinoTncSerialPort.Open</c> +
/// <c>SetModeAsync</c>.</item>
/// <item><see cref="KissTcpTransport"/> → <c>KissTcpClient.ConnectAsync</c> — a
/// softmodem / net-sim (the software-RF channel).</item>
/// </list>
/// <para>
/// AXUDP is not in slice 1 (no <c>IKissModem</c> AXUDP adapter exists), so a
/// transport the factory does not recognise throws
/// <see cref="NotSupportedException"/> with a clear message rather than failing
/// obscurely. The closed union means the switch is exhaustive over the known
/// kinds; the default arm guards against a future subtype added without a
/// factory arm.
/// </para>
/// </remarks>
public sealed class TransportFactory : ITransportFactory
{
    /// <summary>A shared default instance (the factory holds no state).</summary>
    public static TransportFactory Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<IKissModem> CreateAsync(TransportConfig transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        switch (transport)
        {
            case SerialKissTransport s:
                return KissSerialModem.Open(s.Device, s.Baud);

            case NinoTncTransport n:
            {
                var tnc = NinoTncSerialPort.Open(n.Device, n.Baud);
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
                return await KissTcpClient.ConnectAsync(k.Host, k.Port, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException(
                    $"transport kind '{transport.Kind}' is not supported in this build. " +
                    "(AXUDP and other non-KISS transports are not available in slice 1.)");
        }
    }
}
