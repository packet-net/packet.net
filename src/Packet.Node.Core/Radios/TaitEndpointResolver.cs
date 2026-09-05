using Packet.Node.Core.Configuration;
using M0LTE.Radio.Tait;

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

        var match = await FindBySerialAsync(RadioKinds.TaitCcdi, radio.Serial, radio.Baud, cancellationToken)
            .ConfigureAwait(false);
        return (match.Port, match.BaudRate);
    }

    /// <summary>
    /// Find the plugged-in Tait whose CCDI serial is <paramref name="serial"/>, by probing the
    /// machine's candidate serial ports at <paramref name="baud"/>. Shared by the radio-attach path
    /// and the <c>tait-transparent</c> transport, which ask the same question of the same hardware.
    /// </summary>
    /// <param name="kind">What is being looked for, for the failure message (the config
    /// <c>kind</c>: <c>tait-ccdi</c> or <c>tait-transparent</c>).</param>
    /// <param name="serial">The CCDI serial number to match.</param>
    /// <param name="baud">The control baud to probe at.</param>
    /// <param name="cancellationToken">Abandons the scan.</param>
    /// <exception cref="InvalidOperationException">No plugged-in radio carries that serial.</exception>
    public static async Task<TaitDiscoveredRadio> FindBySerialAsync(
        string kind, string serial, int baud, CancellationToken cancellationToken = default)
    {
        // Probed one at a time, stopping at the match: the wanted radio is usually the first
        // candidate, and every port past it costs a probe timeout for nothing.
        var candidates = TaitRadioPortDiscovery.EnumerateCandidatePorts();
        var others = new List<TaitDiscoveredRadio>();
        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TaitRadioPortDiscovery.ProbeAsync(candidate, baud, cancellationToken)
                    .ConfigureAwait(false) is not { } found)
            {
                continue;
            }

            if (RadioSerialResolver.Match([found], serial) is { } match)
            {
                return match;
            }

            others.Add(found);
        }

        // The failure an operator reads at 2am, so it says what was actually looked at rather than
        // one bare count: which devices were probed, and any OTHER Tait that answered - "the radio
        // is there but its serial is not the one in the config" and "nothing answered at all" are
        // different problems with different fixes.
        throw new InvalidOperationException(
            $"no {kind} radio with CCDI serial '{serial}' answered at {baud} baud. " +
            Describe(candidates, others) +
            " Is it plugged in, powered, and finished restarting? A radio reboots after a codeplug " +
            "write and stays silent for a few seconds.");
    }

    private static string Describe(IReadOnlyList<string> candidates, List<TaitDiscoveredRadio> others)
    {
        if (candidates.Count == 0)
        {
            return "There were no candidate serial ports to probe at all (no /dev/ttyUSB*), so no " +
                   "CCDI dongle is enumerated on this machine.";
        }

        string probed = $"Probed {candidates.Count} port(s): {string.Join(", ", candidates)}.";
        if (others.Count == 0)
        {
            return $"{probed} Nothing on any of them answered a CCDI identity query.";
        }

        string found = string.Join(
            ", ", others.Select(o => $"s/n {o.Identity.SerialNumber ?? "(unreported)"} on {o.Port}"));
        return $"{probed} A Tait DID answer, but not that one: {found}.";
    }
}
