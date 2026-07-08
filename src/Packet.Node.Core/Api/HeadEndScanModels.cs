namespace Packet.Node.Core.Api;

/// <summary>
/// The result of a split-station head-end fleet scan (<c>GET /api/v1/radios/headends</c>): every
/// reachable head-end instance (config-pinned or mDNS-discovered), the devices it bridges with their
/// reach-through identification, the auto-suggested TNC↔radio pairs, and any duplicate-instance-id
/// conflicts mDNS surfaced. This is the "plug in and go" preview the operator confirms before an
/// <c>adopt</c> creates the matched ports. System.Text.Json's web defaults camel-case the members.
/// </summary>
/// <param name="Instances">Each head-end instance found (config ∪ discovery), keyed by instance id.</param>
/// <param name="Conflicts">Instance ids advertised at more than one address with no config address
/// to disambiguate — a loud backstop, never silently bound.</param>
public sealed record HeadEndScan(
    IReadOnlyList<HeadEndInstanceScan> Instances,
    IReadOnlyList<HeadEndConflict> Conflicts);

/// <summary>
/// One head-end instance in a <see cref="HeadEndScan"/>: its stable id + resolved address + how the
/// address was found, whether its inventory could be fetched, the devices it bridges, and the
/// proposed pairings within it (the co-location invariant — a TNC pairs only with a radio on the
/// <b>same</b> instance).
/// </summary>
/// <param name="InstanceId">The head-end's stable instance id (the binding key).</param>
/// <param name="Host">The host PDN dials (config or mDNS).</param>
/// <param name="HttpPort">The head-end HTTP control-plane port.</param>
/// <param name="Source">How the address was resolved: <c>config</c> or <c>mdns</c>.</param>
/// <param name="Reachable">True when the inventory was fetched; false leaves <see cref="Devices"/>
/// empty and sets <see cref="Error"/>.</param>
/// <param name="Error">The failure reason when <see cref="Reachable"/> is false; null otherwise.</param>
/// <param name="Devices">Every bridged device, free (identified) or already bound to a port.</param>
/// <param name="ProposedPairs">Suggested TNC↔radio pairs among the FREE devices.</param>
/// <param name="PairingAmbiguous">True when more than one free TNC or radio makes the pairing a
/// manual choice — the <see cref="ProposedPairs"/> are then candidate combinations, not auto-suggestions.</param>
/// <param name="ReachableNow">The background health poller's live view (#583): whether the
/// instance answered its most recent ~30 s health poll. Null when the
/// <see cref="HeadEnd.HeadEndHealthMonitor"/> has no data yet (not registered, first cycle pending,
/// or the instance isn't configured/referenced) — distinct from <see cref="Reachable"/>, which says
/// whether THIS scan fetched the inventory. Folded in from the in-memory snapshot, never probed on
/// the request path.</param>
/// <param name="LastSeen">When the instance last answered a background health poll, or null when
/// the poller has no data / it never has answered.</param>
public sealed record HeadEndInstanceScan(
    string InstanceId,
    string Host,
    int HttpPort,
    string Source,
    bool Reachable,
    string? Error,
    IReadOnlyList<HeadEndDeviceScan> Devices,
    IReadOnlyList<HeadEndPairProposal> ProposedPairs,
    bool PairingAmbiguous,
    bool? ReachableNow = null,
    DateTimeOffset? LastSeen = null);

/// <summary>
/// One device on a head-end, as seen by the scan: its stable id, its reach-through classification,
/// and whether it is free to adopt. A bound device (already referenced by a configured port) is NOT
/// probed — the head-end is single-client-per-pipe, so identifying it would fight the running port;
/// its <see cref="Kind"/> is inferred from the binding instead.
/// </summary>
/// <param name="DeviceId">The stable device id (the inventory <c>id</c>) a port binds to.</param>
/// <param name="Kind">The classification: <see cref="HeadEndDeviceKind.NinoTnc"/>,
/// <see cref="HeadEndDeviceKind.TaitCcdi"/>, or <see cref="HeadEndDeviceKind.Unknown"/>.</param>
/// <param name="Model">Friendly product name (Tait), or null.</param>
/// <param name="Version">The reported version — NinoTNC GETVER firmware, or the Tait CCDI version.</param>
/// <param name="Serial">The Tait CCDI serial number, or null.</param>
/// <param name="Baud">The rate the device answered at (the baud the sweep locked, for a Tait; the
/// inventory baud for a NinoTNC — CDC-ACM baud is fictional).</param>
/// <param name="Free">True when the device is unbound and was identified; false when already bound
/// to a configured port (then <see cref="Kind"/> comes from the binding, not a probe).</param>
/// <param name="BandCode">The Tait band designator (e.g. <c>B1</c>) read from the radio's product
/// code, or null for a NinoTNC / an unknown-band Tait. The tuned frequency is not CCDI-readable, but
/// the band split is.</param>
/// <param name="AmateurBand">The UK amateur band the Tait's split covers (<c>2m</c> / <c>70cm</c> /
/// <c>4m</c>), or null for a NinoTNC / a Tait whose split has no amateur allocation. Adopt defaults a
/// port's MQTT <c>{instance}</c> label and id to this when known.</param>
/// <param name="IdSource">Which link the head-end derived <see cref="DeviceId"/> from —
/// <c>by-path</c> (stable) or <c>dev</c> (unstable last resort). Null when the head-end predates the
/// id-stability fields (&lt; headend-v0.1.3): unknown (see <see cref="HeadEnd.HeadEndPortInfo.IdSource"/>).</param>
/// <param name="IdStable">Whether <see cref="DeviceId"/> survives reboot / same-socket replug —
/// <c>false</c> means a binding to it may not survive a replug (the UI warns). Null = the head-end
/// didn't report it (unknown, deliberately not assumed stable).</param>
public sealed record HeadEndDeviceScan(
    string DeviceId,
    string Kind,
    string? Model,
    string? Version,
    string? Serial,
    int Baud,
    bool Free,
    string? BandCode = null,
    string? AmateurBand = null,
    string? IdSource = null,
    bool? IdStable = null);

/// <summary>A suggested pairing within one instance: a TNC device + a radio device to configure as
/// one matched port (a <c>nino-tnc-tcp</c> transport + a head-end-bound <c>tait-ccdi</c> radio).
/// <see cref="Auto"/> is true only when the instance has exactly one free TNC and one free radio (an
/// unambiguous suggestion); otherwise it is one of several candidate combinations for manual choice.</summary>
public sealed record HeadEndPairProposal(string TncDeviceId, string RadioDeviceId, bool Auto);

/// <summary>An instance id advertised at more than one address with no config address to
/// disambiguate — mDNS does not police its TXT payloads, so PDN surfaces the clash loudly rather
/// than binding to whichever answered.</summary>
public sealed record HeadEndConflict(string InstanceId, IReadOnlyList<string> Addresses);

/// <summary>The device-classification strings a reach-through identify produces.</summary>
public static class HeadEndDeviceKind
{
    /// <summary>A NinoTNC (answered GETVER).</summary>
    public const string NinoTnc = "nino-tnc";

    /// <summary>A Tait CCDI radio (answered MODEL).</summary>
    public const string TaitCcdi = "tait-ccdi";

    /// <summary>Neither probe classified the device (or it was unreachable).</summary>
    public const string Unknown = "unknown";
}
