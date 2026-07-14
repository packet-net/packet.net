using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Rigs;
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
/// RSSI-tagging transport uses for per-burst frame attribution. <c>rig</c> → a
/// SECOND, dedicated connection to the port's <c>rig:</c> CAT daemon (via the
/// <see cref="IRigControlFactory"/> collaborator) wrapped in an owning
/// <see cref="RigRadioControl"/> — dedicated so the carrier-sense poll never queues
/// behind the rig status poller's meter reads on the shared connection. A kind this
/// build doesn't implement throws <see cref="NotSupportedException"/> (unreachable
/// for validated config — the validator shares <see cref="RadioKinds"/>).
/// </remarks>
public sealed class RadioControlFactory : IRadioControlFactory
{
    private readonly IRigControlFactory rigFactory;

    /// <summary>A shared default instance (production collaborators throughout).</summary>
    public static RadioControlFactory Instance { get; } = new();

    /// <summary>
    /// Create the factory. <paramref name="rigFactory"/> is how the kind-<c>rig</c> arm dials
    /// its dedicated rig connection — pass the DI-registered seam so component tests that
    /// substitute a scripted rig factory script the radio arm too; null uses the production
    /// <see cref="RigControlFactory.Instance"/>.
    /// </summary>
    public RadioControlFactory(IRigControlFactory? rigFactory = null)
    {
        this.rigFactory = rigFactory ?? RigControlFactory.Instance;
    }

    /// <inheritdoc/>
    public async Task<IRadioControl> CreateAsync(
        PortRadioConfig radio,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        PortRigConfig? rig = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radio);

        if (RadioKinds.Is(radio.Kind, RadioKinds.TaitCcdi))
        {
            var tait = radio.IsHeadEndBound
                ? await OpenHeadEndTaitAsync(radio, headEndResolver, timeProvider, cancellationToken).ConfigureAwait(false)
                : await OpenLocalTaitAsync(radio, timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                // DCD (carrier-sense) events only flow while unsolicited PROGRESS
                // output is on — without it the tagging transport degrades to
                // threshold-over-noise-floor attribution. Identical for the local and
                // head-end paths: the CCDI/PROGRESS stream rides the socket unchanged.
                await tait.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await tait.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return tait;
        }

        if (RadioKinds.Is(radio.Kind, RadioKinds.Rig))
        {
            if (rig is null)
            {
                throw new InvalidOperationException(
                    "radio kind 'rig' needs the port's rig: block (the CAT daemon its dedicated " +
                    "connection dials), but none was supplied — validated config always carries " +
                    "one, so this is a wiring bug at the call site.");
            }
            var rigControl = await rigFactory.CreateAsync(rig, timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                // ownsRig: the adapter's disposal disposes this dedicated connection — the rig
                // status poller's own connection (dialled separately by the supervisor's rig
                // arm) is untouched, which is the whole point of dialling twice.
                return new RigRadioControl(rigControl, options: null, timeProvider, ownsRig: true);
            }
            catch
            {
                // The bridge rejected the rig (it advertises nothing the packet-medium seam can
                // use) — don't leak the just-dialled connection.
                await rigControl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new NotSupportedException(
            $"radio kind '{radio.Kind}' has no IRadioControl implementation in this build " +
            $"(expected one of: {string.Join(", ", RadioKinds.Names)}).");
    }

    /// <summary>Open a locally-cabled Tait radio: resolve its <c>(device path, baud)</c> (an explicit
    /// <c>port</c>, or a scan for the <c>serial</c>-bound radio's CCDI serial) and open the serial
    /// port — exactly today's behaviour.</summary>
    private static async Task<TaitCcdiRadio> OpenLocalTaitAsync(
        PortRadioConfig radio, TimeProvider? timeProvider, CancellationToken cancellationToken)
    {
        var (devicePath, baud) = await ResolveTaitEndpointAsync(radio, cancellationToken).ConfigureAwait(false);
        return TaitCcdiRadio.Open(devicePath, baud, options: null, timeProvider);
    }

    /// <summary>
    /// Open a head-end-hosted Tait radio (split-station topology): resolve <c>(headEndId, deviceId)</c>
    /// to the head-end's raw TCP pipe + baud via the inventory, then <c>TaitCcdiRadio.OpenTcp</c> with
    /// <c>setBaud</c> wired to the head-end's line-control verb — so the whole CCDI control stack
    /// (RSSI/SNR, DCD, tuning, SDM) runs over the socket exactly as it does locally. A null resolver
    /// for a head-end-bound radio is a wiring bug, surfaced as a clear failure (the supervisor
    /// degrades the radio as it would any open failure).
    /// </summary>
    private static async Task<TaitCcdiRadio> OpenHeadEndTaitAsync(
        PortRadioConfig radio, HeadEndDeviceResolver? headEndResolver,
        TimeProvider? timeProvider, CancellationToken cancellationToken)
    {
        var resolver = headEndResolver
            ?? throw new InvalidOperationException(
                $"radio for head-end '{radio.HeadEndId}' device '{radio.DeviceId}' needs a head-end resolver, " +
                "but none was supplied.");

        var binding = await resolver.ResolveAsync(radio.HeadEndId, radio.DeviceId, cancellationToken).ConfigureAwait(false);
        // The CONFIGURED CCDI rate, not the head-end's current line rate (#576): the open-time
        // setBaud must genuinely re-clock a restarted head-end (whose bridge reopens at its
        // default) back to the rate the radio is programmed for. Passing binding.Baud here would
        // "re-clock" the port to the rate it is already at — every head-end restart would then
        // fail against the radio until an operator re-ran a scan or POSTed the line verb.
        return await TaitCcdiRadio.OpenTcp(
            binding.Host, binding.TcpPort, radio.Baud,
            setBaud: binding.SetBaud, options: null, timeProvider, cancellationToken).ConfigureAwait(false);
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
