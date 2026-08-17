namespace Packet.Node.Core.Hosting;

/// <summary>
/// What <see cref="PortSupervisor.ApplyAsync"/> did with a <see cref="ReconcilePlan"/>: it either
/// applied it, or it <b>refused</b> the whole thing because the candidate config collides with
/// live state the config store cannot see (packet-net/packet.net#723 item 2 - today, an
/// <c>identity.callsign</c> a running application has already bound).
/// </summary>
/// <remarks>
/// A refusal is all-or-nothing on purpose. The alternative - apply the ports and skip the
/// colliding alias - leaves the node half-way between two identities, which is exactly the
/// silent-takeover failure the refusal exists to prevent. The caller (<c>NodeHostedService</c>)
/// leaves its applied-config baseline where it was, so the next config change re-plans from what
/// is actually running rather than from a config that never took effect.
/// </remarks>
/// <param name="Refusals">Operator-facing reasons the apply was refused; empty when
/// <see cref="Applied"/>.</param>
public sealed record PortApplyOutcome(IReadOnlyList<string> Refusals)
{
    /// <summary>The plan was executed.</summary>
    public static PortApplyOutcome Applied { get; } = new([]);

    /// <summary>The plan was refused; nothing was touched.</summary>
    public static PortApplyOutcome Refused(IReadOnlyList<string> reasons) => new(reasons);

    /// <summary>True when nothing was applied.</summary>
    public bool WasRefused => Refusals.Count > 0;
}
