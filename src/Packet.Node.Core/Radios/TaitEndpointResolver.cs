using Packet.Node.Core.Configuration;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Resolves a locally-cabled <c>tait-ccdi</c> radio's config to the concrete <c>(device path,
/// baud)</c> to open. A <c>port</c>-bound radio resolves to itself; a <c>serial</c>-bound radio is
/// located by scanning the machine's candidate ports (at the configured baud) for its CCDI serial
/// number - so a re-enumerated <c>/dev/ttyUSBn</c>, or two dongles that swapped numbers, still
/// resolves to the right physical radio.
/// </summary>
/// <remarks>
/// Shared by the port supervisor's radio bring-up (<see cref="RadioControlFactory"/>) and the
/// codeplug-programming panel (<c>Radios/Programming</c>), which needs the same answer to know
/// which serial device to drive the programming handshake over. A scan opens candidate ports, so it
/// only ever finds a radio whose device is free - which for the programming path means it must run
/// with the port already down.
/// </remarks>
public static class TaitEndpointResolver
{
    /// <summary>
    /// Resolve <paramref name="radio"/> to the device path and baud to open.
    /// </summary>
    /// <param name="radio">A locally-cabled tait-ccdi radio block (bound by serial or by path).</param>
    /// <param name="cancellationToken">Abandons the scan.</param>
    /// <exception cref="InvalidOperationException">A serial-bound radio with no plugged-in match.
    /// The caller decides what that means: the supervisor degrades the port, the programming panel
    /// reports it to the operator.</exception>
    public static async Task<(string Port, int Baud)> ResolveAsync(
        PortRadioConfig radio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radio);

        if (string.IsNullOrWhiteSpace(radio.Serial))
        {
            return (radio.Port, radio.Baud);
        }

        var found = new List<TaitDiscoveredRadio>();
        await foreach (var candidate in TaitRadioPortDiscovery
                           .DiscoverAsync([radio.Baud], cancellationToken).ConfigureAwait(false))
        {
            found.Add(candidate);
        }

        if (RadioSerialResolver.Match(found, radio.Serial) is { } match)
        {
            return (match.Port, match.BaudRate);
        }

        throw new InvalidOperationException(
            $"no tait-ccdi radio with CCDI serial '{radio.Serial}' found among {found.Count} " +
            $"probed port(s) at {radio.Baud} baud - is it plugged in and powered?");
    }
}
