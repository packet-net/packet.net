using Packet.Node.Core.Configuration;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The production <see cref="IRadioControlFactory"/>: maps each radio-control
/// <c>kind</c> onto its concrete <see cref="IRadioControl"/> driver.
/// </summary>
/// <remarks>
/// <c>tait-ccdi</c> → <see cref="TaitCcdiRadio.Open"/> +
/// <see cref="TaitCcdiRadio.SetProgressMessagesAsync"/>(true) — unsolicited PROGRESS
/// output is what makes the driver's carrier-sense (DCD) events fire, which the
/// RSSI-tagging transport uses for per-burst frame attribution. A kind this build
/// doesn't implement throws <see cref="NotSupportedException"/> (unreachable for
/// validated config — the validator shares <see cref="RadioKinds"/>).
/// </remarks>
public sealed class RadioControlFactory : IRadioControlFactory
{
    /// <summary>A shared default instance (the factory holds no state).</summary>
    public static RadioControlFactory Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<IRadioControl> CreateAsync(
        PortRadioConfig radio,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radio);

        if (RadioKinds.Is(radio.Kind, RadioKinds.TaitCcdi))
        {
            // Resolve which device path to open: an explicit `port`, or — for a `serial`-bound
            // radio — scan and find whichever /dev/tty* the radio with that CCDI serial is on right
            // now (survives device-path renumbering + shared-USB-serial dongles). No plugged-in
            // match throws; PortSupervisor treats that as the same clean radio-open degrade.
            var (devicePath, baud) = await ResolveTaitEndpointAsync(radio, cancellationToken).ConfigureAwait(false);
            var tait = TaitCcdiRadio.Open(devicePath, baud, options: null, timeProvider);
            try
            {
                // DCD (carrier-sense) events only flow while unsolicited PROGRESS
                // output is on — without it the tagging transport degrades to
                // threshold-over-noise-floor attribution.
                await tait.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await tait.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return tait;
        }

        throw new NotSupportedException(
            $"radio kind '{radio.Kind}' has no IRadioControl implementation in this build " +
            $"(expected one of: {string.Join(", ", RadioKinds.Names)}).");
    }

    /// <summary>
    /// Resolve a Tait radio's config to the concrete <c>(device path, baud)</c> to open. A
    /// <c>port</c>-bound radio resolves to itself; a <c>serial</c>-bound radio is located by scanning
    /// the machine's candidate ports (at the configured baud) for the CCDI serial number — so a
    /// re-enumerated <c>/dev/ttyUSBn</c>, or two dongles that swapped numbers, still resolves to the
    /// right physical radio. A serial with no plugged-in match throws
    /// <see cref="InvalidOperationException"/>; the caller (the port supervisor) treats that as the
    /// same clean radio-open failure as an unplugged control cable and runs the port without radio
    /// metadata.
    /// </summary>
    private static async Task<(string Port, int Baud)> ResolveTaitEndpointAsync(
        PortRadioConfig radio, CancellationToken cancellationToken)
    {
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
            $"probed port(s) at {radio.Baud} baud — is it plugged in and powered?");
    }
}
