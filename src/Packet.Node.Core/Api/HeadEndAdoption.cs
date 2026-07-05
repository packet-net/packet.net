using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Api;

/// <summary>
/// The operator's chosen pairing for <c>POST /api/v1/radios/headends/{instanceId}/adopt</c>: a TNC
/// device + a radio device on that head-end to configure as one matched port. Operator-confirmed —
/// the scan <em>offers</em> pairs; this is the explicit "yes, configure it".
/// </summary>
/// <param name="TncDeviceId">The NinoTNC device id on the head-end (its <c>nino-tnc-tcp</c> transport).</param>
/// <param name="RadioDeviceId">The Tait CCDI device id on the head-end (its head-end-bound <c>radio:</c>).</param>
/// <param name="PortId">The id for the new port. Null defaults to the instance id.</param>
/// <param name="Mode">NinoTNC modem mode 0..15. Null defaults to 0.</param>
/// <param name="Enabled">Whether the port comes up immediately. Null defaults to true.</param>
/// <param name="Address">Optional manual <c>host:port</c> for the head-end's control plane, stored on
/// the head-end config when the instance isn't already declared. Null/blank leaves the address empty
/// (discover mode — the instance id re-resolves via mDNS at bring-up, surviving a re-address).</param>
public sealed record HeadEndAdoptRequest(
    string TncDeviceId,
    string RadioDeviceId,
    string? PortId = null,
    int? Mode = null,
    bool? Enabled = null,
    string? Address = null);

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

        var portId = string.IsNullOrWhiteSpace(request.PortId) ? instanceId : request.PortId!.Trim();

        var port = new PortConfig
        {
            Id = portId,
            Enabled = request.Enabled ?? true,
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
}
