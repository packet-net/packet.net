namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// Resolves a head-end's stable instance id to the current base <see cref="Uri"/> of its HTTP
/// control plane, preferring a manually-configured <see cref="Configuration.HeadEndConfig.Address"/>
/// and falling back to mDNS discovery when the config address is blank. Keying by instance id (not
/// <c>host:port</c>) is what lets a re-addressed head-end — a Pi that rebooted onto a new DHCP lease
/// — keep its port configs: bring-up re-resolves the id to its current address every time.
/// </summary>
/// <remarks>
/// The backstop for mDNS's inability to police its own TXT payloads (see
/// <c>docs/research/split-station-rf-headend.md § Multiple instances</c>): when the config pins no
/// address and discovery returns the <b>same</b> instance id at two or more distinct addresses, the
/// resolver refuses to guess — it returns <c>null</c> (and logs an error) rather than silently bind
/// to whichever answered first.
/// </remarks>
public interface IHeadEndAddressResolver
{
    /// <summary>Resolve <paramref name="headEndId"/> to its current HTTP base <see cref="Uri"/>
    /// (<c>http://host:port/</c>), or <c>null</c> when neither config nor an unambiguous mDNS hit
    /// yields an address (unknown id, blank config address with no discovery, or a duplicate-id
    /// discovery conflict).</summary>
    Task<Uri?> ResolveBaseAddressAsync(string headEndId, CancellationToken cancellationToken = default);
}
