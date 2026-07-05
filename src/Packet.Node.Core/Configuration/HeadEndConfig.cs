using FluentValidation;
using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// A first-class <b>head-end</b> the node talks to in the split-station topology (see
/// <c>docs/research/split-station-rf-headend.md</c>): a box (typically a Pi) running the Go
/// head-end daemon that bridges its serial radios/modems as raw TCP pipes and serves an
/// inventory + line-control HTTP API. A port's <c>radio:</c> or <c>nino-tnc-tcp</c> transport
/// binds to a device on one of these by <c>(headEndId, deviceId)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> is the head-end's <b>stable instance id</b> (the daemon's own config value,
/// surfaced in its <c>GET /inventory</c> as <c>instanceId</c>) — not its IP. Keying bindings by the
/// instance id, not <c>host:port</c>, is what lets a Pi reboot onto a new DHCP address without
/// orphaning its port configs: bring-up re-resolves <c>id → current address</c> each time.
/// </para>
/// <para>
/// <see cref="Address"/> is the <c>host:port</c> of that HTTP API — <b>manual</b> in Stage 3a.
/// Stage 3b adds mDNS discovery, which will resolve the instance id to a live address so the manual
/// address becomes an optional fallback (routed / VLAN / Tailscale setups where multicast doesn't
/// cross); that is why the address is validated only <em>when set</em>.
/// </para>
/// </remarks>
public sealed record HeadEndConfig
{
    /// <summary>The head-end's stable instance id (== the daemon's <c>instanceId</c>). The key ports
    /// reference and the value bring-up resolves to a current address. Required + unique across
    /// <see cref="NodeConfig.HeadEnds"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>host:port</c> of the head-end's HTTP control plane (e.g.
    /// <c>192.168.1.10:7300</c> or <c>pi.local:7300</c>) — manual in Stage 3a. An explicit port is
    /// required (the API is not on :80). Empty/absent is tolerated for forward-compatibility with the
    /// Stage-3b mDNS path, but a port that references a head-end with no address cannot come up until
    /// discovery lands.</summary>
    public string Address { get; init; } = "";
}

/// <summary>
/// Validates one <see cref="HeadEndConfig"/>: a non-empty id and — when an address is set — a
/// well-formed <c>host:port</c> (an explicit, in-range port). Cross-entry uniqueness and the
/// port→head-end reference checks live in <see cref="NodeConfigValidator"/> (they need the whole
/// config).
/// </summary>
public sealed class HeadEndConfigValidator : AbstractValidator<HeadEndConfig>
{
    public HeadEndConfigValidator()
    {
        RuleFor(h => h.Id)
            .NotEmpty()
            .WithMessage("headEnd.id is required (it is the head-end's stable instanceId).");

        RuleFor(h => h.Address)
            .Must(a => HeadEndAddress.TryParse(a, out _, out _))
            .When(h => !string.IsNullOrWhiteSpace(h.Address))
            .WithMessage(h =>
                $"headEnd.address '{h.Address}' is not a valid host:port with an explicit port " +
                "(e.g. 192.168.1.10:7300 or pi.local:7300).");
    }
}
