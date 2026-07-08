using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Api;

/// <summary>
/// The operator's chosen pairing for <c>POST /api/v1/radios/headends/{instanceId}/adopt</c>: a TNC
/// device + a radio device on that head-end to configure as one matched port. Operator-confirmed —
/// the scan <em>offers</em> pairs; this is the explicit "yes, configure it".
/// </summary>
/// <param name="TncDeviceId">The NinoTNC device id on the head-end (its <c>nino-tnc-tcp</c> transport).</param>
/// <param name="RadioDeviceId">The Tait CCDI device id on the head-end (its head-end-bound <c>radio:</c>).</param>
/// <param name="PortId">The id for the new port. Null defaults to the amateur band (when known) or
/// the instance id, uniquified against the existing ports (<c>2m</c> taken ⇒ <c>2m-2</c>).</param>
/// <param name="Mode">NinoTNC modem mode 0..15. Null defaults to 0.</param>
/// <param name="Enabled">Whether the port comes up immediately. Null defaults to true.</param>
/// <param name="Address">Optional manual <c>host:port</c> for the head-end's control plane, stored on
/// the head-end config when the instance isn't already declared. Null/blank leaves the address empty
/// (discover mode — the instance id re-resolves via mDNS at bring-up, surviving a re-address).</param>
/// <param name="AmateurBand">The radio's amateur band (e.g. <c>2m</c>) from the scan, when known.
/// Defaults the new port's MQTT <c>{instance}</c> label (<see cref="Configuration.PortConfig.MqttInstance"/>)
/// and — when <see cref="PortId"/> is unset — the port id, so a band-named port drops in without
/// operator input. Null/blank leaves both to their instance-id defaults.</param>
/// <param name="MqttInstance">An explicit MQTT <c>{instance}</c> label for the new port
/// (<see cref="Configuration.PortConfig.MqttInstance"/>). When set it wins over the
/// <see cref="AmateurBand"/> default (which still names the port id when <see cref="PortId"/> is
/// unset). Null/blank keeps the band default (or leaves the label unset when no band is known).</param>
public sealed record HeadEndAdoptRequest(
    string TncDeviceId,
    string RadioDeviceId,
    string? PortId = null,
    int? Mode = null,
    bool? Enabled = null,
    string? Address = null,
    string? AmateurBand = null,
    string? MqttInstance = null);

/// <summary>
/// Builds the candidate <see cref="NodeConfig"/> for adopting a matched head-end pair — the pure
/// config transform behind the adopt endpoint, kept out of the web layer so it is unit-testable and
/// so the API stays a thin validate→preview→apply over it. It only <em>constructs</em> config; the
/// existing <see cref="NodeConfigValidator"/> (run by the write seam) is what enforces validity
/// (unique port id, declared head-end reference, the co-location TNC↔radio pairing rule).
/// </summary>
public static class HeadEndAdoption
{
    /// <summary>
    /// Return <paramref name="current"/> with (a) a <see cref="HeadEndConfig"/> for
    /// <paramref name="instanceId"/> ensured in <see cref="NodeConfig.HeadEnds"/> (added in discover
    /// mode, or with the request's manual address, only when not already declared), and (b) a new
    /// <see cref="PortConfig"/> whose <c>nino-tnc-tcp</c> transport + head-end-bound <c>tait-ccdi</c>
    /// radio both reference that instance and the chosen device ids. The result is a candidate to run
    /// through the config seam — it is not validated here.
    /// </summary>
    public static NodeConfig BuildCandidate(NodeConfig current, string instanceId, HeadEndAdoptRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(request);

        // Ensure the referenced head-end is declared (a dangling reference is a validation error).
        // Reuse an existing declaration untouched — don't clobber a hand-pinned address; only add a
        // new entry (discover mode, or the request's manual address) when the instance is unknown.
        var headEnds = current.HeadEnds;
        if (!headEnds.Any(h => string.Equals(h.Id, instanceId, StringComparison.Ordinal)))
        {
            headEnds =
            [
                .. headEnds,
                new HeadEndConfig { Id = instanceId, Address = request.Address?.Trim() ?? "" },
            ];
        }

        // The radio's amateur band, when the scan read one, names both the MQTT {instance} label and —
        // absent an explicit port id — the port id itself, so a band-named port ("2m") drops in with no
        // operator input. Falls back to the instance id when the band is unknown. A DEFAULT id that
        // collides with an existing port is uniquified ("2m" taken ⇒ "2m-2", "2m-3", …) so adopting a
        // second same-band pair doesn't 400 on the duplicate-id rule (#586); an EXPLICIT id is
        // honoured verbatim — colliding there is an operator mistake the validator should report.
        var band = string.IsNullOrWhiteSpace(request.AmateurBand) ? null : request.AmateurBand!.Trim();
        var portId = !string.IsNullOrWhiteSpace(request.PortId) ? request.PortId!.Trim()
            : UniquePortId(current.Ports, band ?? instanceId);

        // An explicit MQTT {instance} label wins over the band default (#579) — the band still names
        // the port id above; this only overrides the collector-continuity label.
        var mqttInstance = string.IsNullOrWhiteSpace(request.MqttInstance) ? band : request.MqttInstance!.Trim();

        var port = new PortConfig
        {
            Id = portId,
            Enabled = request.Enabled ?? true,
            MqttInstance = mqttInstance,
            Transport = new NinoTncTcpTransport
            {
                HeadEndId = instanceId,
                DeviceId = request.TncDeviceId,
                Mode = request.Mode ?? 0,
            },
            Radio = new PortRadioConfig
            {
                Kind = RadioKinds.TaitCcdi,
                HeadEndId = instanceId,
                DeviceId = request.RadioDeviceId,
            },
        };

        return current with
        {
            HeadEnds = headEnds,
            Ports = [.. current.Ports, port],
        };
    }

    /// <summary>First free id in <c>baseId</c>, <c>baseId-2</c>, <c>baseId-3</c>, … against the
    /// existing port ids (ordinal, matching the validator's uniqueness rule).</summary>
    private static string UniquePortId(IReadOnlyList<PortConfig> ports, string baseId)
    {
        var taken = new HashSet<string>(ports.Select(p => p.Id), StringComparer.Ordinal);
        if (!taken.Contains(baseId))
        {
            return baseId;
        }
        for (int n = 2; ; n++)
        {
            var candidate = $"{baseId}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
