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
            var tait = TaitCcdiRadio.Open(radio.Port, radio.Baud, options: null, timeProvider);
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
}
