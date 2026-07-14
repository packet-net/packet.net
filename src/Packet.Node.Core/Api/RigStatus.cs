using Packet.Rig;

namespace Packet.Node.Core.Api;

/// <summary>
/// The live status of one port's rig-control (CAT) attachment — the read model behind
/// <c>GET /api/v1/rigs</c>, <c>GET /api/v1/ports/{id}/rig</c>, and the <c>event: rig</c> SSE
/// stream. Present for every port that has a <c>rig:</c> block: <see cref="Attached"/>
/// distinguishes a rig the node connected to and is polling (<c>true</c>) from one that is
/// configured but not currently attached (<c>false</c> — the port isn't running, or the daemon
/// was unreachable at bring-up and the port degraded to running without it). System.Text.Json's
/// web defaults camel-case the properties.
/// </summary>
/// <param name="PortId">The node port this rig is (or would be) attached to.</param>
/// <param name="Attached"><c>true</c> when the rig backend is connected and being polled;
/// <c>false</c> for a configured-but-not-attached rig (degraded or port down).</param>
/// <param name="Kind">The rig-control kind — <c>hamlib</c> or <c>flrig</c>.</param>
/// <param name="Endpoint">The daemon endpoint as <c>host:port</c> (config defaults resolved).</param>
/// <param name="Backend">What the backend calls itself (e.g. <c>Hamlib rigctld</c>, <c>flrig</c>),
/// or <c>null</c> when not attached.</param>
/// <param name="Manufacturer">Rig manufacturer as the backend reports it, when known.</param>
/// <param name="Model">Rig model as the backend reports it, when known.</param>
/// <param name="Capabilities">The capability slice this rig actually advertises, as camel-cased
/// <c>Packet.Rig.RigCapabilities</c> flag names (<c>frequencyGet</c>, <c>swrMeter</c>, …) — the
/// contract for what the UI should render. Empty when not attached.</param>
/// <param name="ConnectionState">The control-link health: <c>healthy</c> (the last poll tick
/// answered), <c>faulted</c> (the daemon stopped answering — the backend re-dials on each poll, so
/// this self-heals), or <c>unknown</c> (not attached / no tick yet).</param>
/// <param name="FrequencyHz">Current-VFO frequency in Hz, or <c>null</c> (capability absent, read
/// failed, or no tick yet).</param>
/// <param name="Mode">Current operating mode token (hamlib vocabulary or the rig's native name —
/// e.g. <c>USB</c>, <c>PKTUSB</c>, <c>DATA-U</c>), or <c>null</c>.</param>
/// <param name="PassbandHz">Receiver passband width in Hz where the backend reports one
/// (hamlib does; flrig doesn't), or <c>null</c>.</param>
/// <param name="Transmitting">Last observed PTT state, or <c>null</c> when unreadable.</param>
/// <param name="Meters">The latest TX-side meter sample, or <c>null</c> when no meter capability
/// or none taken yet. Meters are polled fast only while <see cref="Transmitting"/> — idle they
/// read ~0 and are left alone.</param>
/// <param name="SampledAt">When the last successful poll tick completed, or <c>null</c> before
/// the first.</param>
public sealed record RigStatus(
    string PortId,
    bool Attached,
    string Kind,
    string Endpoint,
    string? Backend,
    string? Manufacturer,
    string? Model,
    IReadOnlyList<string> Capabilities,
    string ConnectionState,
    long? FrequencyHz,
    string? Mode,
    int? PassbandHz,
    bool? Transmitting,
    RigMeters? Meters,
    DateTimeOffset? SampledAt)
{
    /// <summary>Project <paramref name="capabilities"/> as camel-cased flag names —
    /// <c>FrequencyGet|SwrMeter</c> → <c>["frequencyGet", "swrMeter"]</c>.</summary>
    public static IReadOnlyList<string> CapabilityNames(RigCapabilities capabilities)
    {
        var names = new List<string>();
        foreach (RigCapabilities flag in Enum.GetValues<RigCapabilities>())
        {
            if (flag != RigCapabilities.None && capabilities.HasFlag(flag))
            {
                var name = flag.ToString();
                names.Add(char.ToLowerInvariant(name[0]) + name[1..]);
            }
        }
        return names;
    }
}

/// <summary>
/// The latest TX-side meter sample from a rig. Meaningful while transmitting — an idle rig reads
/// ~0 on all of these, which is why the monitor only samples them fast during PTT.
/// </summary>
/// <param name="Swr">SWR as a dimensionless ratio (1.0 = perfect match), or <c>null</c> when the
/// rig has no SWR meter capability or the read failed.</param>
/// <param name="RfPowerWatts">RF power output in watts (calibrated backends), or <c>null</c>.</param>
/// <param name="RfPowerRelative">RF power output as a fraction of full scale (0–1), or
/// <c>null</c>.</param>
/// <param name="SampleAt">When this meter sample was taken.</param>
public sealed record RigMeters(
    double? Swr,
    double? RfPowerWatts,
    double? RfPowerRelative,
    DateTimeOffset SampleAt);
